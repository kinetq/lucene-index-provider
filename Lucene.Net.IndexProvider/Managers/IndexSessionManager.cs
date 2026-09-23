using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.IndexProvider.Interfaces;
using Lucene.Net.IndexProvider.Models;
using Lucene.Net.Search;
using Lucene.Net.Store;
using System;
using System.Collections.Generic;
using System.Threading;
using Lucene.Net.Index.Extensions;

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

    public IDictionary<string, LuceneSession> ContextSessions => _contextSessions.Value;
    private IDictionary<string, ManualResetEventSlim> SessionLocks => _sessionLocks.Value;

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

            IndexWriter writer;
            try
            {
                writer = new IndexWriter(directory, indexConfig);
            }
            catch (LockObtainFailedException)
            {
                // A stale write.lock exists from a previous crash. Clear it and retry once.
                directory.ClearLock(IndexWriter.WRITE_LOCK_NAME);
                var retryConfig = new IndexWriterConfig(config.LuceneVersion, analyzer);
                retryConfig.SetWriteLockTimeout(config.WriteLockTimeout);
                writer = new IndexWriter(directory, retryConfig);
            }

            var searchManager = new SearcherManager(writer, true, null);
            var luceneSession = new LuceneSession
            {
                Writer = writer,
                SearcherManager = searchManager
            };

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

    public void Commit(string indexName)
    {
        if (ContextSessions.TryGetValue(indexName, out var context))
        {
            if (context.Writer is { IsClosed: false } && context.Writer.HasUncommittedChanges())
            {
                context.Writer.Commit();
                context.SearcherManager.MaybeRefresh();
            }
        }
    }

    public void CloseSession(string indexName)
    {
        if (ContextSessions.TryGetValue(indexName, out var context))
        {
            if (context.Writer is { IsClosed: false })
            {
                try
                {
                    context.Writer.Commit();
                }
                finally
                {
                    context.Writer.Dispose();
                }
            }

            context.SearcherManager.Dispose();
            ContextSessions.Remove(indexName);
        }
    }
}