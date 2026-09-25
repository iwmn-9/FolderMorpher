using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services.ServerSearch
{
    /// <summary>
    /// ファイルサーバー側の検索インデックス（Windows Search, NAS Universal Search 等）を借りて、
    /// 数万〜数十万ファイルの全走査をスキップし、ピンポイント候補（数件〜数十件）に圧縮するアクセラレーター統括クラス。
    /// （ADR 94: Server Search Accelerator）
    /// </summary>
    public sealed class ServerSearchAccelerator
    {
        private static readonly Lazy<ServerSearchAccelerator> _instance = new(() => new ServerSearchAccelerator());
        public static ServerSearchAccelerator Instance => _instance.Value;

        private readonly List<IServerSearchProvider> _providers = new();

        public IReadOnlyList<IServerSearchProvider> Providers => _providers;

        public ServerSearchAccelerator()
        {
            // 標準プロバイダーの登録（Windows Search / WSP / OLE DB）
            _providers.Add(new WindowsSearchProvider());
        }

        public void RegisterProvider(IServerSearchProvider provider)
        {
            if (provider != null && !_providers.Contains(provider))
            {
                _providers.Add(provider);
            }
        }

        /// <summary>
        /// 指定パスとクエリに対してサーバー側のインデックス検索を試行。
        /// 成功した場合は候補ファイルパス一覧（原本確認対象）を返却。
        /// 非対応、エラー、インデックス無効時は null を返し、安全に従来の Live Search へフォールバックさせる。
        /// </summary>
        public async Task<IReadOnlyList<string>?> TryAccelerateAsync(string targetPath, SearchQuery query, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || query == null) return null;

            for (int i = 0; i < _providers.Count; i++)
            {
                var provider = _providers[i];
                try
                {
                    if (await provider.CanHandleAsync(targetPath, ct))
                    {
                        var candidates = await provider.QueryCandidatesAsync(targetPath, query, ct);
                        if (candidates != null && candidates.Count > 0)
                        {
                            return candidates;
                        }
                    }
                }
                catch
                {
                    // 個別プロバイダーのエラーは握りつぶし、次へまたはフォールバック
                }
            }

            return null;
        }
    }
}
