using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    /// <summary>
    /// UNC Root または ボリューム単位の I/O ガバナー
    /// 
    /// 【設計意図 - ADR 91】
    /// スロット枠（GlobalSlotGate: 合計最大4スロット）による合算同時I/O統制と、
    /// 列挙（軽量 5〜15ms）と本文読込（重量 50〜200ms）それぞれのレイテンシ適応学習の完全分離を実現。
    /// 本文走査の所要時間で列挙側のベースラインが異常判定・汚染される現象（崖落ち固着）を根本解決する。
    /// </summary>
    public sealed class SharedVolumeGovernor
    {
        public string RootKey { get; }

        /// <summary>
        /// ボリューム全体の同時実行スロット枠（最大4）。列挙と本文読込の合算が4を超えないことを物理保証。
        /// </summary>
        public SemaphoreSlim GlobalSlotGate { get; } = new(AdaptiveConcurrencyController.MaxConcurrency, AdaptiveConcurrencyController.MaxConcurrency);

        /// <summary>
        /// ディレクトリ列挙専用のレイテンシ適応コントローラー（ベースライン 5〜15ms）。
        /// </summary>
        public AdaptiveConcurrencyController EnumerationController { get; } = new();

        /// <summary>
        /// 本文読み込み専用のレイテンシ適応コントローラー（ベースライン 50〜200ms）。
        /// </summary>
        public AdaptiveConcurrencyController ContentController { get; } = new();

        public SharedVolumeGovernor(string rootKey)
        {
            RootKey = rootKey;
        }

        /// <summary>
        /// 全体スロット枠（GlobalSlotGate）を確保し、完了時に解放する Disposable を返します。
        /// </summary>
        public async Task<IDisposable> AcquireSlotAsync(CancellationToken ct)
        {
            await GlobalSlotGate.WaitAsync(ct).ConfigureAwait(false);
            return new SlotReleaser(GlobalSlotGate);
        }

        private sealed class SlotReleaser : IDisposable
        {
            private SemaphoreSlim? _gate;
            public SlotReleaser(SemaphoreSlim gate) => _gate = gate;
            public void Dispose()
            {
                var g = Interlocked.Exchange(ref _gate, null);
                g?.Release();
            }
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
