using Lucene.Net.Analysis.NGram;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;
using Lock = System.Threading.Lock;

namespace SearchAlgorithms;

public class LuceneIndex : ISearchAlgorithm
{
    const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;
    // N-gram lengths for Filter: 1..6 covers 1-2 char filters too (benchmarks use 3-6). Raising MinGram shrinks the index, but shorter filters then need a fallback.
    const int MinGram = 1, MaxGram = 6;
    // Like StringField (not stored, doc ids only, no norms) but tokenized, so the n-gram tokenizer can feed it.
    static readonly FieldType NGramFieldType = new(StringField.TYPE_NOT_STORED) { IsTokenized = true };
    private readonly string _path;
    private readonly FSDirectory _directory;
    private readonly IndexWriter _indexWriter;
    private readonly StandardAnalyzer _standardAnalyzer = new(AppLuceneVersion);
    // Lucene's: ref-counts readers, so an old one closes only after the last search on it releases it.
    private readonly SearcherManager _searcherManager;
    private readonly Lock _refreshLock = new();
    private long _writes;  // bumped after every Add/Remove
    private long _visible; // writes the current searcher is guaranteed to include

    public LuceneIndex(string[] items)
    {
        _path = Guid.NewGuid().ToString();
        _directory = FSDirectory.Open(_path);

        var indexConfig = new IndexWriterConfig(AppLuceneVersion, _standardAnalyzer);
        _indexWriter = new IndexWriter(_directory, indexConfig);

        _indexWriter.AddDocuments(items.Distinct().Select(CreateDocument));
        
        _searcherManager = new SearcherManager(_indexWriter, applyAllDeletes: true, searcherFactory: null);
    }

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        Search(new PrefixQuery(new Term("value-exact", prefix)));

    public IReadOnlyList<string> GetByEndsWith(string suffix) =>
        Search(new PrefixQuery(new Term("value-exact-revers", Reverse(suffix))));

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
            var collector = new IdCollector();
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
        _indexWriter.UpdateDocument(item.IdTerm(), CreateDocument(item));

        Interlocked.Increment(ref _writes);
    }

    public void Remove(string item)
    {
        _indexWriter.DeleteDocuments(item.IdTerm());

        Interlocked.Increment(ref _writes);
    }

    public ValueTask DisposeAsync()
    {
        _searcherManager.Dispose();
        _indexWriter.Dispose();
        _standardAnalyzer.Dispose();
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
            new StringField("value-exact", x, Field.Store.NO),
            new StringField("value-exact-revers", Reverse(x), Field.Store.NO),
            new Field("value-ngram", new NGramTokenizer(AppLuceneVersion, new StringReader(x), MinGram, MaxGram), NGramFieldType)
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
sealed class IdCollector : ICollector
{
    public readonly List<string> Ids = [];
    readonly BytesRef _scratch = new();
    BinaryDocValues _ids = null!;

    public void SetScorer(Scorer scorer) { }
    // Called once per segment; doc numbers in Collect are relative to that segment.
    public void SetNextReader(AtomicReaderContext context) => _ids = context.AtomicReader.GetBinaryDocValues("id");
    public void Collect(int doc) { _ids.Get(doc, _scratch); Ids.Add(_scratch.Utf8ToString()); }
    public bool AcceptsDocsOutOfOrder => true;
}

static class DocumentExtensions
{
    public static Term IdTerm(this string id)
    {
        return new Term("id", id);
    }
}