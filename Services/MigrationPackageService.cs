using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// エンタープライズ移行パッケージ（Wave分割Robocopyバッチ群、安全弁、Excel手順書）を静的生成する統合サービス
    /// </summary>
    public class MigrationPackageService
    {
        private const long TransferRateBytesPerSec = 80L * 1024 * 1024; // 1Gbps 実効 約80MB/s

        /// <summary>
        /// 設計ツリーとポリシーから移行波次（Wave）計画を自動算定・プレビュー作成します。
        /// </summary>
        public List<MigrationWavePlan> PlanWaves(IEnumerable<SimFolderNode> rootNodes, MigrationPackageOptions options)
        {
            var nodes = rootNodes.ToList();
            if (nodes.Count == 0) return new List<MigrationWavePlan>();

            // 対象とする分割単位ノードの選定
            // もしルートノードが1つだけで直下に複数の子ノードがある場合は、実務上第2階層（実質的な部署群）で分割する
            List<SimFolderNode> unitNodes;
            if (nodes.Count == 1 && nodes[0].Children.Count > 1 && options.Policy != MigrationSplitPolicy.SingleBatch)
            {
                unitNodes = nodes[0].Children.ToList();
            }
            else
            {
                unitNodes = nodes;
            }

            var plans = new List<MigrationWavePlan>();

            if (options.Policy == MigrationSplitPolicy.SingleBatch)
            {
                var p = CreateWavePlan(1, "Wave 1: 全社一括移行 (All Units)", unitNodes);
                plans.Add(p);
            }
            else if (options.Policy == MigrationSplitPolicy.BySizeBudget)
            {
                int waveIndex = 1;
                var currentBucket = new List<SimFolderNode>();
                long currentBucketSize = 0;

                foreach (var node in unitNodes)
                {
                    long nodeSize = CalculateRecursiveSize(node);
                    if (currentBucket.Count > 0 && (currentBucketSize + nodeSize) > options.SizeBudgetBytes)
                    {
                        string waveName = $"Wave {waveIndex}: {string.Join(" + ", currentBucket.Take(2).Select(n => n.Name))}" +
                                          (currentBucket.Count > 2 ? $" 外{currentBucket.Count - 2}部署" : "");
                        plans.Add(CreateWavePlan(waveIndex++, waveName, currentBucket));
                        currentBucket = new List<SimFolderNode>();
                        currentBucketSize = 0;
                    }
                    currentBucket.Add(node);
                    currentBucketSize += nodeSize;
                }

                if (currentBucket.Count > 0)
                {
                    string waveName = $"Wave {waveIndex}: {string.Join(" + ", currentBucket.Take(2).Select(n => n.Name))}" +
                                      (currentBucket.Count > 2 ? $" 外{currentBucket.Count - 2}部署" : "");
                    plans.Add(CreateWavePlan(waveIndex, waveName, currentBucket));
                }
            }
            else // ByTopLevelFolder (既定: 部署・トップフォルダごと)
            {
                int waveIndex = 1;
                foreach (var node in unitNodes)
                {
                    string waveName = $"Wave {waveIndex}: {node.Name}";
                    plans.Add(CreateWavePlan(waveIndex++, waveName, new List<SimFolderNode> { node }));
                }
            }

            return plans;
        }

        private static MigrationWavePlan CreateWavePlan(int waveNum, string waveName, List<SimFolderNode> targetNodes)
        {
            long totalBytes = 0;
            long totalFiles = 0;
            var mappedSources = new List<string>();

            foreach (var node in targetNodes)
            {
                totalBytes += CalculateRecursiveSize(node);
                totalFiles += CalculateRecursiveFiles(node);
                CollectAllMappedSources(node, mappedSources);
            }

            double fullSeconds = (double)totalBytes / TransferRateBytesPerSec;
            var fullTime = TimeSpan.FromSeconds(Math.Max(5, (int)fullSeconds));

            // 差分同期（差分2%想定、最小5秒）
            double cutoverSeconds = (double)(totalBytes * 0.02) / TransferRateBytesPerSec;
            var cutoverTime = TimeSpan.FromSeconds(Math.Max(5, (int)cutoverSeconds));

            return new MigrationWavePlan
            {
                WaveNumber = waveNum,
                WaveName = waveName,
                TargetNodes = targetNodes,
                TotalSizeBytes = totalBytes,
                TotalFileCount = totalFiles,
                EstimatedFullCopyTime = fullTime,
                EstimatedCutoverTime = cutoverTime,
                MappedSourcePaths = mappedSources.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private static long CalculateRecursiveSize(SimFolderNode node)
        {
            long size = node.EstimatedSizeBytes;
            foreach (var child in node.Children)
            {
                size += CalculateRecursiveSize(child);
            }
            return size;
        }

        private static long CalculateRecursiveFiles(SimFolderNode node)
        {
            long count = Math.Max(1, node.EstimatedSizeBytes / (10L * 1024 * 1024));
            foreach (var child in node.Children)
            {
                count += CalculateRecursiveFiles(child);
            }
            return count;
        }

        private static void CollectAllMappedSources(SimFolderNode node, List<string> accumulator)
        {
            foreach (var s in node.MappedSourcePaths)
            {
                if (!string.IsNullOrWhiteSpace(s)) accumulator.Add(s);
            }
            foreach (var child in node.Children)
            {
                CollectAllMappedSources(child, accumulator);
            }
        }

        /// <summary>
        /// 指定されたディレクトリに、ベンダー標準の移行パッケージ一式（バッチ群、安全弁、手順書）を生成します。
        /// </summary>
        public async Task<string> GeneratePackageAsync(
            IEnumerable<SimFolderNode> rootNodes,
            MigrationPackageOptions options,
            IProgress<string>? progress = null)
        {
            return await Task.Run(() =>
            {
                var wavePlans = PlanWaves(rootNodes, options);
                if (wavePlans.Count == 0) throw new InvalidOperationException("移行対象のフォルダーが存在しません。");

                string targetRoot = string.IsNullOrWhiteSpace(options.TargetRoot) ? @"\\NewServer\Share" : options.TargetRoot;
                string cleanTargetName = Path.GetFileName(targetRoot.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(cleanTargetName)) cleanTargetName = "Target";

                string packageDir = Path.Combine(options.OutputDirectory, $"Migration_Package_{cleanTargetName}_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(packageDir);

                string logsDir = Path.Combine(packageDir, "Logs");
                Directory.CreateDirectory(logsDir);

                progress?.Report("移行バッチ群を生成中...");

                var waveBatEntries = new List<(string WaveName, string FullBat, string CutoverBat)>();

                // 各Waveのスクリプト生成
                foreach (var wave in wavePlans)
                {
                    string safeWaveFolderName = $"Wave{wave.WaveNumber:D2}_{SanitizeFileName(wave.WaveName.Replace($"Wave {wave.WaveNumber}:", "").Trim())}";
                    string waveDir = Path.Combine(packageDir, safeWaveFolderName);
                    Directory.CreateDirectory(waveDir);

                    // 1. 01_Baseline_Sync.bat (事前フル同期)
                    string baselineBat = Path.Combine(waveDir, "01_Baseline_Sync.bat");
                    string baselineContent = GenerateWaveRobocopyBat(wave, targetRoot, options, mode: "BASELINE");
                    File.WriteAllText(baselineBat, baselineContent, new UTF8Encoding(false));

                    // 2. 02_Delta_Sync.bat (中間差分同期)
                    string deltaBat = Path.Combine(waveDir, "02_Delta_Sync.bat");
                    string deltaContent = GenerateWaveRobocopyBat(wave, targetRoot, options, mode: "DELTA");
                    File.WriteAllText(deltaBat, deltaContent, new UTF8Encoding(false));

                    // 3. 03_Lock_OldShare_ReadOnly.bat (旧共有書き込み封鎖)
                    if (options.IncludeOldShareLock)
                    {
                        string lockBat = Path.Combine(waveDir, "03_Lock_OldShare_ReadOnly.bat");
                        string lockContent = GenerateOldShareLockBat(wave, isLock: true);
                        File.WriteAllText(lockBat, lockContent, new UTF8Encoding(false));

                        // 99_ROLLBACK_RestoreOldShare.bat (旧共有書き込み復旧)
                        string rollbackBat = Path.Combine(waveDir, "99_ROLLBACK_RestoreOldShare.bat");
                        string rollbackContent = GenerateOldShareLockBat(wave, isLock: false);
                        File.WriteAllText(rollbackBat, rollbackContent, new UTF8Encoding(false));
                    }

                    // 4. 04_Final_Cutover_Mirror.bat (本番最終ミラー)
                    string cutoverBat = Path.Combine(waveDir, "04_Final_Cutover_Mirror.bat");
                    string cutoverContent = GenerateWaveRobocopyBat(wave, targetRoot, options, mode: "CUTOVER");
                    File.WriteAllText(cutoverBat, cutoverContent, new UTF8Encoding(false));

                    waveBatEntries.Add((wave.WaveName, baselineBat, cutoverBat));
                }

                // 00_Run_All_Waves_StepByStep.bat (マスター対話実行バッチ)
                progress?.Report("マスター実行スクリプトを生成中...");
                string masterBat = Path.Combine(packageDir, "00_Run_All_Waves_StepByStep.bat");
                string masterContent = GenerateMasterOrchestratorBat(waveBatEntries);
                File.WriteAllText(masterBat, masterContent, new UTF8Encoding(false));

                // README_MIGRATION_GUIDE.md (手順書ガイド)
                progress?.Report("移行ガイド手順書を生成中...");
                string guideMd = Path.Combine(packageDir, "README_MIGRATION_GUIDE.md");
                string guideContent = GenerateReadmeGuide(wavePlans, targetRoot, options);
                File.WriteAllText(guideMd, guideContent, Encoding.UTF8);

                // Migration_Runbook.xlsx (Excel移行計画書・進捗台帳)
                if (options.IncludeRunbookExcel)
                {
                    progress?.Report("Excel移行計画台帳 (Migration_Runbook.xlsx) を生成中...");
                    string excelPath = Path.Combine(packageDir, "Migration_Runbook.xlsx");
                    GenerateRunbookExcel(excelPath, wavePlans, targetRoot, options);
                }

                progress?.Report("移行パッケージの生成が完了しました。");
                return packageDir;
            });
        }

        private static string GenerateWaveRobocopyBat(
            MigrationWavePlan wave,
            string targetRoot,
            MigrationPackageOptions options,
            string mode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("setlocal EnableDelayedExpansion");
            sb.AppendLine();
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine($"rem FolderMorpher Enterprise Migration Suite");
            sb.AppendLine($"rem Wave: {wave.WaveName}");
            sb.AppendLine($"rem Mode: {mode} ({(mode == "BASELINE" ? "事前フル同期" : mode == "DELTA" ? "中間差分同期" : "最終本番切替ミラー")})");
            sb.AppendLine($"rem Target Root: {targetRoot}");
            sb.AppendLine($"rem Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine();
            sb.AppendLine($"echo ==================================================================");
            sb.AppendLine($"echo   [FolderMorpher] {wave.WaveName}");
            sb.AppendLine($"echo   実行モード: {mode}");
            sb.AppendLine($"echo   想定容量  : {wave.TotalSizeFormatted} ({wave.TotalFileCountFormatted})");
            sb.AppendLine($"echo ==================================================================");
            sb.AppendLine("echo.");
            sb.AppendLine("echo 処理を開始するには何かキーを押してください。中断する場合は Ctrl+C を押してください。");
            sb.AppendLine("pause > nul");
            sb.AppendLine();

            string copyFlag = options.CopyAcl ? "/COPYALL" : "/COPY:DAT";
            string modeFlag = mode == "CUTOVER" ? "/MIR" : "/E";
            int retryCount = mode == "BASELINE" ? 1 : 2;
            int waitSec = mode == "BASELINE" ? 1 : (mode == "DELTA" ? 2 : 3);
            int threads = mode == "BASELINE" ? Math.Max(16, options.Threads) : 8;

            sb.AppendLine($"set LOG_DIR=..\\Logs");
            sb.AppendLine($"if not exist \"%LOG_DIR%\" mkdir \"%LOG_DIR%\"");
            sb.AppendLine($"set LOG_FILE=%LOG_DIR%\\Robo_{mode}_Wave{wave.WaveNumber:D2}_%date:~0,4%%date:~5,2%%date:~8,2%_%time:~0,2%%time:~3,2%%time:~6,2%.log");
            sb.AppendLine($"set LOG_FILE=%LOG_FILE: =0%");
            sb.AppendLine();
            sb.AppendLine("set HAS_ERROR=0");
            sb.AppendLine();

            void CollectDescendants(SimFolderNode parent, List<string> accumulator)
            {
                foreach (var child in parent.Children)
                {
                    foreach (var s in child.MappedSourcePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(s)) accumulator.Add(s);
                    }
                    CollectDescendants(child, accumulator);
                }
            }

            bool IsSubPath(string parent, string child)
            {
                try
                {
                    var p = Path.GetFullPath(parent).TrimEnd('\\', '/');
                    var c = Path.GetFullPath(child).TrimEnd('\\', '/');
                    return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            void AppendRoboCommands(SimFolderNode node, string currentTarget)
            {
                var targetFolder = Path.Combine(currentTarget, node.Name);
                var descendantSources = new List<string>();
                CollectDescendants(node, descendantSources);

                foreach (var src in node.MappedSourcePaths)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;

                    var excludedDirs = descendantSources
                        .Where(ds => IsSubPath(src, ds))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var escSrc = ScriptEscaper.EscapeBatPath(src);
                    var escDst = ScriptEscaper.EscapeBatPath(targetFolder);
                    var xdParam = excludedDirs.Count > 0
                        ? " /XD " + string.Join(" ", excludedDirs.Select(d => ScriptEscaper.EscapeBatPath(d)))
                        : "";

                    sb.AppendLine($"echo ------------------------------------------------------------------");
                    sb.AppendLine($"echo [転送中] {src}  --->  {targetFolder}");
                    sb.AppendLine($"echo ------------------------------------------------------------------");
                    sb.AppendLine($"robocopy {escSrc} {escDst} {modeFlag} {copyFlag} /DCOPY:DAT /R:{retryCount} /W:{waitSec} /NP /MT:{threads}{xdParam} /TEE /LOG+:\"%LOG_FILE%\"");
                    sb.AppendLine($"if errorlevel 8 (");
                    sb.AppendLine($"    echo [ERROR] 重大なエラーが発生しました。ログを確認してください: %LOG_FILE%");
                    sb.AppendLine($"    set HAS_ERROR=1");
                    sb.AppendLine($") else if errorlevel 4 (");
                    sb.AppendLine($"    echo [WARNING] 一部の不一致・アクセス拒否が検出されました。");
                    sb.AppendLine($") else (");
                    sb.AppendLine($"    echo [OK] 転送成功");
                    sb.AppendLine($")");
                    sb.AppendLine();
                }

                foreach (var child in node.Children)
                {
                    AppendRoboCommands(child, targetFolder);
                }
            }

            foreach (var targetNode in wave.TargetNodes)
            {
                string startTarget = targetNode.Parent != null
                    ? Path.Combine(targetRoot, targetNode.Parent.Name)
                    : targetRoot;
                AppendRoboCommands(targetNode, startTarget);
            }

            sb.AppendLine("echo.");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("if %HAS_ERROR% equ 0 (");
            sb.AppendLine($"    echo   [SUCCESS] {wave.WaveName} の {mode} 処理が正常に完了しました。");
            sb.AppendLine(") else (");
            sb.AppendLine($"    echo   [FAILED] エラーが検出されました。ログを確認してください: %LOG_FILE%");
            sb.AppendLine(")");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("pause");

            return sb.ToString();
        }

        private static string GenerateOldShareLockBat(MigrationWavePlan wave, bool isLock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine();
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine($"rem FolderMorpher - 旧環境アクセス権 {(isLock ? "書き込み停止 (ReadOnly化)" : "緊急切戻し (元のアクセス権復元)")}");
            sb.AppendLine($"rem Wave: {wave.WaveName}");
            sb.AppendLine($"rem Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine();
            sb.AppendLine($"echo ==================================================================");
            if (isLock)
            {
                sb.AppendLine($"echo   【注意】本番切替直前の「旧共有 書き込み封鎖」を実行します。");
                sb.AppendLine($"echo   ユーザーが旧環境を更新して先祖返りするのを防ぐため、");
                sb.AppendLine($"echo   一般ユーザーの書き込み権限を一時的に拒否(Deny)します。");
            }
            else
            {
                sb.AppendLine($"echo   【緊急切戻し】旧共有の書き込み権限を復旧します。");
                sb.AppendLine($"echo   封鎖用Deny ACEを削除し、元のアクセス状態へ戻します。");
            }
            sb.AppendLine($"echo ==================================================================");
            sb.AppendLine("pause");
            sb.AppendLine();

            foreach (var src in wave.MappedSourcePaths)
            {
                var escPath = ScriptEscaper.EscapeBatPath(src);
                if (isLock)
                {
                    sb.AppendLine($"echo [{src}] 書き込み停止Denyを適用中...");
                    sb.AppendLine($"icacls {escPath} /deny \"Domain Users\":(WD,AD,WA) /T /C");
                }
                else
                {
                    sb.AppendLine($"echo [{src}] 書き込み停止Denyを解除中...");
                    sb.AppendLine($"icacls {escPath} /remove:d \"Domain Users\" /T /C");
                }
                sb.AppendLine();
            }

            sb.AppendLine("echo 完了しました。");
            sb.AppendLine("pause");
            return sb.ToString();
        }

        private static string GenerateMasterOrchestratorBat(List<(string WaveName, string FullBat, string CutoverBat)> waveBats)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine();
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine("rem FolderMorpher - マスター移行オーケストレーター (順次対話実行)");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine();
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("echo   FolderMorpher - 全Wave 順次移行メニュー");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("echo   1. 事前フル同期 (Baseline Sync) を順次実行");
            sb.AppendLine("echo   2. 本番カットオーバー (Final Cutover /MIR) を順次実行");
            sb.AppendLine("echo   3. 終了");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("set /p M_CHOICE=\"選択してください [1-3]: \"");
            sb.AppendLine();
            sb.AppendLine("if \"%M_CHOICE%\"==\"1\" goto RUN_BASELINE");
            sb.AppendLine("if \"%M_CHOICE%\"==\"2\" goto RUN_CUTOVER");
            sb.AppendLine("goto END");
            sb.AppendLine();

            sb.AppendLine(":RUN_BASELINE");
            foreach (var w in waveBats)
            {
                var dirName = Path.GetFileName(Path.GetDirectoryName(w.FullBat)!);
                sb.AppendLine($"echo.");
                sb.AppendLine($"echo 次のWaveを実行します: {w.WaveName}");
                sb.AppendLine($"set /p W_EXEC=\"実行しますか？ (Y/N/Skip): \"");
                sb.AppendLine($"if /i \"%W_EXEC%\"==\"Y\" (");
                sb.AppendLine($"    call \"{dirName}\\{Path.GetFileName(w.FullBat)}\"");
                sb.AppendLine($")");
            }
            sb.AppendLine("goto END");
            sb.AppendLine();

            sb.AppendLine(":RUN_CUTOVER");
            foreach (var w in waveBats)
            {
                var dirName = Path.GetFileName(Path.GetDirectoryName(w.CutoverBat)!);
                sb.AppendLine($"echo.");
                sb.AppendLine($"echo 【本番切替】次のWaveを実行します: {w.WaveName}");
                sb.AppendLine($"set /p W_EXEC=\"実行しますか？ (Y/N/Skip): \"");
                sb.AppendLine($"if /i \"%W_EXEC%\"==\"Y\" (");
                sb.AppendLine($"    call \"{dirName}\\{Path.GetFileName(w.CutoverBat)}\"");
                sb.AppendLine($")");
            }
            sb.AppendLine("goto END");
            sb.AppendLine();

            sb.AppendLine(":END");
            sb.AppendLine("echo 全工程が終了しました。");
            sb.AppendLine("pause");
            return sb.ToString();
        }

        private static string GenerateReadmeGuide(List<MigrationWavePlan> wavePlans, string targetRoot, MigrationPackageOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 🚀 FolderMorpher ファイルサーバー移行実行ガイド (Runbook Guide)");
            sb.AppendLine();
            sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ");
            sb.AppendLine($"新環境ルート: `{targetRoot}`  ");
            sb.AppendLine($"転送モード: {(options.CopyAcl ? "旧ACL維持 (/COPYALL)" : "新設計ACL適用・データのみ転送 (/COPY:DAT)")}  ");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 1. 移行標準 5大フェーズ手順");
            sb.AppendLine();
            sb.AppendLine("### Phase 1: 事前フル同期（Baseline Sync）");
            sb.AppendLine("- **実施時期**: 移行予定日の 2〜4 週間前の平日夜間または休日");
            sb.AppendLine("- **実行スクリプト**: 各Waveフォルダ直下の `01_Baseline_Sync.bat`");
            sb.AppendLine("- **内容**: 全体の95%以上のデータを先行転送します。`/R:1 /W:1` によりロック中ファイルで止まらず高速に完了します。");
            sb.AppendLine();
            sb.AppendLine("### Phase 2: 中間差分同期（Delta Sync）");
            sb.AppendLine("- **実施時期**: 本番切替の数日前〜前日夜間");
            sb.AppendLine("- **実行スクリプト**: 各Waveフォルダ直下の `02_Delta_Sync.bat`");
            sb.AppendLine("- **内容**: 初回フルコピー以降に更新・追加された差分データだけを追いつかせます。");
            sb.AppendLine();
            sb.AppendLine("### Phase 3: 旧共有の安全封鎖（Freeze & Lock）");
            sb.AppendLine("- **実施時期**: 本番切替当日（業務停止直後）");
            sb.AppendLine("- **実行スクリプト**: `03_Lock_OldShare_ReadOnly.bat`");
            sb.AppendLine("- **内容**: ユーザーが旧環境を誤編集して先祖返りするのを防ぐため、書き込み権限を一時的に停止します。");
            sb.AppendLine();
            sb.AppendLine("### Phase 4: 最終カットオーバー同期（Final Cutover /MIR）");
            sb.AppendLine("- **実施時期**: 本番切替当日（旧共有封鎖完了後）");
            sb.AppendLine("- **実行スクリプト**: `04_Final_Cutover_Mirror.bat`");
            sb.AppendLine("- **内容**: `/MIR` により旧環境で削除されたファイルも新環境へ反映し、完全一致（ミラー）化します。事前同期済みのため数分〜数十分で終わります。");
            sb.AppendLine();
            sb.AppendLine("### Emergency: 緊急切り戻し（Rollback）");
            sb.AppendLine("- **実施時期**: 万が一新環境への切替を中止し旧環境で業務再開する場合");
            sb.AppendLine("- **実行スクリプト**: `99_ROLLBACK_RestoreOldShare.bat`");
            sb.AppendLine("- **内容**: 旧環境の書き込み権限を元の状態へ即座に復旧します。");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 2. Wave（波次）別 計画一覧");
            sb.AppendLine();
            sb.AppendLine("| Wave | 対象ユニット | 想定容量 | 想定ファイル数 | 初回フル見積 | 当日差分見積 | 警告・留意事項 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (var w in wavePlans)
            {
                string warn = "";
                if (w.HasOver48hWarning) warn += "⚠️ 週末枠超過リスク ";
                if (w.HasHighFileCountWarning) warn += "⚠️ 小ファイル過多(/MT:32推奨) ";
                if (string.IsNullOrEmpty(warn)) warn = "―";

                sb.AppendLine($"| Wave {w.WaveNumber} | {w.WaveName.Replace($"Wave {w.WaveNumber}:", "").Trim()} | {w.TotalSizeFormatted} | {w.TotalFileCountFormatted} | {w.FullCopyTimeFormatted} | {w.CutoverTimeFormatted} | {warn} |");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 3. Robocopy 終了コードの判定基準");
            sb.AppendLine("- **0**: 変更なし（新旧完全一致） ➔ 正常");
            sb.AppendLine("- **1**: 正常にファイルがコピーされた ➔ 正常");
            sb.AppendLine("- **2 / 3**: 余分なファイルまたは差分が存在した ➔ 正常");
            sb.AppendLine("- **4 / 8 / 16**: 一部アクセス拒否または重大なエラー ➔ ログを確認してください");
            sb.AppendLine();

            return sb.ToString();
        }

        private static void GenerateRunbookExcel(
            string excelPath,
            List<MigrationWavePlan> wavePlans,
            string targetRoot,
            MigrationPackageOptions options)
        {
            var excelService = new ExcelReportService();
            excelService.GenerateMigrationRunbook(excelPath, wavePlans, targetRoot, options);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}