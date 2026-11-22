using System.Text.RegularExpressions;

namespace IsleWin.Core;

/// A claude.ai usage limit, as Claude Code worded it. A part it didn't say is null, never guessed.
public sealed record UsageLimit(string? Window, string? Resets, string? ContinuesAt)
{
    /// "5-hour limit" / "Weekly limit" / "Usage limit".
    public string Title => Window is { Length: > 0 } w ? char.ToUpperInvariant(w[0]) + w[1..] + " limit" : "Usage limit";
    /// "Resets 9:30pm · continues automatically at 9:30pm" (each part only if it was said).
    public string Detail => string.Join(" · ", new[]
    {
        Resets is { } r ? "Resets " + r : null,
        ContinuesAt is { } c ? "continues automatically at " + c : null,
    }.Where(x => x != null));
}

/// Isle's wording for the Claude face: "Done says what happened", the subagent count, the usage limit.
public static partial class ClaudeText
{
    // "You've hit your 5-hour limit · resets 9:30pm (America/Toronto) · out of usage credits"
    [GeneratedRegex(@"^You'?ve hit your (?<w>.+?) limit(?: · resets (?<r>[^·(]+?))?(?: \([^)]*\))?(?: · .*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRx();
    // "Usage limit reached · set to continue automatically at 7:40pm"
    [GeneratedRegex(@"continue automatically at (?<t>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueRx();
    [GeneratedRegex(@"^(?:usage|your)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericWindowRx();

    /// The limit an assistant message reports, or null when it isn't one.
    public static UsageLimit? ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = LimitRx().Match(text.Trim());
        if (!m.Success) return null;
        var w = m.Groups["w"].Value.Trim();
        return new UsageLimit(GenericWindowRx().IsMatch(w) ? null : w, m.Groups["r"].Success ? m.Groups["r"].Value.Trim() : null, null);
    }

    public static string? ParseContinue(string? text) => text != null && ContinueRx().Match(text) is { Success: true } m ? m.Groups["t"].Value.TrimEnd('.') : null;

    /// "3 agents · Explore ×2, Plan" — types in the order they started, repeats counted.
    public static string SubagentLine(IReadOnlyList<Subagent>? agents)
    {
        if (agents is not { Count: > 0 }) return "";
        var groups = agents.GroupBy(a => a.Type).Select(g => g.Count() > 1 ? $"{Pretty(g.Key)} ×{g.Count()}" : Pretty(g.Key));
        return $"{agents.Count} {(agents.Count == 1 ? "agent" : "agents")} · {string.Join(", ", groups)}";
    }

    static string Pretty(string type) => type.Length > 0 && char.IsLower(type[0]) && !type.Contains('-') ? char.ToUpperInvariant(type[0]) + type[1..] : type;

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")] private static partial Regex ListMarker();
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*>\s?")] private static partial Regex Quote();
    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(?=\S)(.+?)(?<=\S)\1")] private static partial Regex Emphasis();
    [GeneratedRegex(@"`+([^`]*)`+")] private static partial Regex Code();
    [GeneratedRegex(@"^\s*\|?[\s:\-|]+\|[\s:\-|]*$")] private static partial Regex TableRule();
    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")] private static partial Regex HRule();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// The Done face's line: the start of what Claude actually said, Markdown off — the first line of prose
    /// (a heading only when there's nothing else; a line ending in a colon runs on into the next), cut at a
    /// sentence end when one comes early enough. Code blocks, tables' rules and horizontal rules are skipped.
    public static string DoneLine(string? markdown, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var prose = new List<string>(); string? heading = null; bool fence = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) { fence = !fence; continue; }
            if (fence || line.Trim().Length == 0 || TableRule().IsMatch(line) || HRule().IsMatch(line)) { if (prose.Count > 0 && !prose[^1].EndsWith(':')) break; continue; }
            if (Heading().IsMatch(line)) { heading ??= Clean(Heading().Replace(line, "")); if (prose.Count > 0 && !prose[^1].EndsWith(':')) break; continue; }
            var t = Clean(Quote().Replace(ListMarker().Replace(line, ""), ""));
            if (t.Length == 0) continue;
            if (t.StartsWith('|')) t = string.Join(" · ", t.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            prose.Add(t);
            if (!t.EndsWith(':')) break; // a colon carries on into the line it introduces
        }
        var s = prose.Count > 0 ? string.Join(" ", prose) : heading ?? "";
        s = Spaces().Replace(s, " ").Trim();
        if (s.Length <= max) return s;
        // stop on Claude's own full stop if there is one past the first third
        int cut = -1;
        for (int i = max / 3; i < Math.Min(max, s.Length - 1); i++)
            if (s[i] is '.' or '!' or '?' && s[i + 1] == ' ') cut = i;
        return cut > 0 ? s[..(cut + 1)] : s[..max].TrimEnd(' ', ',', ';', ':') + "…";
    }

    static string Clean(string s)
    {
        s = Link().Replace(s, "$1");
        s = Code().Replace(s, "$1");
        for (int i = 0; i < 2; i++) s = Emphasis().Replace(s, "$2");
        return s.Trim().Trim('"', '“', '”');
    }
}
