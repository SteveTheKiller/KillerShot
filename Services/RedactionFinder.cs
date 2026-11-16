using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace KillerShot.Services;

/// <summary>One recognized word and its box in image pixels.</summary>
internal sealed record OcrWord(string Text, Rect Box);

/// <summary>The kinds of sensitive text auto redact looks for.</summary>
[Flags]
internal enum RedactKinds
{
    None = 0,
    Email = 1,
    Card = 2,
    Phone = 4,
    Ssn = 8,
    Ip = 16,
    Secret = 32,
    All = Email | Card | Phone | Ssn | Ip | Secret,
}

/// <summary>
/// Finds sensitive text in OCR lines and returns the boxes to cover, in image pixels. Each line is
/// matched as its words joined by single spaces, so a number OCR splits into several words is still
/// one match, and a match covers every word it touches. Covering a whole word is deliberate: a box
/// that stops partway through a word could leave part of a secret readable.
/// </summary>
internal static partial class RedactionFinder
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\d-])\d(?:[ -]?\d){12,18}(?![\d-])")]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"(?<![\d-])(\d{3})[- ](\d{2})[- ](\d{4})(?![\d-])")]
    private static partial Regex SsnPattern();

    // North American numbers with an optional +1, and international numbers that start with +.
    [GeneratedRegex(@"(?<![\d+])(?:\+?1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]\d{4}(?!\d)|(?<![\d+])\+\d{1,3}(?:[ .-]?\d{2,4}){2,5}(?!\d)")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])")]
    private static partial Regex Ipv4Pattern();

    // Full eight-group addresses, or shortened ones with :: (a time like 12:30:45 has neither).
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,6}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?|::(?:[0-9A-Fa-f]{1,4}:){0,5}[0-9A-Fa-f]{1,4})(?![0-9A-Fa-f:])")]
    private static partial Regex Ipv6Pattern();

    // A value after a label like "password:" or "api_key=". Only the value is covered.
    [GeneratedRegex(@"(?i)\b(?:password|passwd|pwd|passcode|pin|secret|token|api[_ -]?key|access[_ -]?key|client[_ -]?secret|private[_ -]?key)\s*[:=]\s*(\S+)")]
    private static partial Regex LabeledSecretPattern();

    // Well-known token shapes: AWS keys, GitHub and Slack tokens, OpenAI-style keys, Google API keys, JWTs.
    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{36,}\b|\bxox[abprs]-[A-Za-z0-9-]{10,}|\bsk-[A-Za-z0-9_-]{20,}|\bAIza[0-9A-Za-z_-]{35}\b|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex TokenPattern();

    // A long run of key-like characters that mixes letters and digits.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_=-])(?=[A-Za-z0-9+/_=-]*\d)(?=[A-Za-z0-9+/_=-]*[A-Za-z])[A-Za-z0-9+/_=-]{32,}(?![A-Za-z0-9+/_=-])")]
    private static partial Regex LongKeyPattern();

    /// <summary>The boxes that cover every match of the chosen kinds, padded a little.</summary>
    public static IReadOnlyList<Rect> Find(IEnumerable<IReadOnlyList<OcrWord>> lines, RedactKinds kinds)
    {
        var boxes = new List<Rect>();
        if (kinds == RedactKinds.None) return boxes;
        foreach (var words in lines)
        {
            if (words.Count == 0) continue;
            var text = new StringBuilder();
            var starts = new int[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0) text.Append(' ');
                starts[i] = text.Length;
                text.Append(words[i].Text);
            }
            string line = text.ToString();
            var spans = new List<(int Start, int End)>();
            foreach (var span in Matches(line, kinds)) spans.Add(span);
            foreach (var (start, end) in spans)
            {
                Rect? box = null;
                for (int i = 0; i < words.Count; i++)
                {
                    int ws = starts[i], we = ws + words[i].Text.Length;
                    if (we <= start || ws >= end) continue;
                    box = box is { } b ? Rect.Union(b, words[i].Box) : words[i].Box;
                }
                if (box is { } found) boxes.Add(Pad(found));
            }
        }
        return boxes;
    }

    private static IEnumerable<(int Start, int End)> Matches(string line, RedactKinds kinds)
    {
        if (kinds.HasFlag(RedactKinds.Email))
            foreach (Match m in EmailPattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
        if (kinds.HasFlag(RedactKinds.Card))
            foreach (Match m in CardPattern().Matches(line))
                if (IsCardNumber(m.Value)) yield return (m.Index, m.Index + m.Length);
        if (kinds.HasFlag(RedactKinds.Ssn))
            foreach (Match m in SsnPattern().Matches(line))
                if (IsSsn(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)) yield return (m.Index, m.Index + m.Length);
        if (kinds.HasFlag(RedactKinds.Phone))
            foreach (Match m in PhonePattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
        if (kinds.HasFlag(RedactKinds.Ip))
        {
            foreach (Match m in Ipv4Pattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
            foreach (Match m in Ipv6Pattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
        }
        if (kinds.HasFlag(RedactKinds.Secret))
        {
            foreach (Match m in LabeledSecretPattern().Matches(line))
            {
                var value = m.Groups[1];
                yield return (value.Index, value.Index + value.Length);
            }
            foreach (Match m in TokenPattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
            foreach (Match m in LongKeyPattern().Matches(line)) yield return (m.Index, m.Index + m.Length);
        }
    }

    /// <summary>13 to 19 digits that pass the Luhn checksum every card number carries.</summary>
    internal static bool IsCardNumber(string text)
    {
        int sum = 0, count = 0;
        bool doubled = false;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9') return false;
            int d = c - '0';
            if (doubled) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubled = !doubled;
            count++;
        }
        return count is >= 13 and <= 19 && sum % 10 == 0;
    }

    /// <summary>Area 000, 666 and 900 to 999, group 00 and serial 0000 are never issued.</summary>
    private static bool IsSsn(string area, string group, string serial) =>
        area != "000" && area != "666" && area[0] != '9' && group != "00" && serial != "0000";

    // A little margin, because OCR boxes hug the glyphs and antialiased edges reach past them.
    private static Rect Pad(Rect box)
    {
        double pad = Math.Max(2, Math.Round(box.Height * 0.15));
        box.Inflate(pad, pad);
        return box;
    }
}
