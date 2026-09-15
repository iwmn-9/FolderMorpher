using System;
using System.Diagnostics;
using System.IO;

namespace FolderMorpher.Services
{
    /// <summary>
    /// 繧ｨ繧ｯ繧ｹ繝励Ο繝ｼ繝ｩ繝ｼ襍ｷ蜍輔・繧ｷ繧ｧ繝ｫ騾｣謳ｺ縺ｮ豁｣譛ｬ繧ｯ繝ｩ繧ｹ
    /// </summary>
    public static class ShellHelper
    {
        /// <summary>
        /// 謖・ｮ壹＆繧後◆繝輔ぃ繧､繝ｫ縺ｾ縺溘・繝輔か繝ｫ繝繝ｼ繧偵お繧ｯ繧ｹ繝励Ο繝ｼ繝ｩ繝ｼ縺ｧ驕ｸ謚樒憾諷九〒陦ｨ遉ｺ縺吶ｋ (/select,...)
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
        /// 謖・ｮ壹＆繧後◆繝輔か繝ｫ繝繝ｼ繧偵お繧ｯ繧ｹ繝励Ο繝ｼ繝ｩ繝ｼ縺ｧ髢九￥
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