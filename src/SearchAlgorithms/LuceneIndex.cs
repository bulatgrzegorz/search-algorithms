using System.Runtime.CompilerServices;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.NGram;
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
    private const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;
    // N-gram lengths for Filter: 1..6 covers 1-2 char filters too (benchmarks use 3-6). Raising MinGram shrinks the index, but shorter filters then need a fallback.
    private const int MinGram = 1, MaxGram = 6;
    // Longest prefix/suffix indexed for StartsWith/EndsWith. Longer ones are nearly unique per item: indexing them up to 64 chars
    // doubled the index's distinct terms (~6M more at 1M ISINs) and took 1.6x the build time, for queries SearchEdge can verify instead.
    private const int MaxEdgeGram = 6;
    // Like StringField (not stored, doc ids only, no norms) but tokenized, so the analyzer's n-gram tokenizers can feed it.
    private static readonly FieldType NGramFieldType = new(StringField.TYPE_NOT_STORED) { IsTokenized = true };
    private readonly string _path;
    private readonly NRTCachingDirectory _directory;
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
    // Per segment: its ids decoded once, indexed by doc number. Segments never change (deletes only hide docs),
    // so a table stays valid; keyed by the segment's core, it is dropped once Lucene no longer has that segment open.
    private readonly ConditionalWeakTable<object, string[]> _idsBySegment = new();

    public LuceneIndex(string[] items)
    {
        _path = Guid.NewGuid().ToString();
        // MMap: Lucene.Net picks it only on Windows/Linux; elsewhere NIOFSDirectory's buffered file reads were 44-70% of a search.
        // NRTCaching: the tiny segments each refresh after Add/Remove flushes stay in RAM (up to 5 MB each, 60 MB total);
        // as files, opening/deleting/stat-ing them was most of a refresh.
        _directory = new NRTCachingDirectory(new MMapDirectory(new DirectoryInfo(_path)), maxMergeSizeMB: 5, maxCachedMB: 60);

        // 256 MB: fastest build measured at 1M items (16 MB flushes so often that merging dominates, 2048 MB was slower too).
        // No compound file: packing each refresh's tiny segment into one meant writing, copying and deleting extra files.
        var indexConfig = new IndexWriterConfig(AppLuceneVersion, _analyzer) { RAMBufferSizeMB = 256, UseCompoundFile = false };
        // Each indexing thread also flushes its own buffer at its share of the items, so flushes run in parallel. A build that
        // fits in RAMBufferSizeMB (100k items) otherwise flushed every thread's buffer one by one inside ForceMerge.
        indexConfig.MaxBufferedDocs = Math.Max(2, items.Length / Environment.ProcessorCount);
        _indexWriter = new IndexWriter(_directory, indexConfig);

        // Not one AddDocuments call: that's an atomic block, which can't flush, so the whole index sat in one in-memory segment.
        // Each core fills its own segments; then one merge, since every extra segment costs every query a term lookup.
        Parallel.ForEach(items.Distinct(), item => _indexWriter.AddDocument(CreateDocument(item)));
        _indexWriter.ForceMerge(1);
        _indexWriter.Config.MaxBufferedDocs = IndexWriterConfig.DISABLE_AUTO_FLUSH; // build only: Add/Remove flush on refresh

        _searcherManager = new SearcherManager(_indexWriter, applyAllDeletes: true, searcherFactory: null);
    }

    public IReadOnlyList<string> GetByStartsWith(string prefix) =>
        SearchEdge("value-prefix", prefix, x => x.StartsWith(prefix, StringComparison.Ordinal));

    public IReadOnlyList<string> GetByEndsWith(string suffix) =>
        SearchEdge("value-suffix", Reverse(suffix), x => x.EndsWith(suffix, StringComparison.Ordinal));

    // "" is a prefix of everything but no indexed term, so it matches all docs like the old PrefixQuery did.
    // shortcut: edges over MaxEdgeGram chars look up only their first MaxEdgeGram and verify the rest; raise MaxEdgeGram if long ones are common.
    private List<string> SearchEdge(string field, string edge, Func<string, bool> matches)
    {
        if (edge.Length == 0) return Search(term: null);
        var gram = edge.Length <= MaxEdgeGram ? edge : edge[..MaxEdgeGram];
        var ids = Search(new Term(field, gram));
        return gram.Length == edge.Length ? ids : ids.Where(matches).ToList();
    }

    public IReadOnlyList<string> Filter(string fragment)
    {
        // Fragments up to MaxGram chars are indexed as-is, so one exact term lookup finds them.
        // shortcut: longer fragments look up only their first MaxGram chars and verify the rest; intersect more grams if long filters matter.
        var gram = fragment.Length <= MaxGram ? fragment : fragment[..MaxGram];
        var ids = Search(new Term("value-ngram", gram));
        return gram.Length == fragment.Length ? ids : ids.Where(x => x.Contains(fragment, StringComparison.Ordinal)).ToList();
    }

    // Docs holding the term, or every doc for null.
    private List<string> Search(Term? term)
    {
        RefreshIfStale();
        var indexSearcher = _searcherManager.Acquire();
        try
        {
            var reader = indexSearcher.IndexReader;
            // The term's lookup done here, not inside TermQuery, so its doc count can size the list up front
            // (growing it was ~60% of a large search); TermQuery reuses it instead of looking the term up again.
            var context = term is null ? null : TermContext.Build(reader.Context, term);
            Query query = term is null ? new MatchAllDocsQuery() : new TermQuery(term, context);
            var collector = new IdCollector(IdsByDoc, context?.DocFreq ?? reader.NumDocs);
            indexSearcher.Search(query, collector);
            return collector.Ids;
        }
        finally
        {
            _searcherManager.Release(indexSearcher);
        }
    }

    // Built by the first search that reaches a segment: a few ms for a large freshly merged one, then free.
    // Read from the "id" terms' postings, so a hit costs an array index instead of a doc-values read from the file.
    private string[] IdsByDoc(AtomicReader segment) => _idsBySegment.GetValue(segment.CoreCacheKey, _ =>
    {
        var ids = new string[segment.MaxDoc];
        var terms = segment.GetTerms("id").GetEnumerator();

        DocsEnum? docs = null;
        while (terms.MoveNext())
        {
            var id = terms.Term.Utf8ToString();
            // liveDocs null: deleted docs too, as later readers of this segment may hide a different set.
            docs = terms.Docs(null, docs, DocsFlags.NONE);
            for (var doc = docs.NextDoc(); doc != DocIdSetIterator.NO_MORE_DOCS; doc = docs.NextDoc()) 
                ids[doc] = id;
        }
        return ids;
    });

    // A search must see every write that finished before it started; many writes share one reopen.
    private void RefreshIfStale()
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
        _analyzer.Dispose();
        _directory.Dispose();

        Directory.Delete(_path, true);

        return ValueTask.CompletedTask;
    }

    private static Document CreateDocument(string x)
    {
        Document document =
        [
            new StringField("id", x, Field.Store.NO), // indexed: Add/Remove find the doc by this term, IdsByDoc reads ids back from it
            // Every prefix (of the reverse: every suffix) is its own term, so StartsWith/EndsWith are one term lookup
            // instead of a PrefixQuery visiting each matching item's term.
            new Field("value-prefix", x, NGramFieldType),
            new Field("value-suffix", Reverse(x), NGramFieldType),
            new Field("value-ngram", x, NGramFieldType)
        ];

        return document;
    }

    private static string Reverse(string s) =>
        string.Create(s.Length, s, static (dst, src) =>
        {
            src.AsSpan().CopyTo(dst);
            dst.Reverse();
        });
}

// Collects every hit's id: no scoring, no top-N queue, no stored-field loading, nothing decoded or allocated per hit.
internal sealed class IdCollector(Func<AtomicReader, string[]> idsByDoc, int capacity) : ICollector
{
    public readonly List<string> Ids = new(capacity);
    private string[] _ids = [];

    public void SetScorer(Scorer scorer) { }
    // Called once per segment; doc numbers in Collect are relative to that segment.
    public void SetNextReader(AtomicReaderContext context) => _ids = idsByDoc(context.AtomicReader);
    public void Collect(int doc) => Ids.Add(_ids[doc]);
    public bool AcceptsDocsOutOfOrder => true;
}

internal static class DocumentExtensions
{
    public static Term IdTerm(this string id)
    {
        return new Term("id", id);
    }
}
