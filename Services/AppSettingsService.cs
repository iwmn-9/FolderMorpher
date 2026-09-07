using System;
using System.IO;
using System.Text.Json;

namespace FolderMorpher.Services
{
    public enum CacheWriteMode
    {
        Local = 0,        // ローカルに保存（推奨：共有マスターを汚さない安全設定）
        SameAsRead = 1,   // 参照先と同じフォルダーに保存（管理者・マスター更新者用）
        Custom = 2        // 任意のカスタムフォルダーに保存
    }

    public class AppSettings
    {
        // キャッシュ・スナップショットの参照先（空の場合はローカル）
        public string CacheReadPath { get; set; } = string.Empty;

        // 共有参照先にアクセスできない場合にローカルを参照するか
        public bool FallbackToLocalOnReadError { get; set; } = true;

        // 保存先モード
        public CacheWriteMode WriteMode { get; set; } = CacheWriteMode.Local;

        // カスタム保存先パス（WriteMode == Custom の場合に使用）
        public string CacheWriteCustomPath { get; set; } = string.Empty;

        // 言語設定 ("ja" or "en")
        public string Language { get; set; } = "ja";
    }

    public class AppSettingsService
    {
        private static readonly Lazy<AppSettingsService> _instance = new(() => new AppSettingsService());
        public static AppSettingsService Instance => _instance.Value;

        private readonly string _settingsFilePath;
        private AppSettings _settings = new();

        public AppSettings Current => _settings;

        public AppSettingsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "FolderMorpher");
            Directory.CreateDirectory(dir);
            _settingsFilePath = Path.Combine(dir, "appsettings.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    _settings = new AppSettings();
                }
            }
            catch
            {
                _settings = new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_settings, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch
            {
                // 設定保存失敗時はサイレントに無視
            }
        }

        public string GetDefaultLocalBaseDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "FolderMorpher");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// キャッシュ・スナップショットの読み込み（参照）ベースディレクトリを取得
        /// </summary>
        public string GetEffectiveReadBaseDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_settings.CacheReadPath))
            {
                try
                {
                    if (Directory.Exists(_settings.CacheReadPath))
                    {
                        return _settings.CacheReadPath;
                    }
                }
                catch
                {
                    // ネットワークエラー等
                }

                if (_settings.FallbackToLocalOnReadError)
                {
                    return GetDefaultLocalBaseDirectory();
                }
                return string.Empty;
            }

            return GetDefaultLocalBaseDirectory();
        }

        /// <summary>
        /// キャッシュ・スナップショットの書き込み（保存）ベースディレクトリを取得
        /// </summary>
        public string GetEffectiveWriteBaseDirectory()
        {
            switch (_settings.WriteMode)
            {
                case CacheWriteMode.SameAsRead:
                    if (!string.IsNullOrWhiteSpace(_settings.CacheReadPath))
                    {
                        try
                        {
                            Directory.CreateDirectory(_settings.CacheReadPath);
                            return _settings.CacheReadPath;
                        }
                        catch { }
                    }
                    return GetDefaultLocalBaseDirectory();

                case CacheWriteMode.Custom:
                    if (!string.IsNullOrWhiteSpace(_settings.CacheWriteCustomPath))
                    {
                        try
                        {
                            Directory.CreateDirectory(_settings.CacheWriteCustomPath);
                            return _settings.CacheWriteCustomPath;
                        }
                        catch { }
                    }
                    return GetDefaultLocalBaseDirectory();

                case CacheWriteMode.Local:
                default:
                    return GetDefaultLocalBaseDirectory();
            }
        }

        /// <summary>
        /// サブディレクトリ（TreeCaches, Snapshots 等）の読み込みパスを取得
        /// </summary>
        public string GetReadDirectory(string subDirName)
        {
            var baseDir = GetEffectiveReadBaseDirectory();
            if (string.IsNullOrEmpty(baseDir)) return string.Empty;
            var dir = Path.Combine(baseDir, subDirName);
            try
            {
                if (!Directory.Exists(dir) && baseDir == GetDefaultLocalBaseDirectory())
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { }
            return dir;
        }

        /// <summary>
        /// サブディレクトリ（TreeCaches, Snapshots 等）の書き込みパスを取得
        /// </summary>
        public string GetWriteDirectory(string subDirName)
        {
            var baseDir = GetEffectiveWriteBaseDirectory();
            var dir = Path.Combine(baseDir, subDirName);
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 書き込み不可時はローカルにフォールバック
                var localBase = GetDefaultLocalBaseDirectory();
                dir = Path.Combine(localBase, subDirName);
                Directory.CreateDirectory(dir);
            }
            return dir;
        }
    }
}
