using System.Globalization;
using System.Net;

namespace KillerTools.Qr;

public enum QrLinkDisposition { DisplayOnly, Allowed, Warning, Blocked }

public sealed record QrLinkResult(QrLinkDisposition Disposition, Uri? Url, IReadOnlyList<string> Warnings);

/// <summary>Local checks only. Assessing a payload never opens it or sends a network request.</summary>
public static class QrLink
{
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
        if (url.Scheme is not ("https" or "http") || url.Host.Length == 0 || text.Any(char.IsControl))
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
        string path = Uri.UnescapeDataString(url.AbsolutePath).TrimEnd('/');
        int dot = path.LastIndexOf('.');
        if (dot >= 0 && DownloadExtensions.Contains(path[dot..])) warnings.Add("DirectDownload");
        return new(warnings.Count == 0 ? QrLinkDisposition.Allowed : QrLinkDisposition.Warning, url, warnings.ToArray());
    }
}
