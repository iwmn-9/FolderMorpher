using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    public class MediaOptimizerService
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v" };
        private static readonly string[] MasterExtensions = { ".psd", ".ai", ".raw", ".cr2", ".arw", ".nef", ".dng", ".tiff", ".tif" };

        public async Task<(List<MediaItem> Images, List<MediaItem> Videos)> ScanMediaAsync(
            MediaOptimizeOptions options,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var images = new List<MediaItem>();
                var videos = new List<MediaItem>();

                if (!Directory.Exists(options.TargetDirectory)) return (images, videos);

                var rootDir = new DirectoryInfo(options.TargetDirectory);
                int scanned = 0;
                var dirStack = new Stack<DirectoryInfo>();
                dirStack.Push(rootDir);

                while (dirStack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var currentDir = dirStack.Pop();

                    // 1. サブディレクトリをスタックに積む（アクセス権拒否やジャンクションは安全にスキップ）
                    try
                    {
                        foreach (var sub in currentDir.GetDirectories())
                        {
                            try
                            {
                                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { }
                            dirStack.Push(sub);
                        }
                    }
                    catch { /* アクセス拒否フォルダは安全にスキップ */ }

                    // 2. カレントディレクトリ内のファイルを走査
                    FileInfo[] files;
                    try
                    {
                        files = currentDir.GetFiles();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var fi in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        scanned++;
                        if (scanned % 50 == 0) progress?.Report($"メディア走査中: {scanned} 件...");

                        string ext = fi.Extension.ToLowerInvariant();
                        string fullLower = fi.FullName.ToLowerInvariant();

                        // 1. マスター拡張子判定（自動聖域保護）
                        bool isMasterExt = MasterExtensions.Contains(ext);

                        // 2. フォルダ名キーワード判定（聖域保護）
                        bool isKeywordExcluded = options.ExcludedFolderKeywords.Any(k => fullLower.Contains(k.ToLowerInvariant()));

                        // 3. カスタム除外パス判定
                        bool isCustomExcluded = options.CustomExcludedPaths.Any(p => fullLower.StartsWith(p.ToLowerInvariant()));

                        bool isExcluded = isMasterExt || isKeywordExcluded || isCustomExcluded;
                        string reason = string.Empty;
                        if (isMasterExt) reason = "プロ用マスター拡張子 (.raw/.psd等)";
                        else if (isKeywordExcluded) reason = "保護フォルダ名 (Master/原稿等)";
                        else if (isCustomExcluded) reason = "ユーザー指定の除外フォルダ";

                        // 画像ファイル
                        if (ImageExtensions.Contains(ext) || isMasterExt)
                        {
                            // 指定サイズ未満（例: 2MB未満のアイコンや小画像）はスキップ
                            if (!isExcluded && fi.Length < options.MinImageSizeBytes) continue;

                            images.Add(new MediaItem
                            {
                                FullPath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? string.Empty,
                                Extension = ext,
                                OriginalSizeBytes = fi.Length,
                                IsVideo = false,
                                IsExcluded = isExcluded,
                                ExclusionReason = reason,
                                Status = isExcluded ? $"聖域保護 ({reason})" : "最適化対象"
                            });
                        }
                        // 動画ファイル
                        else if (VideoExtensions.Contains(ext))
                        {
                            videos.Add(new MediaItem
                            {
                                FullPath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? string.Empty,
                                Extension = ext,
                                OriginalSizeBytes = fi.Length,
                                IsVideo = true,
                                IsExcluded = isExcluded,
                                ExclusionReason = reason,
                                Status = isExcluded ? $"聖域保護 ({reason})" : "巨大動画"
                            });
                        }
                    }
                }

                // 動画はサイズ降順にソート（モンスター動画を上位に）
                videos = videos.OrderByDescending(v => v.OriginalSizeBytes).ToList();

                return (images, videos);
            }, ct);
        }

        public async Task<MediaOptimizeSummary> OptimizeImagesAsync(
            List<MediaItem> targets,
            MediaOptimizeOptions options,
            IProgress<(string File, bool Success, string Msg)>? progress,
            CancellationToken ct)
        {
            var summary = new MediaOptimizeSummary
            {
                TotalImagesScanned = targets.Count
            };

            await Task.Run(() =>
            {
                foreach (var item in targets)
                {
                    ct.ThrowIfCancellationRequested();

                    // 聖域保護または処理済みはスキップ
                    if (item.IsExcluded || item.IsProcessed) continue;

                    summary.TotalOriginalBytes += item.OriginalSizeBytes;

                    try
                    {
                        // タイムスタンプ退避
                        var origWriteTime = File.GetLastWriteTime(item.FullPath);
                        var origCreationTime = File.GetCreationTime(item.FullPath);

                        // バックアップ退避先が指定されている場合
                        if (!string.IsNullOrEmpty(options.BackupDirectory))
                        {
                            string rel = Path.GetRelativePath(options.TargetDirectory, item.FullPath);
                            string bakFile = Path.Combine(options.BackupDirectory, rel);
                            string? bakDir = Path.GetDirectoryName(bakFile);
                            if (!string.IsNullOrEmpty(bakDir) && !Directory.Exists(bakDir))
                                Directory.CreateDirectory(bakDir);
                            File.Copy(item.FullPath, bakFile, true);
                        }

                        // WIC による安全なリサイズ ＆ JPEG再エンコード
                        long newSize = OptimizeSingleImage(item.FullPath, options.MaxDimension, options.JpegQuality);

                        // タイムスタンプ復元（重要：日付ソートを維持）
                        File.SetLastWriteTime(item.FullPath, origWriteTime);
                        File.SetCreationTime(item.FullPath, origCreationTime);

                        item.OptimizedSizeBytes = newSize;
                        item.IsProcessed = true;
                        item.Status = $"完了 (削減: {item.ReductionPercent:F0}%)";

                        summary.OptimizedImagesCount++;
                        summary.TotalOptimizedBytes += newSize;

                        progress?.Report((item.FullPath, true, $"最適化完了: {item.ReductionPercent:F0}% 削減"));
                    }
                    catch (Exception ex)
                    {
                        item.Status = $"失敗: {ex.Message}";
                        summary.TotalOptimizedBytes += item.OriginalSizeBytes; // 失敗時はサイズ変動なし
                        progress?.Report((item.FullPath, false, ex.Message));
                    }
                }
            }, ct);

            return summary;
        }

        private static long OptimizeSingleImage(string filePath, int maxDimension, int quality)
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(fileBytes);

            // デコーダー作成
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidOperationException("画像フレームを読み込めませんでした。");

            var frame = decoder.Frames[0];
            int origW = frame.PixelWidth;
            int origH = frame.PixelHeight;

            // アスペクト比計算
            BitmapSource source = frame;
            if (origW > maxDimension || origH > maxDimension)
            {
                double scale = (double)maxDimension / Math.Max(origW, origH);
                int targetW = (int)Math.Round(origW * scale);
                int targetH = (int)Math.Round(origH * scale);

                var transformed = new TransformedBitmap();
                transformed.BeginInit();
                transformed.Source = frame;
                transformed.Transform = new ScaleTransform(scale, scale);
                transformed.EndInit();
                transformed.Freeze();
                source = transformed;
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            BitmapEncoder encoder;
            if (ext == ".png")
            {
                // PNG: 透過（アルファチャンネル）を100%保持した最適化
                encoder = new PngBitmapEncoder
                {
                    Interlace = PngInterlaceOption.Off
                };
            }
            else
            {
                // JPEG: 視覚的ロスレス（Visually Lossless）圧縮
                encoder = new JpegBitmapEncoder
                {
                    QualityLevel = Math.Clamp(quality, 10, 100)
                };
            }

            // Exifメタデータの引き継ぎ
            BitmapMetadata? metadata = null;
            if (frame.Metadata is BitmapMetadata origMeta)
            {
                try
                {
                    metadata = origMeta.Clone() as BitmapMetadata;
                }
                catch { }
            }

            try
            {
                encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
            }
            catch
            {
                // メタデータの互換性エラー時は安全にメタデータなしでフレーム追加
                encoder.Frames.Add(BitmapFrame.Create(source));
            }

            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            byte[] optimizedBytes = outMs.ToArray();

            // もし最適化後の方が大きくなってしまった場合は上書きしない
            if (optimizedBytes.Length >= fileBytes.Length)
            {
                return fileBytes.Length;
            }

            // H3対策: 書き出し前に最適化データが正常な画像か再デコード検証 (破損データの上書き防止)
            using (var verifyMs = new MemoryStream(optimizedBytes))
            {
                var verifyDecoder = BitmapDecoder.Create(verifyMs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (verifyDecoder.Frames.Count == 0 || verifyDecoder.Frames[0].PixelWidth == 0 || verifyDecoder.Frames[0].PixelHeight == 0)
                {
                    throw new InvalidOperationException("最適化後データの画像検証に失敗しました。ファイル破損防止のため上書きを中断しました。");
                }
            }

            // H3対策: 一時ファイル書き出し ➔ アトミック置換 (途中クラッシュや破損からの完全防護)
            string tempPath = filePath + ".tmp_" + Guid.NewGuid().ToString("N");
            string backupPath = filePath + ".orig_" + Guid.NewGuid().ToString("N");

            try
            {
                File.WriteAllBytes(tempPath, optimizedBytes);

                try
                {
                    // 同一ボリューム内でのアトミック置換
                    File.Replace(tempPath, filePath, backupPath);
                    try { File.Delete(backupPath); } catch { }
                }
                catch
                {
                    // File.Replace 非対応環境（UNC共有の一部など）での安全フォールバック
                    File.Copy(tempPath, filePath, overwrite: true);
                }

                return optimizedBytes.Length;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                if (File.Exists(backupPath))
                {
                    try { File.Delete(backupPath); } catch { }
                }
            }
        }

        /// <summary>
        /// 巨大動画の一括夜間GPU圧縮（H.265）用バッチスクリプトを生成する
        /// </summary>
        public void GenerateVideoCompressBatch(string scriptPath, IEnumerable<MediaItem> videos)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine("rem FolderMorpher - 巨大動画 夜間GPU一括圧縮スクリプト (H.265 / HEVC)");
            sb.AppendLine("rem ※ FFmpeg (ffmpeg.exe) がPATHにあるか、同一フォルダに存在する必要があります。");
            sb.AppendLine($"rem 生成日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine("rem ==========================================================================");
            sb.AppendLine();
            sb.AppendLine("echo === 巨大動画の夜間再エンコードを開始します ===");
            sb.AppendLine("echo ※ GPUハードウェアエンコード (NVIDIA / Intel) を優先試行します。");
            sb.AppendLine("pause");
            sb.AppendLine();

            foreach (var v in videos)
            {
                string orig = v.FullPath;
                string dest = Path.Combine(v.DirectoryPath, Path.GetFileNameWithoutExtension(v.FileName) + "_compressed.mp4");

                sb.AppendLine($"echo 処理中: \"{v.FileName}\" ({v.OriginalSizeFormatted})...");
                // NVIDIA NVENC を優先し、不可なら CPU libx265 CRF 26 で圧縮
                sb.AppendLine($"ffmpeg -hide_banner -y -i \"{orig}\" -c:v hevc_nvenc -preset p5 -cq 26 -c:a aac -b:a 128k \"{dest}\" 2>nul || " +
                              $"ffmpeg -hide_banner -y -i \"{orig}\" -c:v libx265 -crf 26 -preset fast -c:a aac -b:a 128k \"{dest}\"");
                sb.AppendLine();
            }

            sb.AppendLine("echo === 全動画の圧縮が完了しました。 ===");
            sb.AppendLine("pause");

            File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
