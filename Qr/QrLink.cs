using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace KillerTools.Qr;

public enum QrLinkDisposition { DisplayOnly, Allowed, Warning, Blocked }

public sealed record QrLinkResult(QrLinkDisposition Disposition, Uri? Url, IReadOnlyList<string> Warnings);

/// <summary>Local checks only. Assessing a payload never opens it or sends a network request.</summary>
public static class QrLink
{
    private static readonly Regex WebLinks = new("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<string> FindWebLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<string>();
        foreach (Match match in WebLinks.Matches(text))
        {
            string link = match.Value.TrimEnd('.', ',', ';');
            while (link.Length > 0 && link[^1] is ')' or ']' or '}')
            {
                char close = link[^1], open = close == ')' ? '(' : close == ']' ? '[' : '{';
                if (link.Count(c => c == open) >= link.Count(c => c == close)) break;
                link = link[..^1].TrimEnd('.', ',', ';');
            }
            if (!links.Contains(link, StringComparer.Ordinal)) links.Add(link);
        }
        return links;
    }

    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc", "lnkd.in", "youtu.be"
    };
    private static readonly HashSet<string> DownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr", ".com", ".dll",
        ".zip", ".7z", ".rar", ".iso", ".img", ".dmg", ".pkg", ".apk", ".deb", ".rpm", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    public static QrLinkResult Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) return new(QrLinkDisposition.DisplayOnly, null, []);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
            return new(text.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? QrLinkDisposition.Blocked : QrLinkDisposition.DisplayOnly, null, []);
        if (url.Scheme is "wifi" or "mailto" or "tel" or "sms" or "smsto" or "bitcoin" or "ethereum")
            return new(QrLinkDisposition.DisplayOnly, null, []);
        if (Decode(text).Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return new(QrLinkDisposition.Blocked, null, ["HiddenCharacters"]);
        if (url.Scheme is not ("https" or "http") || url.Host.Length == 0)
            return new(QrLinkDisposition.Blocked, null, []);
        var warnings = new List<string>();
        if (url.Scheme == "http") warnings.Add("PlainHttp");
        if (!url.IsDefaultPort) warnings.Add("UnusualPort");
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out _)) warnings.Add("RawIp");
        if (url.UserInfo.Length > 0) warnings.Add("UserInfo");
        string host;
        try { host = new IdnMapping().GetUnicode(url.IdnHost); }
        catch (ArgumentException) { return new(QrLinkDisposition.Blocked, null, []); }
        if (host.Any(c => c > 127) || url.IdnHost.Contains("xn--", StringComparison.OrdinalIgnoreCase)) warnings.Add("InternationalHost");
        foreach (string label in host.Split('.'))
        {
            bool latin = label.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
            bool greek = label.Any(c => c is >= '\u0370' and <= '\u03ff');
            bool cyrillic = label.Any(c => c is >= '\u0400' and <= '\u052f');
            if ((latin && (greek || cyrillic)) || (greek && cyrillic))
            {
                warnings.Add("MixedScripts");
                break;
            }
        }
        string normalizedHost = url.IdnHost.TrimEnd('.');
        if (normalizedHost.Split('.').Length > 4) warnings.Add("DeepSubdomains");
        if (Shorteners.Contains(normalizedHost)) warnings.Add("Shortener");
        string path = Decode(url.AbsolutePath).TrimEnd('/');
        int dot = path.LastIndexOf('.');
        if (dot >= 0 && DownloadExtensions.Contains(path[dot..])) warnings.Add("DirectDownload");
        if (DisguisedDownload(path)) warnings.Add("DisguisedDownload");
        foreach (string parameter in url.Query.TrimStart('?').Split('&'))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0) continue;
            string value = Decode(parameter[(equals + 1)..]);
            if (DisguisedDownload(value) && !warnings.Contains("DisguisedDownload")) warnings.Add("DisguisedDownload");
            int valueDot = value.LastIndexOf('.');
            if (valueDot >= 0 && DownloadExtensions.Contains(value[valueDot..]) && !warnings.Contains("DirectDownload"))
                warnings.Add("DirectDownload");
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
            if (Uri.TryCreate(value, UriKind.Absolute, out var destination) && destination.Scheme is "http" or "https")
            {
                if (!warnings.Contains("EmbeddedDestination")) warnings.Add("EmbeddedDestination");
            }
        }
        return new(warnings.Count == 0 ? QrLinkDisposition.Allowed : QrLinkDisposition.Warning, url, warnings.ToArray());
    }

    private static string Decode(string value)
    {
        // Bound decoding so nested escaping cannot cause unbounded work.
        for (int i = 0; i < 3; i++)
        {
            string decoded = Uri.UnescapeDataString(value);
            if (decoded == value) break;
            value = decoded;
        }
        return value;
    }

    private static bool DisguisedDownload(string value)
    {
        string name = value[(value.LastIndexOf('/') + 1)..];
        string[] parts = name.Split('.');
        for (int i = 1; i < parts.Length - 1; i++)
            if (parts[i].ToLowerInvariant() is "exe" or "msi" or "scr" or "bat" or "cmd" or "ps1" or "vbs" or "js" or "dll" or "lnk" or "hta")
                return true;
        return false;
    }
}
