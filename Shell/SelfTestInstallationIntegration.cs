using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using KillerFamily.Deployment;

namespace KillerShot.Shell;

/// <summary>Integration rollback with scratch files and a fake registry. No installed state is changed.</summary>
internal static class SelfTestInstallationIntegration
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-installation-integration") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        bool created = false;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new integration test folder.");
            Directory.CreateDirectory(folder);
            created = true;
            FailureMatrix(folder, report);
            UnrelatedChangesSurvive(folder, report);
            PathCases(folder, report);
            RegistryRollbackFailure(folder, report);
            ShortcutRollbackFailure(folder, report);
            SnapshotFailure(folder, report);
            SuccessfulRegistration(folder, report);
            EmptyEnvironmentRollback(folder, report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            if (created) File.WriteAllLines(Path.Combine(folder, "installation-integration-report.txt"), report);
            else Console.Error.WriteLine(string.Join(Environment.NewLine, report));
            App.Instance.Shutdown(code);
        }
        return true;
    }

    private static void FailureMatrix(string folder, List<string> report)
    {
        foreach (bool upgrade in new[] { false, true })
        {
            for (int stage = 0; stage < 16; stage++)
            {
                var f = new Fixture(folder, (upgrade ? "upgrade-" : "new-") + stage, upgrade);
                var failure = new IOException("Injected registration stage " + stage);
                Exception observed = CaptureFailure(() => f.Publish(() =>
                {
                    var stages = f.RegistrationStages();
                    for (int index = 0; index <= stage; index++) stages[index]();
                    throw failure;
                }));
                Require(ReferenceEquals(observed, failure), "Original registration failure was not preserved: " + observed);
                f.CheckRollback();
                Require(f.Broadcasts == (stage == 15 ? 1 : 0), "PATH restoration broadcast count was incorrect.");
                report.Add((upgrade ? "upgrade" : "new install") + " rollback after stage " + stage + " PASS");
            }
        }
    }

    private static void UnrelatedChangesSurvive(string folder, List<string> report)
    {
        var f = new Fixture(folder, "unrelated", false);
        CaptureFailure(() => f.Publish(() =>
        {
            foreach (var stage in f.RegistrationStages()) stage();
            f.Registry.WriteValue("Product", "Theme", new("Custom", RegistryValueKind.String));
            f.Registry.WriteValue(@"Product\Settings", "Count", new(42, RegistryValueKind.DWord));
            f.Registry.WriteValue("Uninstall", "OtherOwner", new("Keep", RegistryValueKind.String));
            f.Registry.WriteValue(f.EnvironmentKey, "OtherVariable", new("Keep", RegistryValueKind.String));
            File.WriteAllText(Path.Combine(f.Menu, "unrelated.txt"), "Keep");
            throw new IOException("Fail after unrelated additions.");
        }));
        f.CheckImageAndShortcuts();
        Require(Directory.Exists(f.Menu) && File.ReadAllText(Path.Combine(f.Menu, "unrelated.txt")) == "Keep",
            "An unrelated Start Menu file was removed.");
        Require(f.Registry.ReadValue("Product", "Installed") == null && f.Registry.ReadValue("Product", "Theme")?.Value as string == "Custom" &&
            Equals(f.Registry.ReadValue(@"Product\Settings", "Count")?.Value, 42) &&
            f.Registry.ReadValue("Uninstall", "DisplayName") == null && f.Registry.ReadValue("Uninstall", "OtherOwner")?.Value as string == "Keep" &&
            f.Registry.ReadValue(f.EnvironmentKey, "Path") == null && f.Registry.ReadValue(f.EnvironmentKey, "OtherVariable")?.Value as string == "Keep",
            "Rollback changed unrelated registry values or subkeys.");
        report.Add("unrelated registry values, subkeys and Start Menu files survive PASS");
    }

    private static void PathCases(string folder, List<string> report)
    {
        foreach (bool machine in new[] { false, true })
        {
            var f = new Fixture(folder, machine ? "machine-path" : "user-path", false, machine);
            const string original = "%SystemRoot%\\system32;; C:\\Other ";
            f.Registry.WriteValue(f.EnvironmentKey, "Path", new(original, RegistryValueKind.ExpandString));
            CaptureFailure(() => f.Publish(() =>
            {
                f.AddPath();
                var added = (string)f.Registry.ReadValue(f.EnvironmentKey, "Path")!.Value;
                f.Registry.WriteValue(f.EnvironmentKey, "Path", new(added + ";;C:\\Concurrent;", RegistryValueKind.String));
                throw new IOException("Fail after concurrent PATH change.");
            }));
            var restored = f.Registry.ReadValue(f.EnvironmentKey, "Path");
            Require(restored?.Kind == RegistryValueKind.String && (string)restored.Value == "%SystemRoot%\\system32; C:\\Other ;;C:\\Concurrent;",
                "Rollback overwrote concurrent PATH entries, whitespace or value kind.");
            Require(f.Broadcasts == 1, "A changed PATH was not broadcast after rollback.");
            report.Add((machine ? "machine" : "user") + " PATH preserves concurrent text and type PASS");
        }
        var present = new Fixture(folder, "path-already-present", false);
        string prior = "C:\\Other;\"" + present.Installed.ToUpperInvariant() + "\\\"";
        present.Registry.WriteValue(present.EnvironmentKey, "Path", new(prior, RegistryValueKind.String));
        CaptureFailure(() => present.Publish(() =>
        {
            present.AddPath();
            present.Registry.WriteValue(present.EnvironmentKey, "Path", new(prior + ";C:\\Concurrent", RegistryValueKind.String));
            throw new IOException("Fail with existing PATH entry.");
        }));
        Require((string)present.Registry.ReadValue(present.EnvironmentKey, "Path")!.Value == prior + ";C:\\Concurrent" && present.Broadcasts == 0,
            "Rollback removed a preexisting PATH entry.");
        report.Add("preexisting PATH entry and concurrent changes survive PASS");

        foreach (var before in new InstallationIntegration.RegistryValue[] {
            new("", RegistryValueKind.String), new("%SystemRoot%\\system32;; ", RegistryValueKind.ExpandString),
            new(new byte[] { 1, 7, 9 }, RegistryValueKind.Binary) })
        {
            var f = new Fixture(folder, "path-exact-" + before.Kind, false);
            f.Registry.WriteValue(f.EnvironmentKey, "Path", before);
            string original = f.Registry.Dump();
            CaptureFailure(() => f.Publish(() => { f.AddPath(); throw new IOException("Fail after PATH."); }));
            Require(f.Registry.Dump() == original, "PATH did not recover exact prior raw data and kind.");
            report.Add("PATH restores original " + before.Kind + " data PASS");
        }
    }

    private static void RegistryRollbackFailure(string folder, List<string> report)
    {
        var f = new Fixture(folder, "registry-rollback-failure", true);
        var original = new IOException("Original registration error.");
        var rollback = new UnauthorizedAccessException("Injected restore failure.");
        Exception observed = CaptureFailure(() => f.Publish(() =>
        {
            foreach (var stage in f.RegistrationStages()) stage();
            f.Registry.BeforeWrite = (path, name) => { if (path == "Product" && name == "Version") throw rollback; };
            throw original;
        }));
        f.Registry.BeforeWrite = null;
        Require(Contains(observed, original) && Contains(observed, rollback), "Rollback failure obscured either failure.");
        f.CheckImageAndShortcuts();
        Require(Equals(f.Registry.ReadValue("Product", "Installed")?.Value, 31) &&
            Equals(f.Registry.ReadValue("Uninstall", "NoModify")?.Value, 7) &&
            (string)f.Registry.ReadValue(f.EnvironmentKey, "Path")!.Value == Fixture.OriginalPath,
            "One registry restoration failure prevented the remaining rollback.");
        report.Add("registry rollback failures are observable and remaining recovery continues PASS");
    }

    private static void ShortcutRollbackFailure(string folder, List<string> report)
    {
        var f = new Fixture(folder, "shortcut-rollback-failure", true);
        var original = new IOException("Original shortcut-stage error.");
        FileStream? locked = null;
        try
        {
            Exception observed = CaptureFailure(() => f.Publish(() =>
            {
                foreach (var stage in f.RegistrationStages()) stage();
                locked = new FileStream(f.DesktopShortcut, FileMode.Open, FileAccess.Read, FileShare.None);
                throw original;
            }));
            Require(Contains(observed, original) && observed.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Count >= 2,
                "Shortcut rollback failure was swallowed.");
            Require(File.ReadAllText(f.Target) == "old executable" && File.ReadAllText(f.MenuShortcut) == "old menu shortcut" &&
                f.Registry.Dump() == f.RegistryBefore, "A locked shortcut prevented independent recovery.");
        }
        finally { locked?.Dispose(); }
        report.Add("locked shortcut reports incomplete rollback and other resources recover PASS");
    }

    private static void SnapshotFailure(string folder, List<string> report)
    {
        var f = new Fixture(folder, "snapshot-failure", true);
        bool invoked = false;
        using (var locked = new FileStream(f.MenuShortcut, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            CaptureFailure(() => f.Publish(() => invoked = true));
            Require(!invoked, "Registration ran after a snapshot failure.");
        }
        f.CheckRollback();
        report.Add("snapshot failure prevents registration and restores the published image PASS");
    }

    private static void SuccessfulRegistration(string folder, List<string> report)
    {
        foreach (bool upgrade in new[] { false, true })
        {
            var f = new Fixture(folder, upgrade ? "upgrade-success" : "new-success", upgrade);
            f.Publish(() => { foreach (var stage in f.RegistrationStages()) stage(); });
            Require(File.ReadAllText(f.Target) == "new executable" && File.ReadAllText(f.MenuShortcut) == "new menu shortcut" &&
                File.ReadAllText(f.DesktopShortcut) == "new desktop shortcut" && Equals(f.Registry.ReadValue("Product", "Installed")?.Value, 1) &&
                f.Registry.ReadValue("Uninstall", "DisplayVersion")?.Value as string == "new DisplayVersion" &&
                ((string)f.Registry.ReadValue(f.EnvironmentKey, "Path")!.Value).Contains(f.Installed) && f.Broadcasts == 0,
                "Successful registration was rolled back or omitted an integration.");
            Require(!Directory.EnumerateFiles(f.Installed).Any(path => path.Contains(".previous-") || path.Contains(".install-")),
                "Successful publication retained staging files.");
            report.Add((upgrade ? "upgrade" : "new install") + " success retains all integration PASS");
        }
    }

    private static void EmptyEnvironmentRollback(string folder, List<string> report)
    {
        var f = new Fixture(folder, "empty-environment", false);
        CaptureFailure(() => f.Publish(() =>
        {
            f.Registry.CreateKey(f.EnvironmentKey);
            throw new IOException("PATH value creation failed after opening a new environment key.");
        }));
        f.CheckRollback();
        report.Add("failed PATH creation restores missing environment key PASS");
    }

    private static Exception CaptureFailure(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure did not propagate.");
    }

    private static bool Contains(Exception outer, Exception expected) => ReferenceEquals(outer, expected) ||
        (outer is AggregateException aggregate && aggregate.InnerExceptions.Any(error => Contains(error, expected))) ||
        (outer.InnerException != null && Contains(outer.InnerException, expected));

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        internal const string OriginalPath = "%SystemRoot%\\system32;; C:\\Existing ";
        internal readonly string Installed, Source, Target, Menu, MenuShortcut, DesktopShortcut, EnvironmentKey;
        internal readonly FakeRegistry Registry = new();
        internal readonly string RegistryBefore;
        internal int Broadcasts;
        private readonly bool _upgrade, _machine;
        private readonly (byte[] Bytes, FileAttributes Attributes, DateTime Created, DateTime Written)[] _shortcuts;

        internal Fixture(string parent, string name, bool upgrade, bool machine = false)
        {
            string folder = Path.Combine(parent, name);
            Installed = Path.Combine(folder, "installed");
            Source = Path.Combine(folder, "source.exe");
            Target = Path.Combine(Installed, "KillerShot.exe");
            Menu = Path.Combine(folder, "StartMenu");
            MenuShortcut = Path.Combine(Menu, "KillerShot.lnk");
            DesktopShortcut = Path.Combine(folder, "Desktop", "KillerShot.lnk");
            EnvironmentKey = machine ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : "Environment";
            _upgrade = upgrade;
            _machine = machine;
            Directory.CreateDirectory(Installed);
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopShortcut)!);
            File.WriteAllText(Source, "new executable");
            if (upgrade)
            {
                File.WriteAllText(Target, "old executable");
                Directory.CreateDirectory(Menu);
                File.WriteAllText(MenuShortcut, "old menu shortcut");
                File.WriteAllText(DesktopShortcut, "old desktop shortcut");
                foreach (string shortcut in new[] { MenuShortcut, DesktopShortcut })
                {
                    File.SetCreationTimeUtc(shortcut, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                    File.SetLastWriteTimeUtc(shortcut, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                    File.SetAttributes(shortcut, FileAttributes.Hidden | FileAttributes.Archive);
                }
                Registry.WriteValue("Product", "Installed", new(31, RegistryValueKind.DWord));
                Registry.WriteValue("Product", "InstallPath", new("%LOCALAPPDATA%\\Old", RegistryValueKind.ExpandString));
                Registry.WriteValue("Product", "Version", new(123456789012L, RegistryValueKind.QWord));
                foreach (string nameToSeed in UninstallNames.Take(8))
                    Registry.WriteValue("Uninstall", nameToSeed, new("old " + nameToSeed, RegistryValueKind.String));
                Registry.WriteValue("Uninstall", "Publisher", new(new byte[] { 0, 42, 255 }, RegistryValueKind.Binary));
                Registry.WriteValue("Uninstall", "DisplayIcon", new(new[] { "old", "icon" }, RegistryValueKind.MultiString));
                Registry.WriteValue("Uninstall", "NoModify", new(7, RegistryValueKind.DWord));
                Registry.WriteValue("Product", "Theme", new("Custom", RegistryValueKind.String));
                Registry.WriteValue(@"Product\Settings", "Zoom", new(125, RegistryValueKind.DWord));
                Registry.WriteValue("Uninstall", "OtherOwner", new("Keep", RegistryValueKind.String));
                Registry.WriteValue(EnvironmentKey, "Path", new(OriginalPath, RegistryValueKind.ExpandString));
                Registry.WriteValue(EnvironmentKey, "OtherVariable", new("Keep", RegistryValueKind.String));
            }
            RegistryBefore = Registry.Dump();
            _shortcuts = upgrade ? new[] { MenuShortcut, DesktopShortcut }.Select(path =>
                (File.ReadAllBytes(path), File.GetAttributes(path), File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path))).ToArray() : [];
        }

        private static readonly string[] UninstallNames = ["DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "DisplayIcon",
            "UninstallString", "QuietUninstallString", "NoModify", "NoRepair"];

        internal List<Action> RegistrationStages()
        {
            var stages = new List<Action> {
                () => Directory.CreateDirectory(Menu),
                () => WriteShortcut(MenuShortcut, "new menu shortcut"),
                () => WriteShortcut(DesktopShortcut, "new desktop shortcut"),
                () => Registry.WriteValue("Product", "Installed", new(1, RegistryValueKind.DWord)),
                () => Registry.WriteValue("Product", "InstallPath", new(Target, RegistryValueKind.String)),
                () => Registry.WriteValue("Product", "Version", new("new version", RegistryValueKind.String)) };
            foreach (string name in UninstallNames)
                stages.Add(() => Registry.WriteValue("Uninstall", name, new("new " + name, RegistryValueKind.String)));
            stages.Add(AddPath);
            return stages;
        }

        private static void WriteShortcut(string path, string content)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, content);
        }

        internal void AddPath()
        {
            var previous = Registry.ReadValue(EnvironmentKey, "Path");
            string current = previous?.Value as string ?? "";
            var kind = previous?.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? previous.Kind : RegistryValueKind.ExpandString;
            string next = Services.CommandLinePath.WithEntry(current, Installed);
            if (next != current) Registry.WriteValue(EnvironmentKey, "Path", new(next, kind));
        }

        internal void Publish(Action registration) => InstalledImagePublication.Publish(Source, Target, () =>
            InstallationIntegration.Register(Registry, "Product", "Uninstall", _machine, Installed, Menu,
                [MenuShortcut, DesktopShortcut], registration, () => Broadcasts++));

        internal void CheckImageAndShortcuts()
        {
            Require(_upgrade ? File.ReadAllText(Target) == "old executable" : !File.Exists(Target), "Installed image did not recover.");
            string[] paths = [MenuShortcut, DesktopShortcut];
            for (int index = 0; index < paths.Length; index++)
            {
                string path = paths[index];
                if (!_upgrade) { Require(!File.Exists(path), "New shortcut survived failed registration."); continue; }
                var prior = _shortcuts[index];
                Require(File.ReadAllBytes(path).SequenceEqual(prior.Bytes) && File.GetAttributes(path) == prior.Attributes &&
                    File.GetCreationTimeUtc(path) == prior.Created && File.GetLastWriteTimeUtc(path) == prior.Written,
                    "Shortcut content, attributes or timestamps did not recover: " + path);
            }
        }

        internal void CheckRollback()
        {
            CheckImageAndShortcuts();
            Require(Directory.Exists(Menu) == _upgrade, "Start Menu directory existence did not recover.");
            Require(Registry.Dump() == RegistryBefore, "Registry values, types or key existence did not recover.");
        }
    }

    private sealed class FakeRegistry : InstallationIntegration.IRegistry
    {
        private readonly Dictionary<string, Dictionary<string, InstallationIntegration.RegistryValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
        internal Action<string, string>? BeforeWrite;
        internal void CreateKey(string path) { if (!_keys.ContainsKey(path)) _keys[path] = new(StringComparer.OrdinalIgnoreCase); }
        public bool KeyExists(string path) => _keys.ContainsKey(path);
        public InstallationIntegration.RegistryValue? ReadValue(string path, string name) =>
            _keys.TryGetValue(path, out var values) && values.TryGetValue(name, out var value) ? value : null;
        public void WriteValue(string path, string name, InstallationIntegration.RegistryValue value)
        {
            BeforeWrite?.Invoke(path, name);
            CreateKey(path);
            _keys[path][name] = value;
        }
        public void DeleteValue(string path, string name) { if (_keys.TryGetValue(path, out var values)) values.Remove(name); }
        public void DeleteKeyIfEmpty(string path)
        {
            if (_keys.TryGetValue(path, out var values) && values.Count == 0 &&
                !_keys.Keys.Any(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))) _keys.Remove(path);
        }
        internal string Dump() => JsonSerializer.Serialize(_keys.OrderBy(key => key.Key).Select(key => new {
            key.Key, Values = key.Value.OrderBy(value => value.Key).Select(value => new { value.Key, value.Value.Kind, value.Value.Value }) }));
    }
}
