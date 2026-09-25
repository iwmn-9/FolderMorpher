using System;
using System.Collections.Concurrent;

namespace FolderMorpher.Services
{
    /// <summary>
    /// UNC Root / ボリューム単位の統合 I/O ガバナー (Shared I/O Governor)
    /// 
    /// 【設計意図 - ADR 90】
    /// ディレクトリ列挙（SafeFileEnumerator）と本文走査（SearchEngineService）がそれぞれ個別に
    /// 最大4並列で動くと、同一UNCに対して最大8並列が走り、SMB/NAS負荷やロックを引き起こすリスクがある。
    /// SharedIoGovernor は、UNC Root（例: \\server\share）またはボリューム（C:）単位で単一の
    /// AdaptiveConcurrencyController を共有・一元管理し、合算並列度を安全域（最大4、異常時即座に2へ崖落ち）に統制する。
    /// </summary>
    public static class SharedIoGovernor
    {
        private static readonly ConcurrentDictionary<string, AdaptiveConcurrencyController> _controllers =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 指定されたパスのターゲットルートに対応する共有 AdaptiveConcurrencyController を取得します。
        /// </summary>
        public static AdaptiveConcurrencyController GetController(string? path)
        {
            string rootKey = PathCanonicalizer.GetVolumeOrShareRoot(path);
            return _controllers.GetOrAdd(rootKey, _ => new AdaptiveConcurrencyController());
        }

        /// <summary>
        /// 現在管理されているコントローラーのキー一覧を取得します（監視・デバッグ用）。
        /// </summary>
        public static IReadOnlyCollection<string> ActiveRoots => _controllers.Keys.ToArray();

        /// <summary>
        /// 全コントローラーをクリアします（テスト用）。
        /// </summary>
        public static void Reset()
        {
            _controllers.Clear();
        }
    }
}
