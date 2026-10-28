using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace KillerShot.Services
{
    /// <summary>
    /// KillerShot.PrintScreen.exe (PrintScreenHelper/), the helper that catches Print Screen over
    /// windows run as administrator. Windows hides those keys from KillerShot itself; it lets a
    /// program with UI Access see them, and grants UI Access only to a signed file in Program
    /// Files. So the helper always lives in Program Files\KillerShot: an all-users install puts it
    /// there, and any other copy puts it there with one permission prompt when the Print Screen
    /// switch is turned on (App.OfferPrintScreenHelper).
    ///
    /// KillerShot.exe carries the helper as a resource. The tray starts it while the switch is on
    /// (Shell/TrayHost.cs) and stops it through a named event; it also quits by itself when
    /// KillerShot exits.
    /// </summary>
    internal static class PrintScreenHelper
    {
        internal const string FileName = "KillerShot.PrintScreen.exe";
        /// <summary>The window message the helper posts; matches PrintScreenHelper/Program.cs.</summary>
        internal const string MessageName = "KillerShot.PrintScreenPressed";
        private const string ResourceName = "KillerShot.PrintScreen.exe";

        /// <summary>Program Files\KillerShot, the all-users install folder.</summary>
        internal static string InstalledPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KillerShot", FileName);

        /// <summary>Self-tests point this at a test build of the helper.</summary>
        internal static string? PathOverride { get; set; }

        internal static string HelperPath => PathOverride ?? InstalledPath;

        internal static bool IsInstalled => File.Exists(HelperPath);

        /// <summary>Writes the helper carried inside this exe to <paramref name="target"/>, through a
        /// temporary file in the same folder that must carry a valid signature before it replaces
        /// anything (UI Access is refused to an unsigned file anyway). A helper that is running
        /// keeps its file locked; the copy is then left as it is and that is reported.</summary>
        internal static void Install(string target, bool requireSignature)
        {
            string folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".helper");
            try
            {
                using (var source = typeof(PrintScreenHelper).Assembly.GetManifestResourceStream(ResourceName)
                    ?? throw new FileNotFoundException("This build of KillerShot does not carry the Print Screen helper."))
                using (var copy = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(copy);
                if (requireSignature && !CodeSignature.GetSignerInfo(temp).valid)
                    throw new System.Security.SecurityException("The Print Screen helper has no valid signature.");
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <summary>Starts the helper for <paramref name="window"/> with the modifier sets to take
        /// (comma separated MOD_ flags) and the name of the event that stops it. Null when it is
        /// missing or Windows refuses it, as it does outside Program Files or without a trusted
        /// signature; KillerShot's own hook carries on alone then.</summary>
        internal static Process? Start(IntPtr window, string modifiers, string stopEvent)
        {
            if (!IsInstalled) return null;
            var start = new ProcessStartInfo(HelperPath, string.Join(' ',
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), modifiers, stopEvent))
            {
                // Through the shell: Windows starts a UI Access program only that way, and with no
                // error box of its own when it refuses.
                UseShellExecute = true,
                ErrorDialog = false,
                WorkingDirectory = Path.GetDirectoryName(HelperPath)!,
            };
            try { return Process.Start(start); }
            catch (Win32Exception) { return null; }
        }
    }
}
