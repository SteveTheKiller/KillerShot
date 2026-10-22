using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KillerFamily.Deployment;

internal static class InstallationIntegration
{
    private static readonly string[] ProductValues = ["Installed", "InstallPath", "Version"];
    private static readonly string[] UninstallValues = ["DisplayName", "DisplayVersion", "Publisher", "InstallLocation",
        "DisplayIcon", "UninstallString", "QuietUninstallString", "NoModify", "NoRepair"];
    private const string UserEnvironment = "Environment";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    internal sealed record RegistryValue(object Value, RegistryValueKind Kind);

    internal interface IRegistry
    {
        bool KeyExists(string path);
        RegistryValue? ReadValue(string path, string name);
        void WriteValue(string path, string name, RegistryValue value);
        void DeleteValue(string path, string name);
        void DeleteKeyIfEmpty(string path);
    }

    /// <summary>Runs inside the image publication lease, before registration changes any integration.</summary>
    internal static void Register(RegistryKey hive, string productKey, string uninstallKey, bool machine,
        string installDirectory, string startMenuDirectory, string[] shortcutPaths, Action register) =>
        Register(new WindowsRegistry(hive), productKey, uninstallKey, machine, installDirectory,
            startMenuDirectory, shortcutPaths, register, BroadcastEnvironmentChange);

    internal static void Register(IRegistry registry, string productKey, string uninstallKey, bool machine,
        string installDirectory, string startMenuDirectory, string[] shortcutPaths, Action register,
        Action? environmentChanged = null)
    {
        // Snapshot before the first mutation. A failed read must prevent registration entirely.
        var product = SnapshotKey(registry, productKey, ProductValues);
        var uninstall = SnapshotKey(registry, uninstallKey, UninstallValues);
        string environment = machine ? MachineEnvironment : UserEnvironment;
        var path = SnapshotKey(registry, environment, ["Path"]);
        string directory = Path.GetFullPath(startMenuDirectory);
        bool directoryExisted = Directory.Exists(directory);
        var shortcuts = shortcutPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(SnapshotFile).ToArray();
        string entry = Path.GetFullPath(installDirectory);
        try { register(); }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); }
            }
            Restore(() =>
            {
                if (RestorePath(registry, path, entry)) environmentChanged?.Invoke();
            });
            if (!path.Existed) Restore(() => registry.DeleteKeyIfEmpty(path.Path));
            foreach (var key in new[] { uninstall, product })
            {
                foreach (var value in key.Values)
                    Restore(() => RestoreValue(registry, key.Path, value.Key, value.Value));
                if (!key.Existed) Restore(() => registry.DeleteKeyIfEmpty(key.Path));
            }
            foreach (var shortcut in shortcuts.Reverse()) Restore(() => RestoreFile(shortcut));
            if (!directoryExisted) Restore(() =>
            {
                // Never recursively remove a folder: unrelated files created meanwhile survive.
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, recursive: false);
            });
            if (errors.Count > 0)
                throw new IOException("Installation registration failed and integration rollback was incomplete.",
                    new AggregateException(new[] { failure }.Concat(errors)));
            throw;
        }
    }

    private sealed record KeySnapshot(string Path, bool Existed, Dictionary<string, RegistryValue?> Values);
    private sealed record FileSnapshot(string Path, byte[]? Bytes, FileAttributes Attributes, DateTime Created, DateTime Written);

    private static KeySnapshot SnapshotKey(IRegistry registry, string path, string[] names) =>
        new(path, registry.KeyExists(path), names.ToDictionary(name => name,
            name => Clone(registry.ReadValue(path, name)), StringComparer.OrdinalIgnoreCase));

    private static RegistryValue? Clone(RegistryValue? value) => value == null ? null : value with
    {
        Value = value.Value switch { byte[] bytes => bytes.ToArray(), string[] strings => strings.ToArray(), _ => value.Value }
    };

    private static bool Same(RegistryValue? left, RegistryValue? right)
    {
        if (left == null || right == null) return left == right;
        if (left.Kind != right.Kind) return false;
        return (left.Value, right.Value) switch
        {
            (byte[] a, byte[] b) => a.SequenceEqual(b),
            (string[] a, string[] b) => a.SequenceEqual(b),
            _ => Equals(left.Value, right.Value),
        };
    }

    private static void RestoreValue(IRegistry registry, string path, string name, RegistryValue? original)
    {
        if (Same(registry.ReadValue(path, name), original)) return;
        if (original == null) registry.DeleteValue(path, name);
        else registry.WriteValue(path, name, original);
    }

    private static bool RestorePath(IRegistry registry, KeySnapshot snapshot, string directory)
    {
        var before = snapshot.Values["Path"];
        string original = before?.Value as string ?? "";
        string expected = KillerShot.Services.CommandLinePath.WithEntry(original, directory);
        // Registration does not own an entry that was already present.
        if (expected == original) return false;
        var current = registry.ReadValue(snapshot.Path, "Path");
        if (Same(current, before) || current?.Value is not string latest) return false;
        RegistryValue? restore;
        if (latest == expected) restore = before;
        else
        {
            // Another installer may have updated PATH. Preserve its text, order and value type,
            // removing only the directory this registration added.
            string[] entries = latest.Split(';');
            string[] kept = entries.Where(value => !KillerShot.Services.CommandLinePath.SameDirectory(value, directory)).ToArray();
            if (kept.Length == entries.Length) return false;
            restore = new RegistryValue(string.Join(";", kept), current.Kind);
        }
        RestoreValue(registry, snapshot.Path, "Path", restore);
        return true;
    }

    private static FileSnapshot SnapshotFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("An installed shortcut is not a regular file: " + path);
            return new(path, File.ReadAllBytes(path), attributes, File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));
        }
        catch (FileNotFoundException) { return new(path, null, 0, default, default); }
        catch (DirectoryNotFoundException) { return new(path, null, 0, default, default); }
    }

    private static void RestoreFile(FileSnapshot file)
    {
        if (file.Bytes == null)
        {
            if (File.Exists(file.Path)) File.Delete(file.Path);
            return;
        }
        // Use a sibling file so a failed restoration does not truncate the surviving shortcut.
        string temporary = file.Path + ".rollback-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(file.Bytes);
                output.Flush(true);
            }
            if (File.Exists(file.Path)) File.SetAttributes(file.Path, FileAttributes.Normal);
            File.Move(temporary, file.Path, overwrite: true);
            File.SetCreationTimeUtc(file.Path, file.Created);
            File.SetLastWriteTimeUtc(file.Path, file.Written);
            File.SetAttributes(file.Path, file.Attributes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class WindowsRegistry(RegistryKey hive) : IRegistry
    {
        public bool KeyExists(string path) { using var key = hive.OpenSubKey(path); return key != null; }
        public RegistryValue? ReadValue(string path, string name)
        {
            using var key = hive.OpenSubKey(path);
            object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value == null ? null : new(value, key!.GetValueKind(name));
        }
        public void WriteValue(string path, string name, RegistryValue value)
        {
            using var key = hive.CreateSubKey(path, writable: true);
            key.SetValue(name, value.Value, value.Kind);
        }
        public void DeleteValue(string path, string name)
        {
            using var key = hive.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        public void DeleteKeyIfEmpty(string path)
        {
            using (var key = hive.OpenSubKey(path))
                if (key == null || key.ValueCount != 0 || key.SubKeyCount != 0) return;
            // DeleteSubKey refuses a nonempty subtree. Never use recursive key deletion here.
            hive.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string text,
        uint flags, uint timeout, out IntPtr result);

    private static void BroadcastEnvironmentChange() =>
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 2000, out _);
}
