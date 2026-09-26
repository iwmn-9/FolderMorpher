using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
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
        private Process? _launchedHost;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public string PipeName => IpcEndpoint.PipeName;
        public int? LaunchedHostProcessId => _launchedHost?.Id;

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
                    _pipeStream = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(attempt == 0 ? 500 : 90000);
                    await _pipeStream.ConnectAsync(timeoutCts.Token);

                    _rpc = JsonRpc.Attach(_pipeStream);
                    _proxy = _rpc.Attach<IFolderMorpherHostService>();
                    var status = await _proxy.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
                    var clientVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
                    if (!string.Equals(status.Version, clientVersion, StringComparison.Ordinal))
                    {
                        _rpc.Dispose();
                        _pipeStream.Dispose();
                        _proxy = null;
                        throw new HostVersionMismatchException(
                            $"起動中のFolderMorpher Hostは旧版です（Host {status.Version} / GUI {clientVersion}）。Hostの作業を終えてWindowsを再ログインし、新しいEXEを起動してください。");
                    }
                    return;
                }
                catch (HostVersionMismatchException)
                {
                    throw;
                }
                catch
                {
                    if (attempt == 0)
                    {
                        // The same executable starts in host mode; single-file publish needs no companion EXE.
                        LaunchHostProcess();
                        await Task.Delay(500, ct);
                    }
                }
            }

            throw new InvalidOperationException("FolderMorpher の Host モードとの IPC 接続を確立できませんでした。");
        }

        private sealed class HostVersionMismatchException(string message) : InvalidOperationException(message) { }

        private void LaunchHostProcess()
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("現在の実行ファイルのパスを取得できません。");
            var psi = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyPath = Path.Combine(AppContext.BaseDirectory, "FolderMorpher.dll");
                if (!File.Exists(assemblyPath))
                    throw new InvalidOperationException("開発時の Host 起動対象を取得できません。");
                psi.ArgumentList.Add(assemblyPath);
            }
            psi.ArgumentList.Add("--host");
            if (Environment.GetCommandLineArgs().Contains("--test-ipc"))
                psi.ArgumentList.Add("--test-ipc");
            _launchedHost = Process.Start(psi) ?? throw new InvalidOperationException("Host プロセスを起動できませんでした。");
        }

        /// <summary>Only the IPC smoke test may stop a host process it started itself.</summary>
        public void StopLaunchedHostForTests()
        {
            Dispose();
            if (_launchedHost is not { HasExited: false } process) return;
            process.Kill();
            process.WaitForExit(5000);
            process.Dispose();
            _launchedHost = null;
        }

        public void Dispose()
        {
            _rpc?.Dispose();
            _pipeStream?.Dispose();
        }
    }
}
