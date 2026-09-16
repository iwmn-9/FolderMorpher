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
                var p = CreateWavePlan(1, "Wave 1: 全社一括移行 (All Units)", unitNodes, options);
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
                        plans.Add(CreateWavePlan(waveIndex++, waveName, currentBucket, options));
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
                    plans.Add(CreateWavePlan(waveIndex, waveName, currentBucket, options));
                }
            }
            else // ByTopLevelFolder (既定: 部署・トップフォルダごと)
            {
                int waveIndex = 1;
                foreach (var node in unitNodes)
                {
                    string waveName = $"Wave {waveIndex}: {node.Name}";
                    plans.Add(CreateWavePlan(waveIndex++, waveName, new List<SimFolderNode> { node }, options));
                }
            }

            return plans;
        }

        private static MigrationWavePlan CreateWavePlan(
            int waveNum,
            string waveName,
            List<SimFolderNode> targetNodes,
            MigrationPackageOptions options)
        {
            long totalBytes = 0;
            long? totalFiles = null;
            var mappedSources = new List<string>();

            foreach (var node in targetNodes)
            {
                totalBytes += CalculateRecursiveSize(node);
                var fCount = CalculateRecursiveFiles(node);
                if (fCount.HasValue)
                {
                    totalFiles = (totalFiles ?? 0) + fCount.Value;
                }
                CollectAllMappedSources(node, mappedSources);
            }

            long rateBytes = (long)Math.Max(1024.0 * 1024.0, options.TransferRateMBps * 1024.0 * 1024.0);
            double fullSeconds = (double)totalBytes / rateBytes;
            var fullTime = TimeSpan.FromSeconds(Math.Max(5, (int)fullSeconds));

            // 差分同期（指定差分率 想定、最小5秒）
            double deltaRatio = Math.Clamp(options.DeltaRatioPercent, 0.01, 100.0) / 100.0;
            double cutoverSeconds = (double)(totalBytes * deltaRatio) / rateBytes;
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
            if (node.Children.Count == 0) return node.EstimatedSizeBytes;
            long childrenSum = 0;
            foreach (var child in node.Children)
            {
                childrenSum += CalculateRecursiveSize(child);
            }
            // 親ノードが子孫を含む全体値を持つ場合は childrenSum との大きい方を採用して二重加算を防止
            return Math.Max(node.EstimatedSizeBytes, childrenSum);
        }

        private static long? CalculateRecursiveFiles(SimFolderNode node)
        {
            if (node.Children.Count == 0) return node.EstimatedFileCount;
            long childrenSum = 0;
            bool hasAnyChild = false;
            foreach (var child in node.Children)
            {
                var c = CalculateRecursiveFiles(child);
                if (c.HasValue)
                {
                    childrenSum += c.Value;
                    hasAnyChild = true;
                }
            }

            if (node.EstimatedFileCount.HasValue)
            {
                return Math.Max(node.EstimatedFileCount.Value, childrenSum);
            }
            return hasAnyChild ? childrenSum : null;
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

                if (string.IsNullOrWhiteSpace(options.TargetRoot))
                {
                    throw new InvalidOperationException("移行先ルートパス (TargetRoot) が指定されていません。移行先のUNCパスまたは絶対パスを入力してください。");
                }
                string targetRoot = options.TargetRoot.Trim();
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

                    // 3. 03_PreCutover_Freeze_Guide.md (旧共有書き込み停止ガイド手順書)
                    if (options.IncludeOldShareLock)
                    {
                        string guideDoc = Path.Combine(waveDir, "03_PreCutover_Freeze_Guide.md");
                        string guideDocContent = GeneratePreCutoverFreezeGuide(wave);
                        File.WriteAllText(guideDoc, guideDocContent, Encoding.UTF8);
                    }

                    // 4. 04_Final_Cutover_DRYRUN.bat (本番最終ミラー Dry-Run シミュレーション /L) (Sol指摘)
                    string dryRunBat = Path.Combine(waveDir, "04_Final_Cutover_DRYRUN.bat");
                    string dryRunContent = GenerateWaveRobocopyBat(wave, targetRoot, options, mode: "CUTOVER", dryRun: true);
                    File.WriteAllText(dryRunBat, dryRunContent, new UTF8Encoding(false));

                    // 5. 04_Final_Cutover_Mirror.bat (本番最終ミラー)
                    string cutoverBat = Path.Combine(waveDir, "04_Final_Cutover_Mirror.bat");
                    string cutoverContent = GenerateWaveRobocopyBat(wave, targetRoot, options, mode: "CUTOVER", dryRun: false);
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
            string mode,
            bool dryRun = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine();
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine($"rem FolderMorpher Enterprise Migration Suite");
            sb.AppendLine($"rem Wave: {wave.WaveName}");
            sb.AppendLine($"rem Mode: {mode} ({(mode == "BASELINE" ? "事前フル同期" : mode == "DELTA" ? "中間差分同期" : dryRun ? "最終本番切替 Dry-Run" : "最終本番切替ミラー")})");
            sb.AppendLine($"rem Target Root: {targetRoot}");
            sb.AppendLine($"rem Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine();
            sb.AppendLine($"echo ==================================================================");
            sb.AppendLine($"echo   [FolderMorpher] {wave.WaveName}");
            sb.AppendLine($"echo   実行モード: {mode}{(dryRun ? " 【DRY-RUN (シミュレーション・書き込みなし)】" : "")}");
            sb.AppendLine($"echo   想定容量  : {wave.TotalSizeFormatted} ({wave.TotalFileCountFormatted})");
            sb.AppendLine($"echo ==================================================================");
            sb.AppendLine("echo.");
            sb.AppendLine("echo 処理を開始するには何かキーを押してください。中断する場合は Ctrl+C を押してください。");
            sb.AppendLine("pause > nul");
            sb.AppendLine();

            string copyFlag = options.CopyAcl ? "/COPYALL" : "/COPY:DAT";
            string modeFlag = mode == "CUTOVER" ? "/MIR" : "/E";
            string dryRunParam = dryRun ? " /L" : "";
            int retryCount = mode == "BASELINE" ? 1 : 2;
            int waitSec = mode == "BASELINE" ? 1 : (mode == "DELTA" ? 2 : 3);
            int threads = mode == "BASELINE" ? Math.Max(16, options.Threads) : 8;

            sb.AppendLine($"set LOG_DIR=%~dp0..\\Logs");
            sb.AppendLine($"if not exist \"%LOG_DIR%\" mkdir \"%LOG_DIR%\"");
            sb.AppendLine($"set LOG_FILE=%LOG_DIR%\\Robo_{mode}{(dryRun ? "_DRYRUN" : "")}_Wave{wave.WaveNumber:D2}_%date:~0,4%%date:~5,2%%date:~8,2%_%time:~0,2%%time:~3,2%%time:~6,2%.log");
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
                    sb.AppendLine($"echo [転送中] {src}  --->  {targetFolder}{(dryRun ? " [DRY-RUN /L]" : "")}");
                    sb.AppendLine($"echo ------------------------------------------------------------------");
                    sb.AppendLine($"robocopy {escSrc} {escDst} {modeFlag} {copyFlag} /DCOPY:DAT /R:{retryCount} /W:{waitSec} /NP /MT:{threads}{xdParam}{dryRunParam} /TEE /LOG+:\"%LOG_FILE%\"");
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
            sb.AppendLine($"    echo   [SUCCESS] {wave.WaveName} の {mode}{(dryRun ? " (DRY-RUN)" : "")} 処理が正常に完了しました。");
            sb.AppendLine(") else (");
            sb.AppendLine($"    echo   [FAILED] エラーが検出されました。ログを確認してください: %LOG_FILE%");
            sb.AppendLine(")");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("pause");
            sb.AppendLine("if %HAS_ERROR% neq 0 (");
            sb.AppendLine("    exit /b 1");
            sb.AppendLine(")");

            return sb.ToString();
        }

        private static string GeneratePreCutoverFreezeGuide(MigrationWavePlan wave)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 🔒 本番切替前 旧共有書き込み停止ガイド (Pre-Cutover Freeze Guide)");
            sb.AppendLine();
            sb.AppendLine($"- **対象 Wave**: {wave.WaveName}");
            sb.AppendLine($"- **生成日時**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 1. 目的と注意点");
            sb.AppendLine("本番カットオーバー直前のデータ先祖返りを防ぐため、一般利用者の旧共有への書き込みを停止します。");
            sb.AppendLine("一括バッチによる安易なアクセス権変更（icacls /deny 等）は、既存アクセス権の意図せぬ喪失や環境依存トラブルの原因となるため推奨されません。");
            sb.AppendLine("以下のエンタープライズ標準手法から、組織の運用設計に合致する方法を選択して実施してください。");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 2. 推奨停止手順（以下のいずれかを実施）");
            sb.AppendLine();
            sb.AppendLine("### 【方法 A】SMB共有アクセス権の変更（最も安全・即時反映・NTFS非破壊・推奨）");
            sb.AppendLine("旧サーバーの共有フォルダ自体のアクセス権（Share Permission）を「読み取り」に変更するか、一般ユーザーグループを削除します。NTFSファイル自体のACLを変更しないため、切戻しも最も安全です。");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("# 例: PowerShell で共有アクセス権を読み取り専用に変更する場合");
            sb.AppendLine("# Revoke-SmbShareAccess -Name \"<共有名>\" -AccountName \"Domain Users\" -Force");
            sb.AppendLine("# Grant-SmbShareAccess -Name \"<共有名>\" -AccountName \"Domain Users\" -AccessRight Read -Force");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### 【方法 B】SMBセッションの強制切断 & ファイルオープン解消");
            sb.AppendLine("書き込み停止前に、利用者が現在開いているファイルハンドルや接続セッションを切断します。");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("# 既存のSMBセッションを切断 (管理者PowerShell)");
            sb.AppendLine("Get-SmbSession | Close-SmbSession -Force");
            sb.AppendLine("# 開いているファイルハンドルを閉じる");
            sb.AppendLine("Get-SmbOpenFile | Close-SmbOpenFile -Force");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### 【方法 C】DFS名前空間のターゲット無効化（DFS利用時）");
            sb.AppendLine("DFSを利用している場合は、旧サーバーのフォルダターゲットを「無効（Offline）」にします。");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("# 例: Set-DfsnFolderTarget -Path \"\\\\domain\\dfs\\target\" -TargetPath \"\\\\OldServer\\Share\" -State Offline");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 3. 本Waveで移行対象となっている旧共有パス一覧");
            sb.AppendLine("確認用として、本Waveに含まれる移行元UNCパスを以下に列挙します。");
            sb.AppendLine();
            foreach (var src in wave.MappedSourcePaths)
            {
                sb.AppendLine($"- `{src}`");
            }
            if (wave.MappedSourcePaths.Count == 0)
            {
                sb.AppendLine("- （直接マッピングされた旧環境UNCパスはありません）");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 4. 緊急切戻し手順（Rollback）");
            sb.AppendLine("万が一、本番切替を中断して旧環境での業務を再開する場合は、上記で変更した共有アクセス権またはDFSターゲットを速やかに元に戻してください。");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("# 方法 A を元に戻す例: 共有アクセス権のフルコントロール/変更を再付与");
            sb.AppendLine("# Grant-SmbShareAccess -Name \"<共有名>\" -AccountName \"Domain Users\" -AccessRight Change -Force");
            sb.AppendLine("```");
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
                sb.AppendLine($"    call \"%~dp0{dirName}\\{Path.GetFileName(w.FullBat)}\"");
                sb.AppendLine($"    if errorlevel 1 (");
                sb.AppendLine($"        echo.");
                sb.AppendLine($"        echo [ABORT] {w.WaveName} の実行でエラーが検出されたため、後続のWaveを安全停止しました。");
                sb.AppendLine($"        pause");
                sb.AppendLine($"        exit /b 1");
                sb.AppendLine($"    )");
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
                sb.AppendLine($"    call \"%~dp0{dirName}\\{Path.GetFileName(w.CutoverBat)}\"");
                sb.AppendLine($"    if errorlevel 1 (");
                sb.AppendLine($"        echo.");
                sb.AppendLine($"        echo [ABORT] {w.WaveName} の本番切替でエラーが検出されたため、後続のWaveを安全停止しました。");
                sb.AppendLine($"        pause");
                sb.AppendLine($"        exit /b 1");
                sb.AppendLine($"    )");
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
            sb.AppendLine("### Phase 3: 旧共有の安全停止（Freeze & Lock）");
            sb.AppendLine("- **実施時期**: 本番切替当日（業務停止直後）");
            sb.AppendLine("- **参照手順書**: 各Waveフォルダ直下の `03_PreCutover_Freeze_Guide.md`");
            sb.AppendLine("- **内容**: ユーザーが旧環境を誤編集して先祖返りするのを防ぐため、共有アクセス権の変更またはセッション切断により書き込みを安全に停止します。");
            sb.AppendLine();
            sb.AppendLine("### Phase 4: 最終カットオーバー同期（Final Cutover /MIR）");
            sb.AppendLine("- **実施時期**: 本番切替当日（旧共有停止完了後）");
            sb.AppendLine("- **実行スクリプト**: `04_Final_Cutover_Mirror.bat`");
            sb.AppendLine("- **内容**: `/MIR` により旧環境で削除されたファイルも新環境へ反映し、完全一致（ミラー）化します。事前同期済みのため数分〜数十分で終わります。");
            sb.AppendLine();
            sb.AppendLine("### Emergency: 緊急切り戻し（Rollback）");
            sb.AppendLine("- **実施時期**: 万が一新環境への切替を中止し旧環境で業務再開する場合");
            sb.AppendLine("- **参照手順書**: `03_PreCutover_Freeze_Guide.md` 内「4. 緊急切戻し手順」");
            sb.AppendLine("- **内容**: 共有アクセス権またはDFSターゲットを速やかに元に戻し、旧環境での書き込み権限を復旧します。");
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