using System.Collections.Generic;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace Lucene.Net.IndexProvider.Models;

public class LuceneSession
{
    public IndexWriter Writer { get; set; }
    public SearcherManager SearcherManager { get; set; }

    /// <summary>
    /// Per-shard writers used during a parallel write operation.
    /// Populated only when <see cref="LuceneConfig.WriterCount"/> is greater than 1.
    /// </summary>
    public IList<IndexWriter> ShardWriters { get; set; } = new List<IndexWriter>();

    public bool IsMultiWriter => ShardWriters.Count > 0;
}