using System.Globalization;
using System.IO;
using System.Text;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    public static string CaptureReference(long id)
    {
        lock (_gate)
        {
            if (_db == null || id <= 0) throw new InvalidOperationException(Loc("Str_Lib_Missing"));
            string path = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "library:v2:" + path + ":" + id.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static CaptureItem? ResolveReference(string reference)
    {
        string[] parts = reference.Trim().Split(':');
        if (parts.Length != 4 || !parts[0].Equals("library", StringComparison.OrdinalIgnoreCase) || parts[1] != "v2"
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0
            || parts[2].Length == 0 || parts[2].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return null;
        try
        {
            string encoded = parts[2].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            string path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.GetFullPath(path);
            lock (_gate)
            {
                if (_db == null || !string.Equals(path, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)) return null;
                return Items().FirstOrDefault(item => item.Id == id);
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
