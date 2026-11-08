using System.Collections.Concurrent;
using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;
using Lock = System.Threading.Lock;
using ItemsByChars = System.Collections.Concurrent.ConcurrentDictionary<string, byte>.AlternateLookup<System.ReadOnlySpan<char>>;

namespace SearchAlgorithms;

public class LuceneIndex : ISearchAlgorithm
{
    const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;
    // N-gram lengths for Filter: 1..6 covers 1-2 char filters too (benchmarks use 3-6). Raising MinGram shrinks the index, but shorter filters then need a fallback.
    const int MinGram = 1, MaxGram = 6;
    // Longest prefix/suffix indexed for StartsWith/EndsWith (a reused tokenizer can't size itself per item).
    const int MaxEdgeGram = 64;
    // Like StringField (not stored, doc ids only, no norms) but tokenized, so the analyzer's n-gram tokenizers can feed it.
    static readonly FieldType NGramFieldType = new(StringField.TYPE_NOT_STORED) { IsTokenized = true };
    private readonly string _path;
    private readonly FSDirectory _directory;
    private readonly IndexWriter _indexWriter;
    // Lucene reuses each field's tokenizer per thread, instead of a new one (~6 KB of buffers) per field per document.
    private readonly Analyzer _analyzer = Analyzer.NewAnonymous((field, reader) => new TokenStreamComponents(field == "value-ngram"
        ? new NGramTokenizer(AppLuceneVersion, reader, MinGram, MaxGram)
        : new EdgeNGramTokenizer(AppLuceneVersion, reader, 1, MaxEdgeGram)), Analyzer.PER_FIELD_REUSE_STRATEGY);
    // Lucene's: ref-counts readers, so an old one closes only after the last search on it releases it.
    private readonly SearcherManager _searcherManager;
    private readonly Lock _refreshLock = new();
    private long _writes;  // bumped after every Add/Remove
    private long _visible; // writes the current searcher is guaranteed to include
    private readonly ConcurrentDictionary<string, byte> _items = new(StringComparer.Ordinal);
    private readonly ItemsByChars _itemsByChars;
    
    public LuceneIndex(string[] items)
    {
        _path = Guid.NewGuid().ToString();
        _directory = FSDirectory.Open(_path);

        var indexConfig = new IndexWriterConfig(AppLuceneVersion, _analyzer);
        _indexWriter = new IndexWriter(_directory, indexConfig);

        // _indexWriter.AddDocuments(items.Distinct().Select(CreateDocument));
        foreach (var item in items) _items.TryAdd(item, byte.MaxValue);
        _itemsByChars = _items.GetAlternateLookup<ReadOnlySpan<char>>();
        
        _indexWriter.AddDocuments(_items.Keys.Select(CreateDocument));
        
        _searcherManager = new SearcherManager(_indexWriter, applyAllDeletes: true, searcherFactory: null);
    }

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        SearchEdge("value-prefix", prefix, x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<string> GetByEndsWith(string suffix) =>
        SearchEdge("value-suffix", Reverse(suffix), x => x.EndsWith(suffix, StringComparison.Ordinal));

    // "" is a prefix of everything but no indexed term, so it matches all docs like the old PrefixQuery did.
    // shortcut: edges over MaxEdgeGram chars look up only their first MaxEdgeGram and verify the rest; raise MaxEdgeGram if long ones are common.
    List<string> SearchEdge(string field, string edge, Func<string, bool> matches)
    {
        if (edge.Length == 0) return Search(new MatchAllDocsQuery());
        var gram = edge.Length <= MaxEdgeGram ? edge : edge[..MaxEdgeGram];
        var ids = Search(new TermQuery(new Term(field, gram)));
        return gram.Length == edge.Length ? ids : ids.Where(matches).ToList();
    }

    public IReadOnlyList<string> Filter(string fragment)
    {
        // Fragments up to MaxGram chars are indexed as-is, so one exact term lookup finds them.
        // shortcut: longer fragments look up only their first MaxGram chars and verify the rest; intersect more grams if long filters matter.
        var gram = fragment.Length <= MaxGram ? fragment : fragment[..MaxGram];
        var ids = Search(new TermQuery(new Term("value-ngram", gram)));
        return gram.Length == fragment.Length ? ids : ids.Where(x => x.Contains(fragment, StringComparison.Ordinal)).ToList();
    }

    List<string> Search(Query query)
    {
        RefreshIfStale();
        var indexSearcher = _searcherManager.Acquire();
        try
        {
            var collector = new IdCollector(_itemsByChars);
            indexSearcher.Search(query, collector);
            return collector.Ids;
        }
        finally
        {
            _searcherManager.Release(indexSearcher);
        }
    }

    // A search must see every write that finished before it started; many writes share one reopen.
    void RefreshIfStale()
    {
        var writes = Interlocked.Read(ref _writes);
        if (writes == Interlocked.Read(ref _visible)) return;
        lock (_refreshLock)
        {
            if (writes <= _visible) return; // another search refreshed past it meanwhile
            var target = Interlocked.Read(ref _writes);
            _searcherManager.MaybeRefresh();
            Interlocked.Exchange(ref _visible, target);
        }
    }

    public void Add(string item)
    {
        _items.TryAdd(item, byte.MinValue);
        _indexWriter.UpdateDocument(item.IdTerm(), CreateDocument(item));

        Interlocked.Increment(ref _writes);
    }

    public void Remove(string item)
    {
        _indexWriter.DeleteDocuments(item.IdTerm());
        _items.TryRemove(item, out _);

        Interlocked.Increment(ref _writes);
    }

    public ValueTask DisposeAsync()
    {
        _searcherManager.Dispose();
        _indexWriter.Dispose();
        _analyzer.Dispose();
        _directory.Dispose();

        Directory.Delete(_path, true);

        return ValueTask.CompletedTask;
    }

    private static Document CreateDocument(string x)
    {
        Document document =
        [
            new StringField("id", x, Field.Store.NO),        // indexed: Add/Remove find the doc by this term
            new BinaryDocValuesField("id", new BytesRef(x)), // column: IdCollector reads the value back per hit
            // Every prefix (of the reverse: every suffix) is its own term, so StartsWith/EndsWith are one term lookup
            // instead of a PrefixQuery visiting each matching item's term. Max(1, ..): the tokenizer rejects 0, "" yields no terms.
            new Field("value-prefix", x, NGramFieldType),
            new Field("value-suffix", Reverse(x), NGramFieldType),
            new Field("value-ngram", x, NGramFieldType)
        ];

        return document;
    }

    static string Reverse(string s) =>
        string.Create(s.Length, s, static (dst, src) =>
        {
            src.AsSpan().CopyTo(dst);
            dst.Reverse();
        });
}

// Collects every hit's id: no scoring, no top-N queue, no stored-field loading.
sealed class IdCollector(ItemsByChars items) : ICollector
{
    public readonly List<string> Ids = [];
    readonly BytesRef _scratch = new();
    BinaryDocValues _ids = null!;
    char[] _chars = [];

    public void SetScorer(Scorer scorer) { }
    // Called once per segment; doc numbers in Collect are relative to that segment.
    public void SetNextReader(AtomicReaderContext context) => _ids = context.AtomicReader.GetBinaryDocValues("id");

    public void Collect(int doc)
    {
        _ids.Get(doc, _scratch);
        var bytes = _scratch.Bytes.AsSpan(_scratch.Offset, _scratch.Length);
        var max = Encoding.UTF8.GetMaxCharCount(bytes.Length);
        if (_chars.Length < max) _chars = new char[max];
        
        ReadOnlySpan<char> chars = _chars.AsSpan(0, Encoding.UTF8.GetChars(bytes, _chars));
        Ids.Add(items.TryGetValue(chars, out var item, out _) ? item : new string(chars));
    }
    
    public bool AcceptsDocsOutOfOrder => true;
}

static class DocumentExtensions
{
    public static Term IdTerm(this string id)
    {
        return new Term("id", id);
    }
}