using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AstraSize.Models;
using AstraSize.Services;
using AstraSize.Services.Mft;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Services.Testing
{
    public static partial class RegressionTestSuite
    {
        /// <summary>
        /// [DOMAIN 5/7] Media Optimizer & LinkFixer 統合テスト
        /// </summary>
        public static async Task TestDomain_MediaOptimizerAndLinkFixerAsync()
        {
            await TestMediaOptimizerPngPreservationAsync();
        }

        public static async Task TestMediaOptimizerPngPreservationAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Media_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string pngPath = Path.Combine(tempDir, "transparent_sample.png");

            try
            {
                // 合成透過 PNG 画像の作成 (400x400, 透過ピクセルを含む Bgra32)
                int width = 400;
                int height = 400;
                int stride = width * 4;
                byte[] rawPixels = new byte[stride * height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * stride) + (x * 4);
                        // 中央 150x150 の領域を完全透過 (Alpha=0) に設定
                        if (x >= 125 && x <= 275 && y >= 125 && y <= 275)
                        {
                            rawPixels[idx + 0] = 0;   // Blue
                            rawPixels[idx + 1] = 0;   // Green
                            rawPixels[idx + 2] = 0;   // Red
                            rawPixels[idx + 3] = 0;   // Alpha (Transparent)
                        }
                        else
                        {
                            // 周囲は半透明カラー
                            rawPixels[idx + 0] = 200; // Blue
                            rawPixels[idx + 1] = 100; // Green
                            rawPixels[idx + 2] = 50;  // Red
                            rawPixels[idx + 3] = 128; // Alpha (Semi-transparent)
                        }
                    }
                }

                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rawPixels, stride);
                var initialEncoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
                initialEncoder.Frames.Add(BitmapFrame.Create(source));
                using (var fs = File.Create(pngPath))
                {
                    initialEncoder.Save(fs);
                }

                long initialSizeBytes = new FileInfo(pngPath).Length;
                if (initialSizeBytes <= 0)
                {
                    throw new InvalidOperationException("テスト用合成 PNG 画像の生成に失敗しました。");
                }

                // MediaOptimizerService を実行 (リサイズ MaxDimension = 200 で確実に再エンコードを発生させる)
                var service = new MediaOptimizerService();
                var options = new MediaOptimizeOptions
                {
                    TargetDirectory = tempDir,
                    MaxDimension = 200,
                    JpegQuality = 75,
                    MinImageSizeBytes = 10
                };

                var mediaItem = new MediaItem
                {
                    FullPath = pngPath,
                    FileName = Path.GetFileName(pngPath),
                    DirectoryPath = tempDir,
                    Extension = ".png",
                    OriginalSizeBytes = initialSizeBytes,
                    IsVideo = false
                };

                var targets = new List<MediaItem> { mediaItem };
                var summary = await service.OptimizeImagesAsync(targets, options, null, CancellationToken.None);

                if (summary.OptimizedImagesCount == 0 && !mediaItem.IsProcessed)
                {
                    throw new InvalidOperationException("MediaOptimizer による PNG の最適化処理がスキップまたは失敗しました。");
                }

                // 1. ファイル先頭の PNG シグネチャ (0x89, 0x50, 0x4E, 0x47) チェック
                byte[] optBytes = File.ReadAllBytes(pngPath);
                if (optBytes.Length < 8)
                {
                    throw new InvalidOperationException($"最適化後ファイルが小さすぎます: {optBytes.Length} bytes");
                }

                if (optBytes[0] != 0x89 || optBytes[1] != 0x50 || optBytes[2] != 0x4E || optBytes[3] != 0x47)
                {
                    throw new InvalidOperationException(
                        $"PNG破壊バグ検出: ファイル先頭シグネチャが PNG (0x89, 0x50, 0x4E, 0x47) ではありません。" +
                        $" 検出バイト: 0x{optBytes[0]:X2} 0x{optBytes[1]:X2} 0x{optBytes[2]:X2} 0x{optBytes[3]:X2}");
                }

                // 2. 透過（アルファチャンネル）の維持チェック
                using var readMs = new MemoryStream(optBytes);
                var decoder = BitmapDecoder.Create(readMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    throw new InvalidOperationException("最適化後 PNG のデコードに失敗しました。");
                }

                var frame = decoder.Frames[0];
                int optW = frame.PixelWidth;
                int optH = frame.PixelHeight;

                // フォーマットがアルファ情報を持つことを確認
                if (frame.Format.BitsPerPixel < 32)
                {
                    throw new InvalidOperationException(
                        $"透過喪失バグ検出: ピクセルフォーマットにアルファチャンネルが含まれていません。Format: {frame.Format}");
                }

                int optStride = optW * 4;
                byte[] decodedPixels = new byte[optStride * optH];
                frame.CopyPixels(decodedPixels, optStride, 0);

                bool foundZeroAlpha = false;
                bool foundSemiAlpha = false;

                for (int i = 0; i < decodedPixels.Length; i += 4)
                {
                    byte alpha = decodedPixels[i + 3];
                    if (alpha == 0) foundZeroAlpha = true;
                    else if (alpha < 200) foundSemiAlpha = true;

                    if (foundZeroAlpha && foundSemiAlpha) break;
                }

                if (!foundZeroAlpha && !foundSemiAlpha)
                {
                    throw new InvalidOperationException(
                        "透過破壊バグ検出: 最適化後の PNG から透明/半透明ピクセルが失われ、全ピクセルが不透明になりました。");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// 2. Live ACL の Deny 喪失および継承無効化時の ACE 消失バグ:
        /// Allow と Deny の両方を含むエントリを適用した際、Deny が保持され Canonical ACL Ordering (Deny 先頭) になっていること、
        /// および継承OFF時にルールが消失しないこと。
        /// </summary>
    }
}