using System;
using System.Diagnostics;
using System.IO;

namespace FolderMorpher.Services
{
    /// <summary>
    /// エクスプローラー起動・シェル連携の正本クラス
    /// </summary>
    public static class ShellHelper
    {
        /// <summary>
        /// 指定されたファイルまたはフォルダーをエクスプローラーで選択状態で表示する (/select,...)
        /// </summary>
        public static bool SelectInExplorer(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;

            try
            {
                if (Directory.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{fullPath}\"",
                        UseShellExecute = true
                    });
                    return true;
                }

                if (File.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    });
                    return true;
                }

                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{parent}\"",
                        UseShellExecute = true
                    });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellHelper] Failed to open in explorer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 指定されたフォルダーをエクスプローラーで開く
        /// </summary>
        public static bool OpenFolder(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellHelper] Failed to open folder: {ex.Message}");
                return false;
            }
        }
    }
}