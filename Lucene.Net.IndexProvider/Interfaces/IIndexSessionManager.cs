using Lucene.Net.Index;
using Lucene.Net.IndexProvider.Models;
using System.Collections.Generic;

namespace Lucene.Net.IndexProvider.Interfaces;

public interface IIndexSessionManager
{
    LuceneSession GetSessionFrom(string indexName);
    void Commit(string indexName);
    IDictionary<string, LuceneSession> ContextSessions { get; }
    void CloseSession(string indexName);
    void AddLock(string indexName);
    void ReleaseLock(string indexName);

    /// <summary>
    /// Merges all shard writers for the given index back into the main writer,
    /// then disposes and removes the shard directories. No-op for single-writer sessions.
    /// </summary>
    void MergeShards(string indexName);
}