using System;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Contracts;

namespace FolderMorpher.Host
{
    /// <summary>
    /// FolderMorpher.Host バックグラウンド常駐プロセスのエントリポイント
    /// （ADR 101: ユーザーログオンセッション常駐型 Core ホスト）
    /// </summary>
    public static class Program
    {
        public static Task RunAsync(string[] args)
        {
            // ユーザーセッションごとの単一インスタンス Mutex
            string mutexName = IpcEndpoint.MutexName;
            using var singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // 既に起動している場合は二重起動せず静かに終了
                return Task.CompletedTask;
            }

            try
            {
                var hostService = new HostService();
                var server = new NamedPipeHostServer(hostService);
                using var cts = new CancellationTokenSource();
                hostService.ShutdownRequested = cts.Cancel;
                server.Start();

                // プロセス終了シグナル（ログオフ、シャットダウン、Ctrl+C等）を検知
                EventHandler onProcessExit = (s, e) => cts.Cancel();
                ConsoleCancelEventHandler onConsoleCancel = (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                AppDomain.CurrentDomain.ProcessExit += onProcessExit;
                Console.CancelKeyPress += onConsoleCancel;
                try
                {
                    // Mutex ownership belongs to this entry thread. Keep its release on the same thread.
                    cts.Token.WaitHandle.WaitOne();
                }
                finally
                {
                    AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
                    Console.CancelKeyPress -= onConsoleCancel;
                    server.Stop();
                }
            }
            finally
            {
                singleInstanceMutex.ReleaseMutex();
            }
            return Task.CompletedTask;
        }
    }
}
