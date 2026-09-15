using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Normalisation and fuzzy matching helpers for path / folder name scoring.
/// </summary>
public static class PathMatcher
{
    /// <summary>
    /// Lowercases and replaces every non letter/digit character with a single space,
    /// mirroring the way Jellyfin normalises item names (CleanName).
    /// </summary>
    /// <param name="value">Raw value.</param>
    /// <returns>Normalised value.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var lastSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Escapes LIKE wildcards so user input cannot widen the query.
    /// </summary>
    /// <param name="value">Raw token.</param>
    /// <returns>Escaped token.</returns>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// Splits a file path into normalised segments.
    /// </summary>
    /// <param name="path">File system path.</param>
    /// <returns>Normalised segments.</returns>
    public static string[] Segments(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var n = Normalize(part);
            if (n.Length > 0)
            {
                result.Add(n);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Scores how well a normalised segment matches the normalised search term.
    /// Higher is better, 0 means no match.
    /// </summary>
    /// <param name="segment">Normalised path segment.</param>
    /// <param name="term">Normalised term.</param>
    /// <returns>Score.</returns>
    public static float ScoreSegment(string segment, string term)
    {
        if (segment.Length == 0 || term.Length == 0)
        {
            return 0f;
        }

        if (string.Equals(segment, term, StringComparison.Ordinal))
        {
            return 72f;
        }

        if (segment.StartsWith(term, StringComparison.Ordinal))
        {
            return 66f;
        }

        if (segment.Contains(term, StringComparison.Ordinal))
        {
            return 58f;
        }

        var best = 0f;
        if (IsSubsequence(term, segment))
        {
            best = 34f;
        }

        var allowed = AllowedDistance(term.Length);
        if (allowed > 0 && Math.Abs(segment.Length - term.Length) <= allowed
            && WithinEditDistance(term, segment, allowed))
        {
            best = Math.Max(best, 40f);
        }

        return best;
    }

    /// <summary>
    /// Scores a normalised item name against the term.
    /// </summary>
    /// <param name="cleanName">Jellyfin CleanName value.</param>
    /// <param name="term">Normalised term.</param>
    /// <returns>Score.</returns>
    public static float ScoreName(string? cleanName, string term)
    {
        if (string.IsNullOrEmpty(cleanName) || term.Length == 0)
        {
            return 0f;
        }

        var name = Normalize(cleanName);
        if (name.Length == 0)
        {
            return 0f;
        }

        if (string.Equals(name, term, StringComparison.Ordinal))
        {
            return 78f;
        }

        if (name.StartsWith(term, StringComparison.Ordinal))
        {
            return 70f;
        }

        if (name.Contains(term, StringComparison.Ordinal))
        {
            return 52f;
        }

        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsSubsequence(term, word))
            {
                return 32f;
            }
        }

        return 0f;
    }

    /// <summary>
    /// 模糊匹配打分：名称里**任意一段**与查询词近似（编辑距离够近）就算命中。
    /// </summary>
    /// <remarks>
    /// 为什么不用子序列：实测 @@bulll@@ 会把 @@user_威猛先生_MS4wLjABAAAA…@@ 这种随机长串也算命中
    /// （字符顺序恰好凑齐），结果全是垃圾。这里改成"近似子串"：
    ///   · 整个名称与查询词编辑距离够近 → 46 分（名称短、纯拼错的情况）；
    ///   · 名称里任意一段（长度 = 查询词长 ± 允许距离，且首字符对得上）编辑距离够近 → 44 分
    ///     （@@bulll@@ → @@bullvideo…@@ 里的 @@bullv@@）。
    /// 精确相等/前缀/包含分别是 78/70/52 分，正常情况下走不到这条路径（前面早就命中了）。
    /// </remarks>
    /// <param name="cleanName">Jellyfin CleanName 值。</param>
    /// <param name="term">归一化后的查询词。</param>
    /// <returns>分数（0 表示不匹配）。</returns>
    public static float ScoreFuzzy(string? cleanName, string term)
    {
        if (string.IsNullOrEmpty(cleanName) || string.IsNullOrEmpty(term))
        {
            return 0f;
        }

        var name = Normalize(cleanName).Replace(" ", string.Empty, StringComparison.Ordinal);
        var needle = term.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (name.Length == 0 || needle.Length == 0)
        {
            return 0f;
        }

        if (string.Equals(name, needle, StringComparison.Ordinal))
        {
            return 78f;
        }

        if (name.StartsWith(needle, StringComparison.Ordinal))
        {
            return 70f;
        }

        if (name.Contains(needle, StringComparison.Ordinal))
        {
            return 52f;
        }

        var allowed = AllowedDistance(needle.Length);
        if (allowed <= 0)
        {
            return 0f;
        }

        if (Math.Abs(name.Length - needle.Length) <= allowed && WithinEditDistance(needle, name, allowed))
        {
            return 46f;
        }

        // 近似子串：只在查询词首字符出现的位置附近开窗（避免整串滑动带来的开销）
        var anchor = needle[0];
        for (var at = name.IndexOf(anchor); at >= 0; at = name.IndexOf(anchor, at + 1))
        {
            for (var shift = -allowed; shift <= allowed; shift++)
            {
                var start = at + shift;
                if (start < 0)
                {
                    continue;
                }

                for (var len = Math.Max(1, needle.Length - allowed); len <= needle.Length + allowed; len++)
                {
                    if (start + len > name.Length)
                    {
                        continue;
                    }

                    if (WithinEditDistance(needle, name.Substring(start, len), allowed))
                    {
                        return 44f;
                    }
                }
            }
        }

        return 0f;
    }

    /// <summary>
    /// True when every character of <paramref name="needle"/> appears in order in <paramref name="haystack"/>.
    /// </summary>
    /// <param name="needle">Term.</param>
    /// <param name="haystack">Candidate text.</param>
    /// <returns>True on match.</returns>
    public static bool IsSubsequence(string needle, string haystack)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        var i = 0;
        foreach (var ch in haystack)
        {
            if (ch == needle[i])
            {
                i++;
                if (i == needle.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Levenshtein distance bounded by <paramref name="max"/>.
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <param name="max">Maximum distance considered.</param>
    /// <returns>True when the distance is at most <paramref name="max"/>.</returns>
    public static bool WithinEditDistance(string a, string b, int max)
    {
        if (max <= 0)
        {
            return false;
        }

        if (Math.Abs(a.Length - b.Length) > max)
        {
            return false;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > max)
            {
                return false;
            }

            Array.Copy(current, previous, current.Length);
        }

        return previous[b.Length] <= max;
    }

    private static int AllowedDistance(int termLength) => termLength switch
    {
        <= 1 => 0,
        <= 5 => 1,
        _ => 2,
    };
}
