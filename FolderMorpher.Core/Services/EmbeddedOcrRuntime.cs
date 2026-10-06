using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderMorpher.Services;

// Host owns this session directory. No persistent model cache or implicit download.
internal static class EmbeddedOcrRuntime
{
    private static readonly object Gate = new();
    private static string? _directory;
    private const string Prefix = "FolderMorpher.Ocr.";

    internal static string? Prepare()
    {
        lock (Gate)
        {
            if (_directory != null) return _directory;
            var assembly = typeof(EmbeddedOcrRuntime).Assembly;
            using var manifestStream = assembly.GetManifestResourceStream(Prefix + "manifest.json");
            if (manifestStream == null) return null;
            using var manifest = JsonDocument.Parse(manifestStream);
            string directory = Path.Combine(Path.GetTempPath(), "FolderMorpher-Ocr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var streams = new List<Stream>();
                try
                {
                    foreach (var chunk in manifest.RootElement.GetProperty("chunks").EnumerateArray())
                    {
                        var stream = assembly.GetManifestResourceStream(Prefix + chunk.GetProperty("path").GetString())
                            ?? throw new IOException("Embedded OCR chunk missing.");
                        streams.Add(stream);
                        if (stream.Length != chunk.GetProperty("bytes").GetInt64() ||
                            !Convert.ToHexString(SHA256.HashData(stream)).Equals(chunk.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Embedded OCR chunk verification failed.");
                        stream.Position = 0;
                    }
                    using var combined = new ChunkStream(streams);
                    using var gzip = new GZipStream(combined, CompressionMode.Decompress);
                    using var tar = new TarReader(gzip);
                    long total = 0;
                    int files = 0;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    TarEntry? entry;
                    while ((entry = tar.GetNextEntry()) != null)
                    {
                        if (entry.EntryType != TarEntryType.RegularFile || entry.Name.Split('/').Any(p => p is ".." or "." || p.Contains(':')))
                            throw new IOException("Invalid OCR archive entry.");
                        string destination = Path.GetFullPath(Path.Combine(directory, entry.Name));
                        if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(destination))
                            throw new IOException("OCR archive path is outside the session or duplicated.");
                        total = checked(total + entry.Length);
                        if (++files > 10000 || total > manifest.RootElement.GetProperty("expandedBytes").GetInt64())
                            throw new IOException("OCR archive exceeds its manifest.");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        if (entry.Length > 0) (entry.DataStream ?? throw new IOException("Missing OCR entry data.")).CopyTo(output);
                        if (output.Length != entry.Length) throw new IOException("Truncated OCR entry.");
                    }
                    if (files != manifest.RootElement.GetProperty("files").GetInt32() || total != manifest.RootElement.GetProperty("expandedBytes").GetInt64())
                        throw new IOException("Incomplete OCR archive.");
                }
                finally { foreach (var stream in streams) stream.Dispose(); }
                _directory = directory;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
                return directory;
            }
            catch { Directory.Delete(directory, true); throw; }
        }
    }

    internal static void Cleanup()
    {
        lock (Gate)
        {
            if (_directory == null) return;
            string directory = _directory;
            // Only the random direct child created by this process can be removed.
            if (!string.Equals(Path.GetDirectoryName(directory), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).StartsWith("FolderMorpher-Ocr-", StringComparison.Ordinal)) return;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(directory, true); _directory = null; return; }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }
            System.Diagnostics.Trace.WriteLine("OCR session cleanup could not finish; temporary files remain.");
        }
    }

    private sealed class ChunkStream(List<Stream> streams) : Stream
    {
        private int _index;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            while (_index < streams.Count)
            {
                int read = streams[_index].Read(buffer);
                if (read != 0) return read;
                _index++;
            }
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
