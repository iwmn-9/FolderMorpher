using System;
using System.Collections.Generic;
using System.Globalization;

namespace FolderMorpher.Services
{
    public enum AppLanguage
    {
        Japanese,
        English
    }

    public class LocalizationService
    {
        private static LocalizationService? _instance;
        public static LocalizationService Instance => _instance ??= new LocalizationService();

        public AppLanguage CurrentLanguage { get; private set; }

        public event Action? LanguageChanged;

        private LocalizationService()
        {
            // システム言語が "ja" (日本語) の場合のみ日本語、それ以外はすべて英語をデフォルトとする
            string sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            CurrentLanguage = (sysLang == "ja") ? AppLanguage.Japanese : AppLanguage.English;
        }

        public void SetLanguage(AppLanguage lang)
        {
            if (CurrentLanguage == lang) return;
            CurrentLanguage = lang;
            LanguageChanged?.Invoke();
        }

        public void ToggleLanguage()
        {
            SetLanguage(CurrentLanguage == AppLanguage.Japanese ? AppLanguage.English : AppLanguage.Japanese);
        }

        public string GetString(string keyJa, string keyEn)
        {
            return CurrentLanguage == AppLanguage.Japanese ? keyJa : keyEn;
        }
    }
}
