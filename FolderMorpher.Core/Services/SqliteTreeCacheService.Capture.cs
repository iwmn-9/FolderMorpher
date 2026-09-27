using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FolderMorpher.Services;

public sealed partial class SqliteTreeCacheService
{
    private sealed class CaptureDirectory
    {
        public required string Path { get; init; }
        public long Id { get; init; }
        public long Size { get; set; }
        public int Files { get; set; }
        public int Folders { get; set; }
    }

    /// <summary>Publishes a fully enumerated local capture atomically in compact preorder form.</summary>
    public Task ImportCaptureAsync(string capturePath, string targetPath, CancellationToken ct) => Task.Run(() =>
    {
        EnsureInitialized();
        ct.ThrowIfCancellationRequested();
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = capturePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        source.Open();
        using var select = source.CreateCommand();
        select.CommandText = "SELECT SortPath, ParentPath, Name, Size, IsDirectory, Modified, Created " +
            "FROM Entries ORDER BY SortPath;";
        using var rows = select.ExecuteReader();

        using var destination = new SqliteConnection(_connectionString);
        destination.Open();
        using var tx = destination.BeginTransaction();
        try
        {
            var normalized = PathCanonicalizer.Normalize(targetPath);
            using (var existing = destination.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT 1 FROM TreeRoots WHERE NormalizedPath=@path LIMIT 1;";
                existing.Parameters.AddWithValue("@path", normalized);
                if (existing.ExecuteScalar() != null) return;
            }

            long rootId;
            using (var insertRoot = destination.CreateCommand())
            {
                insertRoot.Transaction = tx;
                insertRoot.CommandText = "INSERT INTO TreeRoots " +
                    "(NormalizedPath, OriginalTargetPath, Timestamp, TotalSizeBytes, FileCount, FolderCount) " +
                    "VALUES (@norm, @path, @ts, 0, 0, 0); SELECT last_insert_rowid();";
                insertRoot.Parameters.AddWithValue("@norm", normalized);
                insertRoot.Parameters.AddWithValue("@path", targetPath);
                insertRoot.Parameters.AddWithValue("@ts", DateTime.Now.ToString("o"));
                rootId = (long)insertRoot.ExecuteScalar()!;
            }

            long nextId;
            using (var max = destination.CreateCommand())
            {
                max.Transaction = tx;
                max.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM TreeNodes;";
                nextId = (long)max.ExecuteScalar()!;
            }

            using var insert = destination.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO TreeNodes " +
                "(Id,RootId,Name,ParentId,PathSuffix,SubtreeEndId,Size,FileCount,FolderCount,IsDirectory,LastModified,CreationTime,Sha256) " +
                "VALUES (@id,@root,@name,@parent,NULL,NULL,@size,@files,@folders,@dir,@mod,@create,NULL);";
            var id = insert.Parameters.Add("@id", SqliteType.Integer);
            var root = insert.Parameters.Add("@root", SqliteType.Integer);
            var name = insert.Parameters.Add("@name", SqliteType.Text);
            var parent = insert.Parameters.Add("@parent", SqliteType.Integer);
            var size = insert.Parameters.Add("@size", SqliteType.Integer);
            var files = insert.Parameters.Add("@files", SqliteType.Integer);
            var folders = insert.Parameters.Add("@folders", SqliteType.Integer);
            var dir = insert.Parameters.Add("@dir", SqliteType.Integer);
            var mod = insert.Parameters.Add("@mod", SqliteType.Integer);
            var created = insert.Parameters.Add("@create", SqliteType.Integer);
            root.Value = rootId;

            void Insert(long nodeId, string nodeName, long? parentId, long nodeSize, bool isDirectory,
                object modified, object creation)
            {
                id.Value = nodeId;
                name.Value = nodeName;
                parent.Value = (object?)parentId ?? DBNull.Value;
                size.Value = nodeSize;
                files.Value = isDirectory ? 0 : 1;
                folders.Value = 0;
                dir.Value = isDirectory ? 1 : 0;
                mod.Value = modified;
                created.Value = creation;
                insert.ExecuteNonQuery();
            }

            var rootName = Path.GetFileName(targetPath.TrimEnd('\\', '/'));
            if (string.Equals(Path.GetPathRoot(targetPath), targetPath, StringComparison.OrdinalIgnoreCase))
                rootName = targetPath;
            if (string.IsNullOrEmpty(rootName)) rootName = targetPath;
            var rootNodeId = ++nextId;
            Insert(rootNodeId, rootName, null, 0, true, DBNull.Value, DBNull.Value);
            var stack = new Stack<CaptureDirectory>();
            stack.Push(new CaptureDirectory { Path = Path.TrimEndingDirectorySeparator(targetPath), Id = rootNodeId });

            using var finish = destination.CreateCommand();
            finish.Transaction = tx;
            finish.CommandText = "UPDATE TreeNodes SET Size=@size,FileCount=@files,FolderCount=@folders,SubtreeEndId=@end WHERE Id=@id;";
            var finishSize = finish.Parameters.Add("@size", SqliteType.Integer);
            var finishFiles = finish.Parameters.Add("@files", SqliteType.Integer);
            var finishFolders = finish.Parameters.Add("@folders", SqliteType.Integer);
            var finishEnd = finish.Parameters.Add("@end", SqliteType.Integer);
            var finishId = finish.Parameters.Add("@id", SqliteType.Integer);

            void CloseDirectory()
            {
                var done = stack.Pop();
                finishSize.Value = done.Size;
                finishFiles.Value = done.Files;
                finishFolders.Value = done.Folders;
                finishEnd.Value = nextId;
                finishId.Value = done.Id;
                finish.ExecuteNonQuery();
                if (stack.Count == 0) return;
                var owner = stack.Peek();
                owner.Size += done.Size;
                owner.Files += done.Files;
                owner.Folders += done.Folders + 1;
            }

            long totalSize = 0;
            int totalFiles = 0;
            int totalFolders = 0;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                var parentPath = Path.TrimEndingDirectorySeparator(rows.GetString(1));
                while (stack.Count > 1 && !string.Equals(stack.Peek().Path, parentPath, StringComparison.OrdinalIgnoreCase))
                    CloseDirectory();
                if (!string.Equals(stack.Peek().Path, parentPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The captured scan has a child without its parent directory.");

                bool isDirectory = rows.GetInt32(4) == 1;
                long nodeSize = rows.GetInt64(3);
                long nodeId = ++nextId;
                Insert(nodeId, rows.GetString(2), stack.Peek().Id, nodeSize, isDirectory,
                    rows.IsDBNull(5) ? DBNull.Value : rows.GetInt64(5),
                    rows.IsDBNull(6) ? DBNull.Value : rows.GetInt64(6));
                if (isDirectory)
                {
                    var sortPath = rows.GetString(0);
                    stack.Push(new CaptureDirectory { Path = Path.TrimEndingDirectorySeparator(sortPath), Id = nodeId });
                }
                else
                {
                    stack.Peek().Size += nodeSize;
                    stack.Peek().Files++;
                }
            }
            while (stack.Count > 1) CloseDirectory();
            var summary = stack.Peek();
            totalSize = summary.Size;
            totalFiles = summary.Files;
            totalFolders = summary.Folders;
            CloseDirectory();

            using (var updateRoot = destination.CreateCommand())
            {
                updateRoot.Transaction = tx;
                updateRoot.CommandText = "UPDATE TreeRoots SET TotalSizeBytes=@size,FileCount=@files,FolderCount=@folders WHERE Id=@id;";
                updateRoot.Parameters.AddWithValue("@size", totalSize);
                updateRoot.Parameters.AddWithValue("@files", totalFiles);
                updateRoot.Parameters.AddWithValue("@folders", totalFolders);
                updateRoot.Parameters.AddWithValue("@id", rootId);
                updateRoot.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }, ct);
}
