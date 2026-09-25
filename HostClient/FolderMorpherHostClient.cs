using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Contracts;
using StreamJsonRpc;

namespace FolderMorpher.HostClient
{
    /// <summary>
    /// FolderMorpher.Host 常駐プロセスと通信する IPC クライアント。
    /// （ADR 101: Host/Core/GUI 完全分離境界）
    /// Host が未起動の場合は自動的にバックグラウンド起動して接続を確立する。
    /// </summary>
    public class FolderMorpherHostClient : IDisposable
    {
        private static FolderMorpherHostClient? _instance;
        public static FolderMorpherHostClient Instance => _instance ??= new FolderMorpherHostClient();

        private NamedPipeClientStream? _pipeStream;
        private JsonRpc? _rpc;
        private IFolderMorpherHostService? _proxy;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public string PipeName { get; } = $"FolderMorpher_IPC_{Environment.UserName}";

        public async Task<IFolderMorpherHostService> GetServiceAsync(CancellationToken ct = default)
        {
            if (_proxy != null && _pipeStream != null && _pipeStream.IsConnected)
            {
                return _proxy;
            }

            await _connectionLock.WaitAsync(ct);
            try
            {
                if (_proxy != null && _pipeStream != null && _pipeStream.IsConnected)
                {
                    return _proxy;
                }

                await EnsureHostRunningAndConnectedAsync(ct);
                return _proxy!;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task EnsureHostRunningAndConnectedAsync(CancellationToken ct)
        {
            // まず既存のパイプへ接続試行
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _pipeStream?.Dispose();
                    _pipeStream = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(attempt == 0 ? 500 : 3000);
                    await _pipeStream.ConnectAsync(timeoutCts.Token);

                    _rpc = JsonRpc.Attach(_pipeStream);
                    _proxy = _rpc.Attach<IFolderMorpherHostService>();
                    return;
                }
                catch
                {
                    if (attempt == 0)
                    {
                        // 接続できない場合、FolderMorpher.Host.exe をバックグラウンド起動
                        LaunchHostProcess();
                        await Task.Delay(500, ct);
                    }
                }
            }

            throw new InvalidOperationException("FolderMorpher.Host プロセスとの IPC 接続を確立できませんでした。");
        }

        private void LaunchHostProcess()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var hostExe = Path.Combine(baseDir, "FolderMorpher.Host.exe");
                if (File.Exists(hostExe))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = hostExe,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _rpc?.Dispose();
            _pipeStream?.Dispose();
        }
    }
}
