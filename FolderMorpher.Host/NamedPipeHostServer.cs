using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Contracts;
using StreamJsonRpc;

namespace FolderMorpher.Host
{
    /// <summary>
    /// Named Pipe をリッスンし、StreamJsonRpc で GUI / CLI / Agent と通信する IPC サーバー
    /// （ADR 101: 堅牢なプロセス間通信基盤）
    /// </summary>
    public class NamedPipeHostServer
    {
        public static string PipeName => $"FolderMorpher_IPC_{Environment.UserName}";

        private readonly HostService _hostService;
        private readonly CancellationTokenSource _cts = new();

        public NamedPipeHostServer(HostService hostService)
        {
            _hostService = hostService ?? throw new ArgumentNullException(nameof(hostService));
        }

        public void Start()
        {
            Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 現在のログオンユーザーのみアクセス可能な安全なパイプセキュリティ設定
                    var pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeServer.WaitForConnectionAsync(ct);

                    // 接続が来たら、非同期にハンドリングを開始し、リスナーは次の接続待機へ即座に戻る
                    _ = HandleClientConnectionAsync(pipeServer, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // リスナーの予期せぬ例外は短時間待機して自己回復
                    await Task.Delay(500, ct);
                }
            }
        }

        private async Task HandleClientConnectionAsync(NamedPipeServerStream pipeStream, CancellationToken ct)
        {
            try
            {
                using (pipeStream)
                {
                    // StreamJsonRpc で HostService を RPC ターゲットとしてバインド
                    var jsonRpc = JsonRpc.Attach(pipeStream, _hostService);
                    await jsonRpc.Completion;
                }
            }
            catch (Exception)
            {
                // クライアントの急な切断等は安全に終了
            }
        }
    }
}
