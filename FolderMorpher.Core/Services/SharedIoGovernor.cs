using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FolderMorpher.Services
{
    /// <summary>
    /// UNC Root または ボリューム単位の I/O ガバナー
    /// 
    /// 【設計意図 - ADR 91】
    /// 列挙（軽量 5〜15ms）と本文読込（重量 50〜200ms）の並列枠と
    /// レイテンシ適応学習を共有先ごとに分離する。
    /// 本文走査の所要時間で列挙側のベースラインが異常判定・汚染される現象（崖落ち固着）を根本解決する。
    /// </summary>
    public sealed class SharedVolumeGovernor
    {
        public string RootKey { get; }

        /// <summary>
        /// ディレクトリ列挙専用のレイテンシ適応コントローラー（ベースライン 5〜15ms、安全な2並列固定・上限2）。
        /// サーバーのファイルシステム管理領域・メタデータキャッシュを100%保護。
        /// </summary>
        public AdaptiveConcurrencyController EnumerationController { get; } = new(min: 2, defaultVal: 2, max: 2);

        /// <summary>
        /// 本文読み込み専用のレイテンシ適応コントローラー（AIMD: 4 ➔ 6 ➔ 8 ➔ 最大12）。
        /// SMBパイプラインを充填し、RTT遅延を隠蔽してスループットを最大化。
        /// </summary>
        public AdaptiveConcurrencyController ContentController { get; } = new(min: 2, defaultVal: 4, max: 12);

        public SharedVolumeGovernor(string rootKey)
        {
            RootKey = rootKey;
        }

    }

    /// <summary>
    /// UNC Root / ボリューム単位の統合 I/O ガバナー (Shared I/O Governor)
    /// </summary>
    public static class SharedIoGovernor
    {
        private static readonly ConcurrentDictionary<string, SharedVolumeGovernor> _governors =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 指定されたパスのターゲットルートに対応する SharedVolumeGovernor を取得します。
        /// </summary>
        public static SharedVolumeGovernor GetGovernor(string? path)
        {
            string rootKey = PathCanonicalizer.GetVolumeOrShareRoot(path);
            return _governors.GetOrAdd(rootKey, key => new SharedVolumeGovernor(key));
        }

        /// <summary>
        /// 後方互換性メソッド: 指定パスの列挙用コントローラーを取得します。
        /// </summary>
        public static AdaptiveConcurrencyController GetController(string? path)
        {
            return GetGovernor(path).EnumerationController;
        }

        /// <summary>
        /// 現在管理されているボリュームキー一覧を取得します（監視・デバッグ用）。
        /// </summary>
        public static IReadOnlyCollection<string> ActiveRoots => _governors.Keys.ToArray();

        /// <summary>
        /// 全ガバナーをクリアします（テスト用）。
        /// </summary>
        public static void Reset()
        {
            _governors.Clear();
        }
    }
}
