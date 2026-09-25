using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services.ServerSearch
{
    /// <summary>
    /// ファイルサーバーやストレージ側が保持する検索インデックス（Windows Search, NAS Universal Search 等）を
    /// 外部プロバイダーとして借りるための抽象インターフェース。（ADR 94: Server Search Accelerator）
    /// </summary>
    public interface IServerSearchProvider
    {
        /// <summary>
        /// プロバイダー名（例: "Windows Search (WSP / OLE DB)"）
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 指定されたパス（UNCまたはローカル）に対して本プロバイダーが検索可能か判定（プローブ）。
        /// </summary>
        Task<bool> CanHandleAsync(string targetPath, CancellationToken ct);

        /// <summary>
        /// サーバー側インデックスに問い合わせ、合致する候補ファイルパスの一覧を取得。
        /// サービス未稼働、リモートクエリ禁止、プロトコル非対応等の場合は例外を投げず null を返して透過的フォールバックを促す。
        /// </summary>
        Task<IReadOnlyList<string>?> QueryCandidatesAsync(string targetPath, SearchQuery query, CancellationToken ct);
    }
}
