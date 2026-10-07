using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Filunest互換 PP-OCRv6-small 2段ロケットOCRサービス (ADR 144)。
    /// 通常検索（第1ロケット）を先行完了させた後、画像・画像PDFに対して
    /// worker.py (JSON-Line IPC) を介して遅延全文走査を実行する。
    /// </summary>
    public static class OcrWorkerService
    {
        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".tif", ".gif"
        };

        private static readonly HashSet<string> SupportedOcrExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".tif", ".gif", ".pdf"
        };

        public static bool IsSupportedOcrExtension(string ext) =>
            SupportedOcrExtensions.Contains(ext);

        public static bool IsImageExtension(string ext) =>
            SupportedImageExtensions.Contains(ext);

        private static string? _cachedOcrDir;
        private static string? _cachedPythonExe;
        private static bool _envChecked;
        private static readonly object _envLock = new();

        /// <summary>
        /// OCR実行環境が利用可能か判定する
        /// </summary>
        public static bool IsEnvironmentAvailable()
        {
            lock (_envLock)
            {
                if (_envChecked)
                {
                    return !string.IsNullOrEmpty(_cachedOcrDir) && !string.IsNullOrEmpty(_cachedPythonExe);
                }

                try { _cachedOcrDir = ResolveOcrDirectory(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { _cachedOcrDir = null; }
                if (!string.IsNullOrEmpty(_cachedOcrDir))
                {
                    _cachedPythonExe = ResolvePythonExecutable(_cachedOcrDir);
                }

                _envChecked = true;
                return !string.IsNullOrEmpty(_cachedOcrDir) && !string.IsNullOrEmpty(_cachedPythonExe);
            }
        }

        /// <summary>
        /// 環境キャッシュをリセットして再判定可能にする（テスト用）
        /// </summary>
        public static void ResetEnvironmentCache()
        {
            lock (_envLock)
            {
                _cachedOcrDir = null;
                _cachedPythonExe = null;
                _envChecked = false;
            }
        }

        private static string? ResolveOcrDirectory()
        {
            // 1. 環境変数 FOLDERMORPHER_OCR_DIR
            string? envPath = Environment.GetEnvironmentVariable("FOLDERMORPHER_OCR_DIR");
            if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath) && IsValidOcrDir(envPath))
            {
                return Path.GetFullPath(envPath);
            }

            // 2. 環境変数 FILUNEST_OCR_DIR
            string? filunestEnv = Environment.GetEnvironmentVariable("FILUNEST_OCR_DIR");
            if (!string.IsNullOrWhiteSpace(filunestEnv) && Directory.Exists(filunestEnv) && IsValidOcrDir(filunestEnv))
            {
                return Path.GetFullPath(filunestEnv);
            }

            // 3. EXE直下の ocr ディレクトリ
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localOcr = Path.Combine(appDir, "ocr");
            if (Directory.Exists(localOcr) && IsValidOcrDir(localOcr))
            {
                return Path.GetFullPath(localOcr);
            }

            return EmbeddedOcrRuntime.Prepare();
        }

        private static bool IsValidOcrDir(string dir)
        {
            return File.Exists(Path.Combine(dir, "worker.py")) &&
                   File.Exists(Path.Combine(dir, "models.json"));
        }

        private static string? ResolvePythonExecutable(string ocrDir)
        {
            // 埋め込み Python を最優先
            string embeddedPython = Path.Combine(ocrDir, "python", "python.exe");
            if (File.Exists(embeddedPython))
            {
                return embeddedPython;
            }

            // システム PATH の python.exe
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null)
                {
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0) return "python";
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// OCRクライアントの作成を試行する
        /// </summary>
        public static async Task<OcrWorkerClient?> TryCreateClientAsync(CancellationToken ct = default)
        {
            if (!IsEnvironmentAvailable()) return null;

            string ocrDir = _cachedOcrDir!;
            string pythonExe = _cachedPythonExe!;

            var client = new OcrWorkerClient(ocrDir, pythonExe);
            bool ready = await client.StartAndAwaitReadyAsync(ct);
            if (!ready)
            {
                await client.DisposeAsync();
                return null;
            }

            return client;
        }

        /// <summary>
        /// 単一のOCR結果ページ
        /// </summary>
        public record OcrPageInfo(int PageNumber, string FullText, IReadOnlyList<string> Lines);

        /// <summary>
        /// ファイル全体のOCR結果
        /// </summary>
        public record OcrDocumentResult(
            string FilePath,
            bool Success,
            IReadOnlyList<OcrPageInfo> Pages,
            string? Warning);

        /// <summary>
        /// クエリ条件とOCR結果を照合し、マッチした場合は抜粋スニペットを生成する
        /// </summary>
        public static bool TryMatchOcrDocument(OcrDocumentResult doc, SearchQuery query, out string? snippet, out int page)
            => TryMatchOcrDocument(doc, query, null, out snippet, out page);

        public static bool TryMatchOcrDocument(
            OcrDocumentResult doc,
            SearchQuery query,
            SearchResultItem? candidate,
            out string? matchedSnippet,
            out int matchedPage)
        {
            matchedSnippet = null;
            matchedPage = 1;

            if (!doc.Success || doc.Pages.Count == 0) return false;

            var groups = SearchEngineService.GetRequiredContentGroups(candidate ?? new SearchResultItem(), query);
            if (groups.Count == 0) return false;
            // 1. Check document-wide match: all query groups must match across document pages
            bool documentMatches = groups.All(group =>
                group.Any(term => doc.Pages.Any(p => p.FullText.Contains(term, StringComparison.OrdinalIgnoreCase))));
            if (!documentMatches) return false;

            // 2. Prefer a single page that satisfies all groups
            foreach (var page in doc.Pages)
            {
                if (groups.All(group => group.Any(term => page.FullText.Contains(term, StringComparison.OrdinalIgnoreCase))))
                {
                    string term = groups.SelectMany(g => g).First(t => page.FullText.Contains(t, StringComparison.OrdinalIgnoreCase));
                    matchedPage = page.PageNumber;
                    matchedSnippet = Extract3LineWindowSnippet(page.Lines, term);
                    return true;
                }
            }

            // 3. Across-pages match: snippet from the first matching page
            foreach (var page in doc.Pages)
            {
                var matchedTerm = groups.SelectMany(g => g).FirstOrDefault(t => page.FullText.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (matchedTerm != null)
                {
                    matchedPage = page.PageNumber;
                    matchedSnippet = Extract3LineWindowSnippet(page.Lines, matchedTerm);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Filunest流 3-Line Window (前1行 + ヒット行 + 後1行) のスニペット生成
        /// </summary>
        private static string Extract3LineWindowSnippet(IReadOnlyList<string> lines, string keyword)
        {
            if (lines.Count == 0) return keyword;

            int matchIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                return lines[0];
            }

            int start = Math.Max(0, matchIndex - 1);
            int end = Math.Min(lines.Count - 1, matchIndex + 1);

            var sb = new StringBuilder();
            if (start > 0) sb.Append("... ");

            for (int i = start; i <= end; i++)
            {
                if (sb.Length > 0 && !sb.ToString().EndsWith("... ") && !sb.ToString().EndsWith(" "))
                {
                    sb.Append(" / ");
                }
                sb.Append(lines[i].Trim());
            }

            if (end < lines.Count - 1) sb.Append(" ...");

            return sb.ToString();
        }
    }

    /// <summary>
    /// worker.py と JSON-Line プロトコルで双方向通信を行う子プロセスクライアント
    /// </summary>
    public sealed class OcrWorkerClient : IAsyncDisposable, IDisposable
    {
        private readonly string _ocrDir;
        private readonly string _pythonExe;
        private Process? _process;
        private StreamWriter? _stdin;
        private StreamReader? _stdout;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _isDisposed;

        public OcrWorkerClient(string ocrDir, string pythonExe)
        {
            _ocrDir = ocrDir;
            _pythonExe = pythonExe;
        }

        public async Task<bool> StartAndAwaitReadyAsync(CancellationToken ct = default)
        {
            try
            {
                string workerScript = Path.Combine(_ocrDir, "worker.py");
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonExe,
                    Arguments = $"-I -B \"{workerScript}\"",
                    WorkingDirectory = _ocrDir,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = new UTF8Encoding(false)
                };

                _process = Process.Start(psi);
                if (_process == null) return false;

                _ = _process.StandardError.ReadToEndAsync(ct);
                _stdin = _process.StandardInput;
                _stdout = _process.StandardOutput;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    string? line = await _stdout.ReadLineAsync(timeoutCts.Token);
                    if (line == null) break;

                    if (line.Length > 4 * 1024 * 1024) throw new IOException("OCR response exceeded page limit.");
                    line = line.Trim();
                    using var ready = JsonDocument.Parse(line);
                    if (ready.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "ready")
                    {
                        return true;
                    }
                }
            }
            catch
            {
                KillProcessSafe();
            }

            return false;
        }

        /// <summary>
        /// 単一のファイルをOCR処理し、ページごとの抽出テキストを取得する
        /// </summary>
        public async Task<OcrWorkerService.OcrDocumentResult> ProcessFileAsync(string filePath, CancellationToken ct = default)
        {
            if (_isDisposed || _process == null || _process.HasExited || _stdin == null || _stdout == null)
            {
                return new OcrWorkerService.OcrDocumentResult(filePath, false, Array.Empty<OcrWorkerService.OcrPageInfo>(), "OCRワーカーが終了しています");
            }

            await _gate.WaitAsync(ct);
            try
            {
                var pages = new List<OcrWorkerService.OcrPageInfo>();
                string? warningMessage = null;

                bool completed = false;
                // {"path": "..."} を送信
                string reqJson = JsonSerializer.Serialize(new { path = filePath });
                await _stdin.WriteLineAsync(reqJson);
                await _stdin.FlushAsync();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(180)); // 1ファイル最大180秒 (Filunest OCR.md準拠)

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    string? line = await _stdout.ReadLineAsync(timeoutCts.Token);
                    if (line == null)
                    {
                        // 予期せぬ切断
                        KillProcessSafe();
                        return new OcrWorkerService.OcrDocumentResult(filePath, false, pages, "OCRワーカーが応答途中で終了しました");
                    }

                    if (line.Length > 4 * 1024 * 1024) throw new IOException("OCR response exceeded page limit.");
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        string? kind = root.TryGetProperty("kind", out var kp) ? kp.GetString() : null;

                        if (kind == "done")
                        {
                            completed = true;
                            break;
                        }
                        else if (kind == "warning")
                        {
                            if (root.TryGetProperty("detail", out var dp))
                            {
                                warningMessage = dp.GetString();
                            }
                        }
                        else if (kind == "page")
                        {
                            int pageNum = root.TryGetProperty("page", out var pp) ? pp.GetInt32() : 1;
                            var lines = new List<string>();
                            var fullTextSb = new StringBuilder();

                            if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var rec in records.EnumerateArray())
                                {
                                    if (rec.TryGetProperty("text", out var tp))
                                    {
                                        string? text = tp.GetString();
                                        if (!string.IsNullOrWhiteSpace(text))
                                        {
                                            lines.Add(text);
                                            fullTextSb.AppendLine(text);
                                        }
                                    }
                                }
                            }

                            pages.Add(new OcrWorkerService.OcrPageInfo(pageNum, fullTextSb.ToString(), lines));
                        }
                    }
                    catch
                    {
                        throw new IOException("Invalid OCR JSON response.");
                    }
                }

                if (!completed) { KillProcessSafe(); throw new IOException("OCR response incomplete."); }
                return new OcrWorkerService.OcrDocumentResult(filePath, true, pages, warningMessage);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { KillProcessSafe(); throw; }
            catch (Exception ex)
            {
                KillProcessSafe();
                return new OcrWorkerService.OcrDocumentResult(filePath, false, Array.Empty<OcrWorkerService.OcrPageInfo>(), ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void KillProcessSafe()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _stdin?.Close();
            }
            catch { }

            KillProcessSafe();
            _gate.Dispose();
            _process?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
