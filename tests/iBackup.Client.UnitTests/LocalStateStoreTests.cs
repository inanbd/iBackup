using iBackup.Client.Core.State;
using Xunit;

namespace iBackup.Client.UnitTests;

public class LocalStateStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LocalStateStore _store;
    private readonly Guid _folderId = Guid.NewGuid();

    public LocalStateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "ibackup-state-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new LocalStateStore(_dbPath);
    }

    [Fact]
    public async Task Upsert_and_read_back()
    {
        var file = new IndexedFile(_folderId, "docs/a.txt", 100, 42, new string('a', 64));
        await _store.UpsertIndexedFileAsync(file, CancellationToken.None);

        var loaded = await _store.GetIndexedFileAsync(_folderId, "docs/a.txt", CancellationToken.None);
        Assert.Equal(file, loaded);

        // Upsert overwrites.
        await _store.UpsertIndexedFileAsync(file with { Size = 200 }, CancellationToken.None);
        loaded = await _store.GetIndexedFileAsync(_folderId, "docs/a.txt", CancellationToken.None);
        Assert.Equal(200, loaded!.Size);
    }

    [Fact]
    public async Task Folder_index_returns_all_files_of_folder_only()
    {
        await _store.UpsertIndexedFileAsync(new IndexedFile(_folderId, "a.txt", 1, 1, "h1"), CancellationToken.None);
        await _store.UpsertIndexedFileAsync(new IndexedFile(_folderId, "b.txt", 2, 2, "h2"), CancellationToken.None);
        await _store.UpsertIndexedFileAsync(new IndexedFile(Guid.NewGuid(), "other.txt", 3, 3, "h3"), CancellationToken.None);

        var index = await _store.GetFolderIndexAsync(_folderId, CancellationToken.None);
        Assert.Equal(2, index.Count);
        Assert.True(index.ContainsKey("a.txt"));
        Assert.True(index.ContainsKey("b.txt"));
    }

    [Fact]
    public async Task Remove_and_rename_work()
    {
        await _store.UpsertIndexedFileAsync(new IndexedFile(_folderId, "old.txt", 1, 1, "h"), CancellationToken.None);

        await _store.RenameIndexedFileAsync(_folderId, "old.txt", "new.txt", CancellationToken.None);
        Assert.Null(await _store.GetIndexedFileAsync(_folderId, "old.txt", CancellationToken.None));
        Assert.NotNull(await _store.GetIndexedFileAsync(_folderId, "new.txt", CancellationToken.None));

        await _store.RemoveIndexedFileAsync(_folderId, "new.txt", CancellationToken.None);
        Assert.Null(await _store.GetIndexedFileAsync(_folderId, "new.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Pending_uploads_queue_roundtrip()
    {
        await _store.EnqueueUploadAsync(_folderId, @"C:\data\a.txt", "a.txt", priority: 1, CancellationToken.None);
        await _store.EnqueueUploadAsync(_folderId, @"C:\data\b.txt", "b.txt", priority: 5, CancellationToken.None);
        // Duplicate enqueue keeps the higher priority and does not duplicate the row.
        await _store.EnqueueUploadAsync(_folderId, @"C:\data\a.txt", "a.txt", priority: 0, CancellationToken.None);

        Assert.Equal(2, await _store.GetPendingCountAsync(CancellationToken.None));

        var batch = await _store.DequeueBatchAsync(10, CancellationToken.None);
        Assert.Equal(2, batch.Count);
        Assert.Equal("b.txt", batch[0].RelativePath); // higher priority first

        await _store.CompletePendingUploadAsync(batch[0].Id, CancellationToken.None);
        Assert.Equal(1, await _store.GetPendingCountAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
