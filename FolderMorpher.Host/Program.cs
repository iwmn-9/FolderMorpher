using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Host
{
    /// <summary>
    /// FolderMorpher.Host バックグラウンド常駐プロセスのエントリポイント
    /// （ADR 101: ユーザーログオンセッション常駐型 Core ホスト）
    /// </summary>
    public static class Program
    {
        private static Mutex? _singleInstanceMutex;

        public static async Task Main(string[] args)
        {
            // ユーザーセッションごとの単一インスタンス Mutex
            string mutexName = $@"Local\FolderMorpher_Host_{Environment.UserName}";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // 既に起動している場合は二重起動せず静かに終了
                return;
            }

            try
            {
                var hostService = new HostService();
                var server = new NamedPipeHostServer(hostService);
                server.Start();

                // 常駐待機用 CancellationTokenSource
                using var cts = new CancellationTokenSource();

                // プロセス終了シグナル（ログオフ、シャットダウン、Ctrl+C等）を検知
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    server.Stop();
                    cts.Cancel();
                };

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    server.Stop();
                    cts.Cancel();
                };

                // キャンセルされるまで非同期に常駐待機
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 正常終了
                }
            }
            finally
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
        }
    }
}
