using System.Text.RegularExpressions;
using Lucene.Net.Analysis.Reverse;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;

namespace SearchAlgorithms;

// Baseline for LuceneIndex: the textbook setup. Default directory and config, one stored field per Key and Id,
// wildcard queries, a fresh reader per search and each hit's entry read back from stored fields.
public sealed class NaiveLuceneIndex : ISearchAlgorithm
{
    private const LuceneVersion AppLuceneVersion = LuceneVersion.LUCENE_48;
    private readonly string _path = Guid.NewGuid().ToString();
    private readonly FSDirectory _directory;
    private readonly StandardAnalyzer _analyzer = new(AppLuceneVersion); // unused by StringField, but the config requires one
    private readonly IndexWriter _indexWriter;

    public NaiveLuceneIndex(Entry[] items)
    {
        _directory = FSDirectory.Open(_path);
        _indexWriter = new IndexWriter(_directory, new IndexWriterConfig(AppLuceneVersion, _analyzer));
        foreach (var item in items)
            _indexWriter.AddDocument(CreateDocument(item));
    }

    public IReadOnlyList<Entry> GetByStartsWith(string prefix) => Search(Escape(prefix) + "*");

    public IReadOnlyList<Entry> GetByEndsWith(string suffix) => Search("*" + Escape(suffix));

    public IReadOnlyList<Entry> Filter(string fragment) => Search("*" + Escape(fragment) + "*");

    private List<Entry> Search(string pattern)
    {
        using var reader = DirectoryReader.Open(_indexWriter, applyAllDeletes: true);
        var searcher = new IndexSearcher(reader);
        
        var topDocs = searcher.Search(new WildcardQuery(new Term("value", pattern)), int.MaxValue);
        
        return topDocs.ScoreDocs
            .Select(x => searcher.Doc(x.Doc))
            .Select(d => new Entry(d.Get("value"), d.GetField("id").GetInt32Value()!.Value))
            .ToList();
    }

    // WildcardQuery's special characters, so queries stay literal.
    private static string Escape(string s) => Regex.Replace(s, @"[\\*?]", @"\$0");

    public void Add(Entry item) => _indexWriter.UpdateDocument(EntryTerm(item), CreateDocument(item));

    public void Remove(Entry item) => _indexWriter.DeleteDocuments(EntryTerm(item));

    private static Term EntryTerm(Entry x) => new("entry", x.IdText());

    public ValueTask DisposeAsync()
    {
        _indexWriter.Dispose();
        _analyzer.Dispose();
        _directory.Dispose();
        Directory.Delete(_path, true);
        return ValueTask.CompletedTask;
    }

    private static Document CreateDocument(Entry x) =>
        [
            new StringField("value", x.Key, Field.Store.YES), 
            new StoredField("id", x.Id), 
            new StringField("entry", x.IdText(), Field.Store.NO)
        ];
}
