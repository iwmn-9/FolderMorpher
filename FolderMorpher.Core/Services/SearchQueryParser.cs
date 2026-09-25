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

                    case "content" or "text":
                        query.ContentKeyword = val;
                        return;

                    case "office-link" or "link":
                        if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1")
                        {
                            query.HasOfficeLinkOnly = true;
                            query.OfficeLinkKeyword = null;
                        }
                        else
                        {
                            query.HasOfficeLinkOnly = true;
                            query.OfficeLinkKeyword = val;
                        }
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

            // Regular keyword (supports OR via comma ',' or pipe '|')
            string cleanToken = StripQuotes(token);
            if (!string.IsNullOrEmpty(cleanToken))
            {
                if (cleanToken.Contains(',') || cleanToken.Contains('|'))
                {
                    var orWords = cleanToken.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(w => StripQuotes(w.Trim()))
                                            .Where(w => !string.IsNullOrEmpty(w))
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList();
                    if (orWords.Count > 0)
                    {
                        query.KeywordGroups.Add(orWords);
                        foreach (var w in orWords)
                        {
                            if (!query.Keywords.Contains(w, StringComparer.OrdinalIgnoreCase))
                            {
                                query.Keywords.Add(w);
                            }
                        }
                    }
                }
                else
                {
                    query.KeywordGroups.Add(new List<string> { cleanToken });
                    if (!query.Keywords.Contains(cleanToken, StringComparer.OrdinalIgnoreCase))
                    {
                        query.Keywords.Add(cleanToken);
                    }
                }
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
            if (val.Contains(".."))
            {
                var parts = val.Split(new[] { ".." }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    if (TryParseBytes(parts[0], out long min)) query.MinSizeBytes = min;
                    if (TryParseBytes(parts[1], out long max)) query.MaxSizeBytes = max;
                }
                return;
            }

            if (val.StartsWith(">="))
            {
                if (TryParseBytes(val.Substring(2), out long b)) query.MinSizeBytes = b;
            }
            else if (val.StartsWith('>'))
            {
                if (TryParseBytes(val.Substring(1), out long b)) query.MinSizeBytes = b;
            }
            else if (val.StartsWith("<="))
            {
                if (TryParseBytes(val.Substring(2), out long b)) query.MaxSizeBytes = b;
            }
            else if (val.StartsWith('<'))
            {
                if (TryParseBytes(val.Substring(1), out long b)) query.MaxSizeBytes = b;
            }
            else if (TryParseBytes(val, out long exact))
            {
                query.MinSizeBytes = exact;
                query.MaxSizeBytes = exact;
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
            else if (input.EndsWith("KB")) { multiplier = 1024.0; numPart = input.Substring(0, input.Length - 2); }
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
            // Handles dormant:>3y, dormant:>180d, dormant:3y, dormant:>=3y
            string clean = val.TrimStart('>', '<', '=').Trim();
            if (clean.EndsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                string numStr = clean.Substring(0, clean.Length - 1).Trim();
                if (int.TryParse(numStr, out int years) && years > 0)
                {
                    query.DormantDays = years * 365;
                    query.MaxModifiedUtc = DateTime.UtcNow.AddYears(-years);
                }
            }
            else if (clean.EndsWith("d", StringComparison.OrdinalIgnoreCase))
            {
                string numStr = clean.Substring(0, clean.Length - 1).Trim();
                if (int.TryParse(numStr, out int days) && days > 0)
                {
                    query.DormantDays = days;
                    query.MaxModifiedUtc = DateTime.UtcNow.AddDays(-days);
                }
            }
            else if (int.TryParse(clean, out int defaultYears) && defaultYears > 0)
            {
                query.DormantDays = defaultYears * 365;
                query.MaxModifiedUtc = DateTime.UtcNow.AddYears(-defaultYears);
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
