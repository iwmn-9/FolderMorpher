using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace FolderMorpher.Services;

// Size groups share a bounded reader pool. Results leave in input order so the
// audit's large-file-first preview and deterministic group IDs stay intact.
internal sealed class DuplicateHashPipeline : IDisposable
{
    internal const int PartialChunkBytes = 4096;
    internal const long LocalPartialThresholdBytes = 64 * 1024;
    internal const long NetworkPartialThresholdBytes = 1024 * 1024;
    internal const long PrefixThresholdBytes = 32L * 1024 * 1024;
    internal const int PrefixBytes = 1024 * 1024;
    internal const int LocalConcurrency = 4;
    internal const int NetworkConcurrency = 2;
    internal const int LocalReadBytes = 256 * 1024;

    private readonly bool _network;
    private readonly int _concurrency;
    private readonly Channel<DuplicateHashReader> _available;
    private readonly DuplicateHashReader[] _readers;

    internal DuplicateHashPipeline(bool network)
    {
        _network = network;
        _concurrency = network ? NetworkConcurrency : LocalConcurrency;
        _available = Channel.CreateBounded<DuplicateHashReader>(_concurrency);
        _readers = Enumerable.Range(0, _concurrency)
            .Select(_ => new DuplicateHashReader(network ? 64 * 1024 : LocalReadBytes, network)).ToArray();
        foreach (var reader in _readers) _available.Writer.TryWrite(reader);
    }

    internal async Task<List<List<ScannedFileEntry>>> PrepareCandidatesAsync(
        IReadOnlyList<List<ScannedFileEntry>> groups, Action<int, int>? progress, CancellationToken ct)
    {
        long threshold = _network ? NetworkPartialThresholdBytes : LocalPartialThresholdBytes;
        int total = groups.Where(group => group[0].Length >= threshold).Sum(group => group.Count);
        int processed = 0;
        var candidates = new List<List<ScannedFileEntry>>[groups.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, groups.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = _network ? 1 : _concurrency,
            CancellationToken = ct
        }, async (index, token) =>
        {
            var group = groups[index];
            if (group[0].Length < threshold)
            {
                candidates[index] = [group];
                return;
            }

            var hashes = await HashFilesAsync(group, DuplicateHashMode.HeadTail, null, token,
                () => progress?.Invoke(Interlocked.Increment(ref processed), total));
            if (hashes.Any(string.IsNullOrEmpty))
            {
                candidates[index] = [group];
                return;
            }
            var partialGroups = SplitOrKeepOnFailure(group, hashes);
            var result = new List<List<ScannedFileEntry>>();
            foreach (var partialGroup in partialGroups)
            {
                if (_network || partialGroup[0].Length < PrefixThresholdBytes)
                    result.Add(partialGroup);
                else
                {
                    var prefixHashes = await HashFilesAsync(partialGroup, DuplicateHashMode.Prefix, null, token);
                    result.AddRange(SplitOrKeepOnFailure(partialGroup, prefixHashes));
                }
            }
            candidates[index] = result;
        });
        return candidates.SelectMany(group => group).ToList();
    }

    // A failed sample is unknown, not a non-match. Keep the entire source group
    // for full SHA so transient errors never separate possible duplicate pairs.
    internal static List<List<ScannedFileEntry>> SplitOrKeepOnFailure(
        List<ScannedFileEntry> files, string?[] hashes)
    {
        if (hashes.Any(string.IsNullOrEmpty)) return [files];
        return files.Select((file, index) => (File: file, Hash: hashes[index]!))
            .GroupBy(entry => entry.Hash).Where(group => group.Count() > 1)
            .Select(group => group.Select(entry => entry.File).ToList()).ToList();
    }

    internal async IAsyncEnumerable<(List<ScannedFileEntry> Files, string?[] Hashes)> HashGroupsAsync(
        IReadOnlyList<List<ScannedFileEntry>> groups, BandwidthThrottler? throttler,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new Queue<Task<(List<ScannedFileEntry>, string?[])>>();
        int next = 0;
        int window = _network ? 1 : _concurrency;
        Task<(List<ScannedFileEntry>, string?[])> StartNext()
        {
            var group = groups[next++];
            return HashGroupAsync(group, throttler, pendingCancellation.Token);
        }
        try
        {
            while (next < groups.Count && pending.Count < window) pending.Enqueue(StartNext());
            while (pending.Count > 0)
            {
                yield return await pending.Dequeue();
                if (next < groups.Count) pending.Enqueue(StartNext());
            }
        }
        finally
        {
            // A canceled/abandoned enumeration must not leave readers touching
            // files after the audit returns or disposes its shared buffers.
            pendingCancellation.Cancel();
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) when (pendingCancellation.IsCancellationRequested) { }
        }
    }

    private async Task<(List<ScannedFileEntry>, string?[])> HashGroupAsync(
        List<ScannedFileEntry> files, BandwidthThrottler? throttler, CancellationToken ct) =>
        (files, await HashFilesAsync(files, DuplicateHashMode.Full, throttler, ct));

    private async Task<string?[]> HashFilesAsync(List<ScannedFileEntry> files, DuplicateHashMode mode,
        BandwidthThrottler? throttler, CancellationToken ct, Action? completed = null)
    {
        var hashes = new string?[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = _concurrency,
            CancellationToken = ct
        }, async (index, token) =>
        {
            var reader = await _available.Reader.ReadAsync(token);
            try
            {
                var file = files[index];
                hashes[index] = await reader.HashAsync(file.FullPath, file.Length, mode, throttler, token);
                completed?.Invoke();
            }
            finally { _available.Writer.TryWrite(reader); }
        });
        return hashes;
    }

    public void Dispose()
    {
        foreach (var reader in _readers) reader.Dispose();
    }
}

internal enum DuplicateHashMode { HeadTail, Prefix, Full }

// One SHA implementation serves the audit, public hash helpers and deletion
// rechecks. A reader is leased exclusively and resets its state for every file.
internal sealed class DuplicateHashReader : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _readBytes;
    private readonly bool _asynchronous;
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    internal DuplicateHashReader(int readBytes, bool asynchronous)
    {
        _readBytes = readBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(readBytes, 2 * DuplicateHashPipeline.PartialChunkBytes));
        _asynchronous = asynchronous;
    }

    internal async ValueTask<string?> HashAsync(string path, long expectedSize, DuplicateHashMode mode,
        BandwidthThrottler? throttler, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool finished = false;
        try
        {
            var options = mode == DuplicateHashMode.HeadTail ? FileOptions.RandomAccess : FileOptions.SequentialScan;
            if (_asynchronous) options |= FileOptions.Asynchronous;
            // The reader already owns the buffer; no second FileStream buffer.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, options);
            // Enumeration already supplies the size. Network readers do not
            // query it again; full reads validate the actual byte count at EOF.
            long length = expectedSize;
            if (!_asynchronous && length >= 0 && stream.Length != length) return null;
            long consumed = 0;
            if (mode == DuplicateHashMode.HeadTail)
            {
                if (length < 0) return null;
                int head = (int)Math.Min(length, DuplicateHashPipeline.PartialChunkBytes);
                await ReadExactAndAppendAsync(stream, head, ct);
                int tail = (int)Math.Min(length - head, DuplicateHashPipeline.PartialChunkBytes);
                if (tail > 0)
                {
                    stream.Position = length - tail;
                    await ReadExactAndAppendAsync(stream, tail, ct);
                }
            }
            else
            {
                long limit = mode == DuplicateHashMode.Prefix ? DuplicateHashPipeline.PrefixBytes : long.MaxValue;
                while (consumed < limit)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(_readBytes, limit - consumed);
                    long started = throttler == null ? 0 : Stopwatch.GetTimestamp();
                    int read = _asynchronous
                        ? await stream.ReadAsync(_buffer.AsMemory(0, count), ct)
                        : stream.Read(_buffer, 0, count);
                    if (read == 0) break;
                    throttler?.ObserveRead(read, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    _sha.AppendData(_buffer, 0, read);
                    consumed += read;
                    if (throttler != null) await throttler.ThrottleAsync(read, ct);
                }
                if (mode == DuplicateHashMode.Prefix && consumed != limit) return null;
                if (mode == DuplicateHashMode.Full && expectedSize >= 0 && consumed != expectedSize) return null;
            }
            if (!_asynchronous && length >= 0 && stream.Length != length) return null;
            ct.ThrowIfCancellationRequested();
            string hash = Convert.ToHexString(_sha.GetHashAndReset()).ToLowerInvariant();
            finished = true;
            return hash;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { if (!finished) _sha.GetHashAndReset(); }
    }

    private async ValueTask ReadExactAndAppendAsync(FileStream stream, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_asynchronous) await stream.ReadExactlyAsync(_buffer.AsMemory(0, count), ct);
        else stream.ReadExactly(_buffer.AsSpan(0, count));
        _sha.AppendData(_buffer, 0, count);
    }

    public void Dispose()
    {
        _sha.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    }
}
