using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Lucene.Net.DocumentMapper.Helpers;
using Lucene.Net.Index;
using Lucene.Net.IndexProvider.Helpers;
using Lucene.Net.IndexProvider.Interfaces;
using Lucene.Net.IndexProvider.Managers;
using Lucene.Net.IndexProvider.Models;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lucene.Net.IndexProvider.Tests;

public class LockingTests : IDisposable
{
    private readonly string _indexPath;
    private readonly ServiceProvider _serviceProvider;
    private readonly IIndexSessionManager _sessionManager;
    private readonly IIndexProvider _indexProvider;
    private readonly Mock<IDirectoryManager> _mockDirectoryManager;

    private const string IndexName = "LockTestIndex";

    public LockingTests()
    {
        string settingsPath = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, @"..\..\..\settings"));
        _indexPath = Path.Combine(settingsPath, "PersonalBlog", "index");

        _mockDirectoryManager = new Mock<IDirectoryManager>();
        _mockDirectoryManager
            .Setup(x => x.GetDirectory(It.IsAny<string>()))
            .Returns((string name) => FSDirectory.Open(Path.Combine(_indexPath, name)));

        var mockPathFactory = new Mock<ILocalIndexPathFactory>();
        mockPathFactory.Setup(x => x.GetLocalIndexPath()).Returns(_indexPath);

        var services = new ServiceCollection()
            .AddLuceneDocumentMapper()
            .AddLuceneProvider()
            .AddLogging(b => b.AddConsole());

        services.Add(new ServiceDescriptor(typeof(ILocalIndexPathFactory), mockPathFactory.Object));
        services.Add(new ServiceDescriptor(typeof(IDirectoryManager), _mockDirectoryManager.Object));

        _serviceProvider = services.BuildServiceProvider();

        var configManager = _serviceProvider.GetRequiredService<IIndexConfigurationManager>();
        configManager.AddConfiguration(new LuceneConfig
        {
            Indexes = new[] { IndexName },
            BatchSize = 1000,
            LuceneVersion = LuceneVersion.LUCENE_48
        });

        _sessionManager = _serviceProvider.GetRequiredService<IIndexSessionManager>();
        _indexProvider = _serviceProvider.GetRequiredService<IIndexProvider>();
    }

    /// <summary>
    /// Proves fix 1: CloseSession must release the Lucene write.lock file on disk.
    /// After calling CloseSession the index directory must contain no write.lock file.
    /// </summary>
    [Fact]
    public async Task CloseSession_ReleasesWriteLockFile()
    {
        await _indexProvider.CreateIndexIfNotExists(IndexName);

        // Open a writer session so a write.lock is held.
        _sessionManager.GetSessionFrom(IndexName);

        string lockFile = Path.Combine(_indexPath, IndexName, IndexWriter.WRITE_LOCK_NAME);
        Assert.True(File.Exists(lockFile), "write.lock should exist while session is open");

        // Act
        _sessionManager.CloseSession(IndexName);

        // Assert: lock file must be gone after close.
        Assert.False(File.Exists(lockFile), "write.lock should be removed after CloseSession");
    }

    /// <summary>
    /// Proves fix 2: SwapIndex must always call ReleaseLock even when an exception
    /// is thrown during the swap.  A subsequent GetSessionFrom must not block/timeout.
    /// We force an exception by pointing the temp index at a path that does not exist.
    /// </summary>
    [Fact]
    public async Task SwapIndex_AlwaysReleasesSessionLock_OnException()
    {
        await _indexProvider.CreateIndexIfNotExists(IndexName);
        _sessionManager.GetSessionFrom(IndexName);
        _sessionManager.Commit(IndexName);
        _sessionManager.CloseSession(IndexName);

        // "nonexistent_temp" has no directory on disk, so SwapIndex will log + return false.
        bool result = await _indexProvider.SwapIndex("nonexistent_temp", IndexName);

        Assert.False(result);

        // Prove the session lock was released: GetSessionFrom must not throw TimeoutException.
        var ex = Record.Exception(() => _sessionManager.GetSessionFrom(IndexName));
        Assert.Null(ex);
    }

    /// <summary>
    /// Proves fix 3: GetSessionFrom must recover from a stale write.lock left on disk
    /// by a previous crash, rather than throwing LockObtainFailedException permanently.
    /// </summary>
    [Fact]
    public async Task GetSessionFrom_RecoversStaleLockFile()
    {
        await _indexProvider.CreateIndexIfNotExists(IndexName);

        // Simulate a crash: write a stale lock file without holding the actual lock.
        string lockFile = Path.Combine(_indexPath, IndexName, IndexWriter.WRITE_LOCK_NAME);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        await File.WriteAllTextAsync(lockFile, string.Empty);

        Assert.True(File.Exists(lockFile), "Precondition: stale write.lock should exist");

        // Act: must succeed instead of throwing LockObtainFailedException.
        var ex = Record.Exception(() => _sessionManager.GetSessionFrom(IndexName));
        Assert.Null(ex);

        // Clean up
        _sessionManager.CloseSession(IndexName);
    }

    public void Dispose()
    {
        try { _sessionManager.CloseSession(IndexName); } catch { /* already closed */ }

        string dir = Path.Combine(_indexPath, IndexName);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, true);

        _serviceProvider.Dispose();
    }
}
