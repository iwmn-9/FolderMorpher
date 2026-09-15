using System;

namespace FolderMorpher.Services
{
    /// <summary>
    /// アプリケーション全体のバイト数・サイズ表示フォーマット正本クラス
    /// </summary>
    public static class FormatHelper
    {
        private static readonly string[] Suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>
        /// バイト数を人間が読みやすい文字列（B, KB, MB, GB, TB, PB）に変換する
        /// </summary>
        public static string FormatBytes(long bytes, int decimals = 1)
        {
            if (bytes == 0) return "0 B";
            string sign = bytes < 0 ? "-" : "";
            long absBytes = Math.Abs(bytes);
            int place = Convert.ToInt32(Math.Floor(Math.Log(absBytes, 1024)));
            if (place >= Suffixes.Length) place = Suffixes.Length - 1;
            if (place == 0) return $"{sign}{absBytes} B";

            double num = Math.Round(absBytes / Math.Pow(1024, place), decimals);
            string format = decimals == 2 ? "0.00" : "0.#";
            return $"{sign}{num.ToString(format)} {Suffixes[place]}";
        }
 }
}
