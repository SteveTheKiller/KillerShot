using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

/// <summary>Passing a finished capture on: its file path to the clipboard, the file to a program
/// the user chose, or a new email with the picture attached. Programs get the path as a single
/// structured argument, never a command line built from text.</summary>
internal static class Handoff
{
    public const int MaxApps = 3;

    /// <summary>The programs chosen for Open with, by full path.</summary>
    public static IReadOnlyList<string> Apps
    {
        get
        {
            try
            {
                return Settings.Get("HandoffApps") is { } json && JsonNode.Parse(json) is JsonArray list
                    ? list.Select(n => n?.GetValue<string>() ?? "").Where(IsProgram).Take(MaxApps).ToList() : [];
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { return []; }
        }
        set => Settings.Set("HandoffApps", new JsonArray(value.Where(IsProgram).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxApps).Select(p => (JsonNode)p).ToArray()).ToJsonString());
    }

    /// <summary>A program KillerShot may start: an existing .exe file given by full path.</summary>
    public static bool IsProgram(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static string AppName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path); }
        catch (Exception error) when (error is IOException or FileNotFoundException) { return Path.GetFileNameWithoutExtension(path); }
    }

    /// <summary>A file to hand on: the saved capture, or a temporary copy that expires like a drag export.</summary>
    public static string FileFor(BitmapSource image, string? saved) => saved is { } path && File.Exists(path) ? path : ImageDrag.ExportFile(image);

    /// <summary>Starts a chosen program with the file. Returns an error message, or null.</summary>
    public static string? OpenWith(string app, string file)
    {
        if (!IsProgram(app)) return string.Format(LocaleManager.Loc("Str_Handoff_AppMissing"), app);
        try
        {
            var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? "" };
            start.ArgumentList.Add(file);
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return error.Message; }
    }

    /// <summary>Opens the default email program's new-message window with the file attached, using
    /// Windows' Simple MAPI. Runs off the interface thread, since the window can stay open a while.</summary>
    public static Task<string?> EmailAsync(string file)
    {
        var done = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(SendMail(file)); }
            catch (Exception error) { done.SetResult(error.Message); }
        }) { IsBackground = true, Name = "KillerShot email" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static string? SendMail(string file)
    {
        try
        {
            // The Unicode entry point keeps paths outside the system code page intact, such as a
            // Polish, Russian or Chinese user name in the temporary folder. Mail programs that only
            // offer the older one get the ANSI call.
            int result;
            try { result = Send<MapiFileDescW, MapiMessageW>(file, MAPISendMailW); }
            catch (EntryPointNotFoundException) { result = Send<MapiFileDesc, MapiMessage>(file, MAPISendMail); }
            return result switch
            {
                0 or MapiUserAbort => null,
                _ => LocaleManager.Loc("Str_Handoff_NoMail"),
            };
        }
        catch (DllNotFoundException) { return LocaleManager.Loc("Str_Handoff_NoMail"); }
        catch (EntryPointNotFoundException) { return LocaleManager.Loc("Str_Handoff_NoMail"); }
    }

    private interface IMapiFile { string? PathName { set; } string? FileName { set; } int Position { set; } }
    private interface IMapiMessage { string? Subject { set; } int FileCount { set; } IntPtr Files { set; } }

    private static int Send<TFile, TMessage>(string file, Func<IntPtr, IntPtr, TMessage, int, int, int> send)
        where TFile : struct, IMapiFile where TMessage : class, IMapiMessage, new()
    {
        TFile attachment = default;
        attachment.Position = -1;
        attachment.PathName = file;
        attachment.FileName = Path.GetFileName(file);
        IntPtr files = Marshal.AllocHGlobal(Marshal.SizeOf<TFile>());
        try
        {
            Marshal.StructureToPtr(attachment, files, false);
            var message = new TMessage { Subject = Path.GetFileNameWithoutExtension(file), FileCount = 1, Files = files };
            return send(IntPtr.Zero, IntPtr.Zero, message, MapiLogonUi | MapiDialog, 0);
        }
        finally
        {
            Marshal.DestroyStructure<TFile>(files);
            Marshal.FreeHGlobal(files);
        }
    }

    private const int MapiLogonUi = 0x1, MapiDialog = 0x8, MapiUserAbort = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiMessage : IMapiMessage
    {
        public int Reserved;
        public string? subject;
        public string? NoteText;
        public string? MessageType;
        public string? DateReceived;
        public string? ConversationId;
        public int Flags;
        public IntPtr Originator;
        public int RecipientCount;
        public IntPtr Recipients;
        public int fileCount;
        public IntPtr files;
        public string? Subject { set => subject = value; }
        public int FileCount { set => fileCount = value; }
        public IntPtr Files { set => files = value; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiFileDesc : IMapiFile
    {
        public int Reserved;
        public int Flags;
        public int position;
        public string? pathName;
        public string? fileName;
        public IntPtr FileType;
        public string? PathName { set => pathName = value; }
        public string? FileName { set => fileName = value; }
        public int Position { set => position = value; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class MapiMessageW : IMapiMessage
    {
        public int Reserved;
        public string? subject;
        public string? NoteText;
        public string? MessageType;
        public string? DateReceived;
        public string? ConversationId;
        public int Flags;
        public IntPtr Originator;
        public int RecipientCount;
        public IntPtr Recipients;
        public int fileCount;
        public IntPtr files;
        public string? Subject { set => subject = value; }
        public int FileCount { set => fileCount = value; }
        public IntPtr Files { set => files = value; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDescW : IMapiFile
    {
        public int Reserved;
        public int Flags;
        public int position;
        public string? pathName;
        public string? fileName;
        public IntPtr FileType;
        public string? PathName { set => pathName = value; }
        public string? FileName { set => fileName = value; }
        public int Position { set => position = value; }
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Ansi)]
    private static extern int MAPISendMail(IntPtr session, IntPtr uiParam, MapiMessage message, int flags, int reserved);

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MAPISendMailW(IntPtr session, IntPtr uiParam, MapiMessageW message, int flags, int reserved);
}
