using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Flexible Everything-style search query parser
    /// </summary>
    public static class SearchQueryParser
    {
        private static readonly Regex TokenizerRegex = new(
            @"(?:[^\s""]+|""[^""]*"")+(?:\.\.[^\s""]+)?",
            RegexOptions.Compiled);

        /// <summary>
        /// Parse raw input query into structured SearchQuery
        /// </summary>
        public static SearchQuery Parse(string rawQuery)
        {
            var query = new SearchQuery
            {
                RawQuery = rawQuery?.Trim() ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(query.RawQuery))
            {
                return query;
            }

            var matches = TokenizerRegex.Matches(query.RawQuery);
            foreach (Match match in matches)
            {
                string token = match.Value.Trim();
                if (string.IsNullOrEmpty(token)) continue;

                ParseToken(token, query);
            }

            return query;
        }

        private static void ParseToken(string token, SearchQuery query)
        {
            // NOT prefix (!word or -word)
            if ((token.StartsWith('!') || token.StartsWith('-')) && token.Length > 1 && !token.Contains(':'))
            {
                string cleanWord = StripQuotes(token.Substring(1));
                if (!string.IsNullOrEmpty(cleanWord))
                {
                    query.ExcludedWords.Add(cleanWord);
                }
                return;
            }

            // Exact phrase ("exact phrase")
            if (token.StartsWith('"') && token.EndsWith('"') && token.Length >= 2)
            {
                string phrase = StripQuotes(token);
                if (!string.IsNullOrEmpty(phrase))
                {
                    query.ExactPhrases.Add(phrase);
                }
                return;
            }

            // Keyword:Value modifier
            int colonIdx = token.IndexOf(':');
            if (colonIdx > 0 && colonIdx < token.Length - 1)
            {
                string prefix = token.Substring(0, colonIdx).ToLowerInvariant();
                string val = StripQuotes(token.Substring(colonIdx + 1));

                switch (prefix)
                {
                    case "ext":
                        ParseExtensions(val, query);
                        return;

                    case "size":
                        ParseSize(val, query);
                        return;

                    case "modified" or "date":
                        ParseDate(val, query);
                        return;

                    case "dormant":
                        ParseDormant(val, query);
                        return;

                    case "path" or "folder":
                        if (!string.IsNullOrEmpty(val)) query.PathContains.Add(val);
                        return;

                    case "pathlen":
                        ParsePathLength(val, query);
                        return;

                    case "chars":
                        if (val.Equals("illegal", StringComparison.OrdinalIgnoreCase)) query.OnlyIllegalChars = true;
                        return;

                    case "duplicate" or "dup":
                        query.OnlyDuplicates = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
                        return;

                    case "content" or "text":
                        query.ContentKeyword = val;
                        return;

                    case "office-link" or "link":
                        query.OfficeLinkKeyword = val;
                        return;

                    case "regex":
                        try
                        {
                            query.CompiledRegex = new Regex(val, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        }
                        catch { }
                        return;

                    case "type":
                        if (val.Equals("dir", StringComparison.OrdinalIgnoreCase) || val.Equals("folder", StringComparison.OrdinalIgnoreCase))
                            query.IsDirectoryOnly = true;
                        else if (val.Equals("file", StringComparison.OrdinalIgnoreCase))
                            query.IsDirectoryOnly = false;
                        return;
                }
            }

            // Wildcard extension (*.ext)
            if (token.StartsWith("*.") && token.Length > 2 && !token.Substring(2).Contains('*'))
            {
                string ext = token.Substring(1).ToLowerInvariant();
                query.Extensions.Add(ext);
                return;
            }

            // Regular keyword
            string cleanKeyword = StripQuotes(token);
            if (!string.IsNullOrEmpty(cleanKeyword))
            {
                query.Keywords.Add(cleanKeyword);
            }
        }

        private static void ParseExtensions(string val, SearchQuery query)
        {
            var exts = val.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var e in exts)
            {
                var clean = e.Trim().TrimStart('*').ToLowerInvariant();
                if (!clean.StartsWith('.')) clean = "." + clean;
                query.Extensions.Add(clean);
            }
        }

        private static void ParseSize(string val, SearchQuery query)
        {
            int rangeIdx = val.IndexOf("..", StringComparison.Ordinal);
            if (rangeIdx > 0)
            {
                string minStr = val.Substring(0, rangeIdx);
                string maxStr = val.Substring(rangeIdx + 2);
                if (TryParseBytes(minStr, out long minB)) query.MinSizeBytes = minB;
                if (TryParseBytes(maxStr, out long maxB)) query.MaxSizeBytes = maxB;
                return;
            }

            if (val.StartsWith(">=") || val.StartsWith(">"))
            {
                string numStr = val.TrimStart('>', '=');
                if (TryParseBytes(numStr, out long b)) query.MinSizeBytes = b;
            }
            else if (val.StartsWith("<=") || val.StartsWith("<"))
            {
                string numStr = val.TrimStart('<', '=');
                if (TryParseBytes(numStr, out long b)) query.MaxSizeBytes = b;
            }
            else
            {
                if (TryParseBytes(val, out long b))
                {
                    query.MinSizeBytes = b;
                    query.MaxSizeBytes = b;
                }
            }
        }

        private static bool TryParseBytes(string input, out long bytes)
        {
            bytes = 0;
            input = input.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(input)) return false;

            double multiplier = 1.0;
            string numPart = input;

            if (input.EndsWith("TB")) { multiplier = 1024L * 1024 * 1024 * 1024; numPart = input.Substring(0, input.Length - 2); }
            else if (input.EndsWith("GB")) { multiplier = 1024L * 1024 * 1024; numPart = input.Substring(0, input.Length - 2); }
            else if (input.EndsWith("MB")) { multiplier = 1024L * 1024; numPart = input.Substring(0, input.Length - 2); }
            else if (input.EndsWith("KB")) { multiplier = 1024L; numPart = input.Substring(0, input.Length - 2); }
            else if (input.EndsWith("B")) { multiplier = 1.0; numPart = input.Substring(0, input.Length - 1); }

            if (double.TryParse(numPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                bytes = (long)(val * multiplier);
                return true;
            }
            return false;
        }

        private static void ParseDate(string val, SearchQuery query)
        {
            var now = DateTime.UtcNow;
            if (val.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                query.MinModifiedUtc = DateTime.UtcNow.Date;
                return;
            }
            if (val.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            {
                query.MinModifiedUtc = DateTime.UtcNow.Date.AddDays(-1);
                query.MaxModifiedUtc = DateTime.UtcNow.Date;
                return;
            }

            if ((val.StartsWith('>') || val.StartsWith('<')) && val.EndsWith('d'))
            {
                bool isAfter = val.StartsWith('>');
                string daysStr = val.Substring(1, val.Length - 2);
                if (int.TryParse(daysStr, out int days))
                {
                    var target = now.AddDays(-days);
                    if (isAfter) query.MinModifiedUtc = target;
                    else query.MaxModifiedUtc = target;
                }
                return;
            }

            if (val.StartsWith('>') || val.StartsWith('<'))
            {
                bool isAfter = val.StartsWith('>');
                string dateStr = val.TrimStart('>', '<', '=');
                if (DateTime.TryParse(dateStr, out var dt))
                {
                    if (isAfter) query.MinModifiedUtc = dt.ToUniversalTime();
                    else query.MaxModifiedUtc = dt.ToUniversalTime();
                }
            }
        }

        private static void ParseDormant(string val, SearchQuery query)
        {
            string numStr = val.TrimEnd('y', 'Y');
            if (int.TryParse(numStr, out int years) && years > 0)
            {
                query.DormantYears = years;
                query.MaxModifiedUtc = DateTime.UtcNow.AddYears(-years);
            }
        }

        private static void ParsePathLength(string val, SearchQuery query)
        {
            string numStr = val.TrimStart('>', '=');
            if (int.TryParse(numStr, out int len))
            {
                query.MinPathLength = len;
            }
        }

        private static string StripQuotes(string str)
        {
            if (str.Length >= 2 && str.StartsWith('"') && str.EndsWith('"'))
            {
                return str.Substring(1, str.Length - 2);
            }
            return str;
        }
    }
}