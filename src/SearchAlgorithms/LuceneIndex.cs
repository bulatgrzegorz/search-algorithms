using System.Buffers;
using System.Reflection.Emit;
using System.Text;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;

namespace SearchAlgorithms;

public class LuceneIndex : ISearchAlgorithm
{
    const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;
    // N-gram lengths for Filter: 1..6 covers 1-2 char filters too (benchmarks use 3-6). Raising MinGram shrinks the index, but shorter filters then need a fallback.
    const int MinGram = 1, MaxGram = 6;
    // Like StringField (not stored, doc ids only, no norms) but tokenized, so the n-gram tokenizer can feed it.
    static readonly FieldType NGramFieldType = new(StringField.TYPE_NOT_STORED) { IsTokenized = true };
    private string path;
    FSDirectory _directory;
    IndexWriter _indexWriter;
    IndexSearcher _indexSearcher;
    StandardAnalyzer _standardAnalyzer;
    
    public LuceneIndex(string[] items)
    {
        // Ensures index backward compatibility
        

// Construct a machine-independent path for the index

        path = Guid.NewGuid().ToString();
        _directory = FSDirectory.Open(path);

// Create an analyzer to process the text
        _standardAnalyzer = new StandardAnalyzer(AppLuceneVersion);

// Create an index writer
        var indexConfig = new IndexWriterConfig(AppLuceneVersion, _standardAnalyzer);
        _indexWriter = new IndexWriter(_directory, indexConfig);
        
        // var fields = items.Select(x => new TextField("value", x, Field.Store.YES));
        var f = items.Distinct().Select(CreateDocument);
        
        _indexWriter.AddDocuments(f);
        // _indexWriter.Commit();
        // _indexWriter.Flush(triggerMerge: false, applyAllDeletes: false);
        
        
        // var reader = _indexWriter.GetReader(applyAllDeletes: true);
        // _indexSearcher = new IndexSearcher(reader);
    }

    public IReadOnlyList<string> GetByStartsWithOld(string prefix)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // ReadOnlySpan<char> trimmed = prefix.AsSpan().Trim(); // slicing, no copy
        //
        // char[]? rented = null;
        // Span<char> prefixBuffer = trimmed.Length <= 256
        //     ? stackalloc char[256]
        //     : (rented = ArrayPool<char>.Shared.Rent(trimmed.Length));
        
        // var prefixWritten = trimmed.ToLowerInvariant(prefixBuffer); // returns -1 only if the buffer is too small
        // ReadOnlySpan<char> normalized = trimmed;// prefixBuffer[..prefixWritten];
        
        var utf8 = Encoding.UTF8;
        var buffer = ArrayPool<byte>.Shared.Rent(utf8.GetByteCount(prefix) + utf8.GetByteCount([WildcardQuery.WILDCARD_STRING]));
        try
        {
            var written = utf8.GetBytes(prefix, buffer);
            written += utf8.GetBytes([WildcardQuery.WILDCARD_STRING], buffer.AsSpan(written));
            
            var wildcardQuery = new WildcardQuery(new Term("value", new BytesRef(buffer, 0, written)));
            
            TopDocs topDocs = indexSearcher.Search(wildcardQuery, int.MaxValue);
        
            return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
        }
        finally
        {
            // if (rented is not null) ArrayPool<char>.Shared.Return(rented);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IReadOnlyList<string> GetByStartsWithOld2(string prefix)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // var a= indexSearcher.Search(new MatchAllDocsQuery(), 10000);
        
        var utf8 = Encoding.UTF8;
        var buffer = ArrayPool<byte>.Shared.Rent(utf8.GetByteCount(prefix) + utf8.GetByteCount([WildcardQuery.WILDCARD_STRING]));
        try
        {
            var written = utf8.GetBytes(prefix, buffer);
            written += utf8.GetBytes([WildcardQuery.WILDCARD_STRING], buffer.AsSpan(written));

            // var bytes = buffer.AsSpan(0, written); // Rent can return a bigger array, so always use `written`
            
            var wildcardQuery = new WildcardQuery(new Term("value", new BytesRef(buffer, 0, written)));
            
            TopDocs topDocs = indexSearcher.Search(wildcardQuery, int.MaxValue);
        
            return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    public IReadOnlyList<string> GetByStartsWith(string prefix)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // var a= indexSearcher.Search(new MatchAllDocsQuery(), 10000);

            // var bytes = buffer.AsSpan(0, written); // Rent can return a bigger array, so always use `written`
            var p = QueryParserBase.Escape(prefix);
            var pp = indexSearcher.Search(new MatchAllDocsQuery(), int.MaxValue);
            
            var ppp = pp.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc)).ToList();
            
        var wildcardQuery = new PrefixQuery(new Term("value-exact", QueryParserBase.Escape(prefix)));

        TopDocs topDocs = indexSearcher.Search(wildcardQuery,  int.MaxValue);

        return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
    }
    
    public IReadOnlyList<string> GetByEndsWith(string suffix)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // var a= indexSearcher.Search(new MatchAllDocsQuery(), 10000);

        // var bytes = buffer.AsSpan(0, written); // Rent can return a bigger array, so always use `written`
        
        var pp = indexSearcher.Search(new MatchAllDocsQuery(), int.MaxValue);
            
        var ppp = pp.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc)).ToList();
        
        var wildcardQuery = new PrefixQuery(new Term("value-exact-revers", QueryParserBase.Escape(Reverse(suffix))));

        TopDocs topDocs = indexSearcher.Search(wildcardQuery,  int.MaxValue);

        return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
    }
    
    public IReadOnlyList<string> GetByEndsWithOld(string suffix)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // var a= indexSearcher.Search(new MatchAllDocsQuery(), 10000);
        
        var utf8 = Encoding.UTF8;
        var buffer = ArrayPool<byte>.Shared.Rent(utf8.GetByteCount(suffix) + utf8.GetByteCount([WildcardQuery.WILDCARD_STRING]));
        try
        {
            var written = utf8.GetBytes([WildcardQuery.WILDCARD_STRING], buffer);
            written += utf8.GetBytes(suffix, buffer.AsSpan(written));

            // var bytes = buffer.AsSpan(0, written); // Rent can return a bigger array, so always use `written`
            
            var wildcardQuery = new WildcardQuery(new Term("value-exact", new BytesRef(buffer, 0, written)));
            
            TopDocs topDocs = indexSearcher.Search(wildcardQuery, int.MaxValue);
        
            return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IReadOnlyList<string> Filter(string fragment)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        // Fragments up to MaxGram chars are indexed as-is, so one exact term lookup finds them.
        // shortcut: longer fragments look up only their first MaxGram chars and verify the rest; intersect more grams if long filters matter.
        var gram = fragment.Length <= MaxGram ? fragment : fragment[..MaxGram];
        TopDocs topDocs = indexSearcher.Search(new TermQuery(new Term("value-ngram", gram)), int.MaxValue);

        var ids = topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId());
        return (gram.Length == fragment.Length ? ids : ids.Where(x => x.Contains(fragment, StringComparison.Ordinal))).ToList();
    }

    public IReadOnlyList<string> FilterOld(string fragment)
    {
        using var reader = _indexWriter.GetReader(applyAllDeletes: true);
        var indexSearcher = new IndexSearcher(reader);

        var utf8 = Encoding.UTF8;
        var buffer = ArrayPool<byte>.Shared.Rent(utf8.GetByteCount(fragment) + 2*utf8.GetByteCount([WildcardQuery.WILDCARD_STRING]));
        try
        {
            var written = utf8.GetBytes([WildcardQuery.WILDCARD_STRING], buffer);
            written += utf8.GetBytes(fragment, buffer.AsSpan(written));
            written += utf8.GetBytes([WildcardQuery.WILDCARD_STRING], buffer.AsSpan(written));

            // var bytes = buffer.AsSpan(0, written); // Rent can return a bigger array, so always use `written`
            
            var wildcardQuery = new WildcardQuery(new Term("value-exact", new BytesRef(buffer, 0, written)));
            
            TopDocs topDocs = indexSearcher.Search(wildcardQuery, int.MaxValue);
        
            return topDocs.ScoreDocs.Select(x => indexSearcher.Doc(x.Doc).GetId()).ToList();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Add(string item)
    {
        // _indexWriter.AddDocument(doc);
        _indexWriter.UpdateDocument(new Term("id", item), CreateDocument(item));
        // _indexWriter.AddDocument([new TextField("value", item, Field.Store.YES)]);
        // _indexWriter.Commit();
    }

    public void Remove(string item)
    {
        _indexWriter.DeleteDocuments(new Term("id", item));
        // _indexWriter.Commit();
    }

    public ValueTask DisposeAsync()
    {
        _standardAnalyzer.Dispose();
        _directory.Dispose();
        
        Directory.Delete(path, true);
        
        return ValueTask.CompletedTask;
    }
    
    private static Document CreateDocument(string x)
    {
        Document document =
        [
            new StringField("id", x, Field.Store.YES),
            new StringField("value-exact", QueryParserBase.Escape(x), Field.Store.YES),
            new StringField("value-exact-revers", QueryParserBase.Escape(Reverse(x)), Field.Store.YES),
            new TextField("value", QueryParserBase.Escape(x), Field.Store.YES),
            new Field("value-ngram",
                new NGramTokenizer(AppLuceneVersion, new StringReader(x), MinGram, MaxGram), NGramFieldType)
        ];
        
        // Every substring of MinGram..MaxGram chars, for Filter. IndexWriter consumes and disposes the tokenizer.

        return document;
    }
    
    static string Reverse(string s) =>
        string.Create(s.Length, s, static (dst, src) =>
        {
            src.AsSpan().CopyTo(dst);
            dst.Reverse();
        });
}



static class DocumentExtensions
{
    public static string GetValue(this Document document)
    {
        return document.Get("value");
    }

    public static string GetId(this Document document)
    {
        return document.Get("id");
    }
}