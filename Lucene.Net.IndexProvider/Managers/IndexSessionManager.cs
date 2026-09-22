using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.Index.Extensions;
using Lucene.Net.IndexProvider.Interfaces;
using Lucene.Net.IndexProvider.Models;
using Lucene.Net.Search;
using Lucene.Net.Store;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Lucene.Net.IndexProvider.Managers;

public class IndexSessionManager : IIndexSessionManager
{
    private readonly IIndexConfigurationManager _configurationManager;
    private readonly IDirectoryManager _directoryManager;

    public IndexSessionManager(
        IIndexConfigurationManager configurationManager, 
        IDirectoryManager directoryManager)
    {
        _configurationManager = configurationManager;
        _directoryManager = directoryManager;
    }

    private readonly Lazy<Dictionary<string, LuceneSession>> _contextSessions =
        new(() => new Dictionary<string, LuceneSession>(StringComparer.OrdinalIgnoreCase));

    private readonly Lazy<Dictionary<string, ManualResetEventSlim>> _sessionLocks =
        new(() => new Dictionary<string, ManualResetEventSlim>(StringComparer.OrdinalIgnoreCase));

    private readonly Lazy<Dictionary<string, IList<Directory>>> _shardDirectories =
        new(() => new Dictionary<string, IList<Directory>>(StringComparer.OrdinalIgnoreCase));

    public IDictionary<string, LuceneSession> ContextSessions => _contextSessions.Value;
    private IDictionary<string, ManualResetEventSlim> SessionLocks => _sessionLocks.Value;
    private IDictionary<string, IList<Directory>> ShardDirectories => _shardDirectories.Value;

    public LuceneSession GetSessionFrom(string indexName)
    {
        if (SessionLocks.TryGetValue(indexName, out var sessionLock))
        {
            bool signaled = sessionLock.Wait(TimeSpan.FromSeconds(5));
            if (!signaled)
            {
                throw new TimeoutException($"Lock for index '{indexName}' could not be acquired within the timeout period.");
            }
        }

        if (!ContextSessions.TryGetValue(indexName, out var context))
        {
            var config = _configurationManager.GetConfiguration(indexName);

            var analyzer = new StandardAnalyzer(config.LuceneVersion);
            var directory = _directoryManager.GetDirectory(indexName);

            if (config.ReadOnly)
            {
                var searcherManager = new SearcherManager(directory, null);
                var readOnlyLuceneSession = new LuceneSession
                {
                    SearcherManager = searcherManager
                };

                ContextSessions.Add(indexName, readOnlyLuceneSession);
                return readOnlyLuceneSession;
            }

            var indexConfig = new IndexWriterConfig(config.LuceneVersion, analyzer);
            indexConfig.SetWriteLockTimeout(config.WriteLockTimeout);

            var writer = new IndexWriter(directory, indexConfig);
            var searchManager = new SearcherManager(writer, true, null);
            var luceneSession = new LuceneSession
            {
                Writer = writer,
                SearcherManager = searchManager
            };

            if (config.WriterCount > 1)
            {
                var shardDirs = new List<Directory>(config.WriterCount);
                for (int i = 0; i < config.WriterCount; i++)
                {
                    string shardName = GetShardIndexName(indexName, i);
                    var shardDirectory = _directoryManager.GetDirectory(shardName);
                    var shardConfig = new IndexWriterConfig(config.LuceneVersion, new StandardAnalyzer(config.LuceneVersion));
                    shardConfig.SetWriteLockTimeout(config.WriteLockTimeout);
                    luceneSession.ShardWriters.Add(new IndexWriter(shardDirectory, shardConfig));
                    shardDirs.Add(shardDirectory);
                }
                ShardDirectories[indexName] = shardDirs;
            }

            ContextSessions.Add(indexName, luceneSession);
            return luceneSession;
        }

        return context;
    }

    public void AddLock(string indexName)
    {
        if (!SessionLocks.ContainsKey(indexName))
        {
            SessionLocks[indexName] = new ManualResetEventSlim(false);
        }
    }

    public void ReleaseLock(string indexName)
    {
        if (SessionLocks.TryGetValue(indexName, out var sessionLock))
        {
            sessionLock.Set();
            SessionLocks.Remove(indexName);
        }
    }

    public void MergeShards(string indexName)
    {
        if (!ContextSessions.TryGetValue(indexName, out var session) || !session.IsMultiWriter)
            return;

        var shardDirectories = new List<Directory>(session.ShardWriters.Count);
        foreach (var shardWriter in session.ShardWriters)
        {
            if (!shardWriter.IsClosed)
            {
                shardWriter.Commit();
                shardDirectories.Add(shardWriter.Directory);
                shardWriter.Dispose();
            }
        }

        session.Writer.AddIndexes(shardDirectories.ToArray());
        session.Writer.ForceMerge(1);

        for (int i = 0; i < session.ShardWriters.Count; i++)
        {
            string shardName = GetShardIndexName(indexName, i);
            _directoryManager.DisposeDirectory(shardName);
        }

        session.ShardWriters.Clear();

        if (ShardDirectories.ContainsKey(indexName))
            ShardDirectories.Remove(indexName);
    }

    public void Commit(string indexName)
    {
        if (!ContextSessions.TryGetValue(indexName, out var context))
            return;

        foreach (var shardWriter in context.ShardWriters)
        {
            if (!shardWriter.IsClosed && shardWriter.HasUncommittedChanges())
                shardWriter.Commit();
        }

        if (context.Writer is { IsClosed: false } && context.Writer.HasUncommittedChanges())
        {
            context.Writer.Commit();
            context.SearcherManager.MaybeRefresh();
        }
    }

    public void CloseSession(string indexName)
    {
        if (!ContextSessions.TryGetValue(indexName, out var context))
            return;

        if (context.IsMultiWriter)
            MergeShards(indexName);

        if (context.Writer is { IsClosed: false })
        {
            context.Writer.Commit();
            context.Writer.Dispose();
        }

        context.SearcherManager.Dispose();
        ContextSessions.Remove(indexName);
    }

    private static string GetShardIndexName(string indexName, int shardIndex)
        => $"{indexName}_shard_{shardIndex}";
}