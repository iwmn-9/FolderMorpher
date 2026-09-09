using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// 物理ファイル単位の削除実行計画
    /// </summary>
    public class AuditCleanupPlan
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime? ExpectedLastWriteTimeUtc { get; set; }
        public bool IsOriginalCandidate { get; set; }
        public List<AuditItem> AssociatedItems { get; set; } = new();
    }

    /// <summary>
    /// 物理削除の実行結果
    /// </summary>
    public class AuditCleanupResult
    {
        public int SuccessCount { get; set; }
        public long FreedBytes { get; set; }
        public HashSet<string> DeletedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 断捨離・健全化の完全削除サービス（整線パッチパネル）
    /// 監査行（AuditItem）ではなく物理ファイル（FullPath）を正本として削除計画・実行・属性復元を行う
    /// </summary>
    public static class AuditCleanupService
    {
        /// <summary>
        /// 全監査アイテムと現在の選択状態から、FullPath 単位で一意化された削除実行計画を作成する
        /// </summary>
        /// <param name="allItems">全監査アイテムの正本（_lastAuditItems）</param>
        /// <returns>物理ファイル単位の実行計画リスト</returns>
        public static List<AuditCleanupPlan> BuildPlan(IEnumerable<AuditItem> allItems)
        {
            var itemList = allItems as IList<AuditItem> ?? allItems.ToList();

            // 1. 全アイテムから原本候補の FullPath を聖域として抽出
            // （休眠行から選択されても原本保護を確実に発動させるための正本リスト）
            var originalPaths = new HashSet<string>(
                itemList.Where(i => i.IsOriginalCandidate || (i.Detail != null && i.Detail.Contains("[原本候補]")))
                        .Select(i => i.FullPath),
                StringComparer.OrdinalIgnoreCase);

            // 2. チェックされているアイテムのみを抽出し、FullPath でグループ化
            var checkedItems = itemList.Where(i => i.IsChecked).ToList();
            var plans = checkedItems
                .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    return new AuditCleanupPlan
                    {
                        FullPath = g.Key,
                        FileName = first.FileName,
                        Size = first.Size,
                        ExpectedLastWriteTimeUtc = first.LastWriteTime != default ? first.LastWriteTime.ToUniversalTime() : null,
                        IsOriginalCandidate = originalPaths.Contains(g.Key),
                        AssociatedItems = g.ToList()
                    };
                })
                .ToList();

            return plans;
        }

        /// <summary>
        /// 実行計画に基づき、物理ファイルを1回だけ削除する
        /// （読み取り専用属性は一時解除し、失敗時は元の属性を復元する安全機構付き）
        /// </summary>
        public static AuditCleanupResult ExecutePlan(IEnumerable<AuditCleanupPlan> plans)
        {
            var result = new AuditCleanupResult();

            foreach (var plan in plans)
            {
                FileAttributes? originalAttrs = null;
                try
                {
                    if (!File.Exists(plan.FullPath))
                    {
                        // M4対策: ファイルが存在しない場合は成功カウントせず、警告として記録
                        result.Errors.Add($"{plan.FileName}: ファイルが存在しません（既に移動または削除されています）");
                        continue;
                    }

                    var fi = new FileInfo(plan.FullPath);

                    // H4対策: スキャン後のファイル更新を検知（楽観的ロック / ETag検証）
                    if (plan.ExpectedLastWriteTimeUtc.HasValue)
                    {
                        var diffSeconds = Math.Abs((fi.LastWriteTimeUtc - plan.ExpectedLastWriteTimeUtc.Value).TotalSeconds);
                        if (diffSeconds > 2 || fi.Length != plan.Size)
                        {
                            result.Errors.Add($"{plan.FileName}: スキャン後にファイルが変更されています（安全のため削除をスキップしました）");
                            continue;
                        }
                    }

                    originalAttrs = fi.Attributes;

                    // 読み取り専用属性の解除
                    if (fi.IsReadOnly)
                    {
                        fi.IsReadOnly = false;
                    }

                    fi.Delete();

                    result.SuccessCount++;
                    result.FreedBytes += plan.Size;
                    result.DeletedPaths.Add(plan.FullPath);
                }
                catch (Exception ex)
                {
                    // 削除失敗時：元のファイル属性を安全に復元（Medium指摘対応）
                    if (originalAttrs.HasValue && File.Exists(plan.FullPath))
                    {
                        try
                        {
                            File.SetAttributes(plan.FullPath, originalAttrs.Value);
                        }
                        catch
                        {
                            // 属性復元時の例外は握りつぶし、主原因のエラーを報告する
                        }
                    }

                    result.Errors.Add($"{plan.FileName}: {ex.Message}");
                }
            }

            return result;
        }
    }
}
