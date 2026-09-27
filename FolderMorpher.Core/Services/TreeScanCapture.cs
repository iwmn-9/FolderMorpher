using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FolderMorpher.Services;

/// <summary>
/// Captures metadata already returned by a cold live traversal. The bounded channel keeps
/// search memory bounded; the temporary database never touches the scanned volume/share.
/// An incomplete traversal is discarded rather than published as a complete TreeCache root.
/// </summary>
public sealed class TreeScanCapture : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _root;
    private readonly Channel<ScannedFileEntry> _entries;
    private readonly Task _writer;
    private bool _completed;
    private volatile bool _failed;

    private TreeScanCapture(string root, string path)
    {
        _root = root;
        _path = path;
        _entries = Channel.CreateBounded<ScannedFileEntry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _writer = Task.Run(WriteAsync);
    }

    public static async Task<TreeScanCapture?> TryStartAsync(string root, CancellationToken ct)
    {
        try
        {
            if (await SqliteTreeCacheService.Instance.HasRootAsync(root)) return null;
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(Path.GetDirectoryName(SqliteTreeCacheService.Instance.DbFilePath)!,
                "tree_capture_" + Guid.NewGuid().ToString("N") + ".db");
            return new TreeScanCapture(root, path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Tree capture unavailable: {ex}");
            return null;
        }
    }

    public void Add(ScannedFileEntry entry)
    {
        if (_completed || _failed) return;
        try { _entries.Writer.WriteAsync(entry).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            _failed = true;
            System.Diagnostics.Trace.WriteLine($"Tree capture stopped: {ex}");
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using (var setup = conn.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=OFF; " +
                    "CREATE TABLE Entries (SortPath TEXT NOT NULL, ParentPath TEXT NOT NULL, Name TEXT NOT NULL, " +
                    "Size INTEGER NOT NULL, IsDirectory INTEGER NOT NULL, Modified INTEGER, Created INTEGER); " +
                    "CREATE INDEX idx_capture_order ON Entries(SortPath);";
                setup.ExecuteNonQuery();
            }

            while (await _entries.Reader.WaitToReadAsync())
            {
                using var tx = conn.BeginTransaction();
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO Entries VALUES (@sort, @parent, @name, @size, @dir, @mod, @create);";
                var sort = insert.Parameters.Add("@sort", SqliteType.Text);
                var parent = insert.Parameters.Add("@parent", SqliteType.Text);
                var name = insert.Parameters.Add("@name", SqliteType.Text);
                var size = insert.Parameters.Add("@size", SqliteType.Integer);
                var dir = insert.Parameters.Add("@dir", SqliteType.Integer);
                var mod = insert.Parameters.Add("@mod", SqliteType.Integer);
                var created = insert.Parameters.Add("@create", SqliteType.Integer);
                int count = 0;
                while (count < 2048)
                {
                    if (!_entries.Reader.TryRead(out var entry))
                    {
                        if (!await _entries.Reader.WaitToReadAsync()) break;
                        continue;
                    }
                    bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    sort.Value = isDir ? entry.FullPath.TrimEnd('\\') + "\\" : entry.FullPath;
                    parent.Value = entry.DirectoryPath;
                    name.Value = entry.Name;
                    size.Value = isDir ? 0 : entry.Length;
                    dir.Value = isDir ? 1 : 0;
                    mod.Value = entry.LastWriteTime.ToBinary();
                    created.Value = entry.CreationTime.ToBinary();
                    insert.ExecuteNonQuery();
                    count++;
                }
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            _entries.Writer.TryComplete(ex);
            throw;
        }
    }

    public async Task PublishIfCompleteAsync(ScanCoverage coverage, CancellationToken ct)
    {
        _completed = true;
        _entries.Writer.TryComplete();
        await _writer;
        if (_failed) return;
        ct.ThrowIfCancellationRequested();
        if (!coverage.IsCompleteCoverage || coverage.TotalFoldersScanned == 0 ||
            coverage.CompletedFolders != coverage.DiscoveredFolders) return;
        await SqliteTreeCacheService.Instance.ImportCaptureAsync(_path, _root, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        _entries.Writer.TryComplete();
        try { await _writer; } catch { /* A failed cache must not fail the scan. */ }
        try { File.Delete(_path); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Tree capture cleanup failed: {ex}"); }
        try { File.Delete(_path + "-journal"); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Tree capture journal cleanup failed: {ex}"); }
    }
}
