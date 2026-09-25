using System;

namespace FolderMorpher.Services
{
    /// <summary>
    /// BATバッチファイルおよびPowerShellスクリプト生成時の安全な文字列エスケープヘルパー。
    /// パスに含まれる %, $, `, " などの特殊文字によるスクリプト誤動作・コマンドインジェクションを防止する。
    /// </summary>
    public static class ScriptEscaper
    {
        /// <summary>
        /// BATファイル用のパス・引数エスケープ。
        /// % を %% に置換して環境変数展開を防ぎ、前後のダブルクォートを付与する。
        /// </summary>
        public static string EscapeBatPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\"\"";
            // % を %% にエスケープ（バッチファイル内の環境変数誤爆防止）
            string escaped = path.Replace("%", "%%").Replace("\"", "");
            return $"\"{escaped}\"";
        }

        /// <summary>
        /// PowerShellのダブルクォート文字列用エスケープ。
        /// $, `, " を安全にエスケープする。
        /// </summary>
        public static string EscapePowerShellString(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("`", "``")
                .Replace("$", "`$")
                .Replace("\"", "`\"");
        }

        /// <summary>
        /// PowerShellのシングルクォートリテラル文字列用エスケープ。
        /// ' を '' に置換し、前後にシングルクォートを付与する（変数展開を一切行わせない最も安全な形式）。
        /// </summary>
        public static string EscapePowerShellLiteral(string text)
        {
            if (string.IsNullOrEmpty(text)) return "''";
            string escaped = text.Replace("'", "''");
            return $"'{escaped}'";
        }
    }
}
