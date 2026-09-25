using System;
using System.Collections.Generic;

namespace FolderMorpher.Services
{
    /// <summary>
    /// 【ADR 82: Aho-Corasick 多パターン同時照合エンジン】
    /// 複数の検索語・フレーズを単一の決定性オートマトン（Trie + Failure Link）へ統合し、
    /// テキストストリームを「1パス（前から後ろへ一方向）」で流すだけで全パターンの出現を同時検知する。
    /// 巨大UNCファイルでも複数回スキャンを完全に根絶し、条件合致時の Early Exit を可能にする。
    /// </summary>
    public sealed class AhoCorasickSearcher
    {
        private sealed class Node
        {
            public Dictionary<char, Node> Transitions { get; } = new();
            public Node? Failure { get; set; }
            public List<int> MatchedPatternIndices { get; } = new();
        }

        private readonly Node _root = new();
        private readonly string[] _patterns;
        private readonly bool _ignoreCase;

        public AhoCorasickSearcher(IReadOnlyList<string> patterns, bool ignoreCase = true)
        {
            _ignoreCase = ignoreCase;
            var list = new List<string>();
            foreach (var p in patterns)
            {
                if (!string.IsNullOrEmpty(p))
                {
                    list.Add(ignoreCase ? p.ToLowerInvariant() : p);
                }
            }
            _patterns = list.ToArray();
            BuildTrie();
            BuildFailureLinks();
        }

        public int PatternCount => _patterns.Length;
        public IReadOnlyList<string> Patterns => _patterns;

        private void BuildTrie()
        {
            for (int i = 0; i < _patterns.Length; i++)
            {
                string p = _patterns[i];
                Node current = _root;
                foreach (char c in p)
                {
                    if (!current.Transitions.TryGetValue(c, out var next))
                    {
                        next = new Node();
                        current.Transitions[c] = next;
                    }
                    current = next;
                }
                current.MatchedPatternIndices.Add(i);
            }
        }

        private void BuildFailureLinks()
        {
            var queue = new Queue<Node>();

            // Root直下のノードのFailureはRoot自身
            foreach (var child in _root.Transitions.Values)
            {
                child.Failure = _root;
                queue.Enqueue(child);
            }

            // BFSでFailure Linkを構築
            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();

                foreach (var (c, next) in current.Transitions)
                {
                    queue.Enqueue(next);

                    Node? fail = current.Failure;
                    while (fail != null && !fail.Transitions.ContainsKey(c))
                    {
                        fail = fail.Failure;
                    }

                    next.Failure = fail != null ? fail.Transitions[c] : _root;

                    // 出現パターンの伝播
                    if (next.Failure.MatchedPatternIndices.Count > 0)
                    {
                        next.MatchedPatternIndices.AddRange(next.Failure.MatchedPatternIndices);
                    }
                }
            }
        }

        /// <summary>
        /// ストリームまたはチャンク文字列を流し込み、マッチしたパターンインデックスを追跡する。
        /// 全必須パターン（またはOR条件）が揃った時点で Early Exit できる。
        /// </summary>
        public bool ContainsAny(string text)
        {
            if (string.IsNullOrEmpty(text) || _patterns.Length == 0) return false;

            Node current = _root;
            for (int i = 0; i < text.Length; i++)
            {
                char c = _ignoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                while (current != _root && !current.Transitions.ContainsKey(c))
                {
                    current = current.Failure ?? _root;
                }

                if (current.Transitions.TryGetValue(c, out var next))
                {
                    current = next;
                }
                else
                {
                    current = _root;
                }

                if (current.MatchedPatternIndices.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 指定されたテキスト中に出現した全パターンのインデックス集合を返す（ワンパス照合）。
        /// </summary>
        public HashSet<int> FindMatchedIndices(string text)
        {
            var matched = new HashSet<int>();
            if (string.IsNullOrEmpty(text) || _patterns.Length == 0) return matched;

            Node current = _root;
            for (int i = 0; i < text.Length; i++)
            {
                char c = _ignoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                while (current != _root && !current.Transitions.ContainsKey(c))
                {
                    current = current.Failure ?? _root;
                }

                if (current.Transitions.TryGetValue(c, out var next))
                {
                    current = next;
                }
                else
                {
                    current = _root;
                }

                if (current.MatchedPatternIndices.Count > 0)
                {
                    foreach (var idx in current.MatchedPatternIndices)
                    {
                        matched.Add(idx);
                    }

                    // 全パターンがマッチしたら早期終了（Early Exit）
                    if (matched.Count == _patterns.Length)
                    {
                        break;
                    }
                }
            }

            return matched;
        }

        /// <summary>
        /// 指定されたテキスト中から最初に出現したマッチ箇所を検出し、
        /// その前後を含めたハイライトスニペット（例: "...前文【キーワード】後文..."）をワンパスで生成する。
        /// </summary>
        public string ExtractSnippet(string text, int snippetLength = 60, string highlightOpen = "【", string highlightClose = "】")
        {
            if (string.IsNullOrEmpty(text) || _patterns.Length == 0) return string.Empty;

            Node current = _root;
            int matchStart = -1;
            int matchLen = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = _ignoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                while (current != _root && !current.Transitions.ContainsKey(c))
                {
                    current = current.Failure ?? _root;
                }

                if (current.Transitions.TryGetValue(c, out var next))
                {
                    current = next;
                }
                else
                {
                    current = _root;
                }

                if (current.MatchedPatternIndices.Count > 0)
                {
                    int patIdx = current.MatchedPatternIndices[0];
                    matchLen = _patterns[patIdx].Length;
                    matchStart = i - matchLen + 1;
                    break;
                }
            }

            if (matchStart < 0) return string.Empty;

            int contextMargin = Math.Max(0, (snippetLength - matchLen) / 2);
            int start = Math.Max(0, matchStart - contextMargin);
            int end = Math.Min(text.Length, matchStart + matchLen + contextMargin);

            string before = text.Substring(start, matchStart - start);
            string matchedText = text.Substring(matchStart, matchLen);
            string after = text.Substring(matchStart + matchLen, end - (matchStart + matchLen));

            before = before.Replace("\r", " ").Replace("\n", " ");
            after = after.Replace("\r", " ").Replace("\n", " ");

            string prefix = start > 0 ? "..." : "";
            string suffix = end < text.Length ? "..." : "";

            return $"{prefix}{before}{highlightOpen}{matchedText}{highlightClose}{after}{suffix}";
        }
    }
}
