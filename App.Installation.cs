using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using KillerShot.Services;
using Microsoft.Win32;

namespace KillerShot
{
    public partial class App
    {
        private const string RegKey = @"Software\KillerShot";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerShot";
        private static readonly string AppName = "KillerShot";
        private static readonly string ExeName = "KillerShot.exe";
        private static readonly string[] InstalledFiles = { ExeName };

        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        private static readonly string InstallExe = Path.Combine(InstallDir, ExeName);
        // Machine-wide target, used by /silent with admin rights (winget, choco, RMM tools).
        private static readonly string MachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string MachineInstallExe = Path.Combine(MachineInstallDir, ExeName);
        private static readonly string StartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        private static readonly string StartMenuLnk = Path.Combine(StartMenuDir, AppName + ".lnk");
        private static readonly string DesktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        private static readonly string CommonDesktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");

        /// <summary>Runs /silent, /uninstall, /uninstall-silent or /remove-machine-conflict.
        /// False for any other first argument.</summary>
        private static bool TryRunInstallSwitch(string[] args, out int exitCode)
        {
            exitCode = 0;
            string first = args[0];
            if (string.Equals(first, "/silent", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = DoSilentInstall(args.Skip(1).Contains("/desktop", StringComparer.OrdinalIgnoreCase));
                return true;
            }
            bool uninstall = string.Equals(first, "/uninstall", StringComparison.OrdinalIgnoreCase);
            bool silent = string.Equals(first, "/uninstall-silent", StringComparison.OrdinalIgnoreCase);
            if (uninstall || silent)
            {
                if (!silent) InitializeKit();
                exitCode = args.Length <= 2 ? Uninstall(silent, args.Length == 2 ? args[1] : null)
                    : KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                return true;
            }
            if (string.Equals(first, "/remove-machine-conflict", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = RemoveMachineInstallConflict();
                return true;
            }
            return false;
        }
        // ============================================================
        // Install state
        // ============================================================

        /// <summary>True when this exe is running from somewhere other than an install location,
        /// i.e. it is the portable copy and could offer to install itself.</summary>
        internal static bool IsPortable()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
                return !string.Equals(currentExe, InstallExe, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(currentExe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when KillerShot is already installed machine-wide.</summary>
        internal static bool MachineInstallExists() => File.Exists(MachineInstallExe);

        private static bool ShowInstallDialog(string message, bool confirmation = false, string? heading = null)
        {
            // The badge's install runs off the interface thread; its dialogs still open on it.
            if (!Current.Dispatcher.CheckAccess())
                return Current.Dispatcher.Invoke(() => ShowInstallDialog(message, confirmation, heading));
            var dialog = new Controls.ConfirmDialog(heading ?? AppName, message,
                confirmation ? "Yes" : "OK", confirmation ? "No" : "");
            if (Current.MainWindow?.IsVisible == true)
                dialog.Owner = Current.MainWindow;
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
            }
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        internal static void OfferInstallConflictRepair()
        {
            if (!File.Exists(InstallExe) || !File.Exists(MachineInstallExe)) return;
            string current = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool runningMachine = string.Equals(current, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            bool runningUser = string.Equals(current, InstallExe, StringComparison.OrdinalIgnoreCase);
            if (!runningMachine && !runningUser) return;

            // Asked once: a decline is remembered, so the question does not open every start.
            if (Settings.GetBool("InstallConflictDeclined", false)) return;
            string other = runningMachine ? "per-user" : "all-users";
            if (!ShowInstallDialog($"KillerShot is installed twice. Remove the other {other} copy now?\n\nYour albums and settings will not be removed.",
                true, AppName + " installation conflict"))
            {
                Settings.SetBool("InstallConflictDeclined", true);
                return;
            }

            if (runningMachine) RemovePerUserInstall();
            else
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(current, "/remove-machine-conflict")
                    { UseShellExecute = true, Verb = "runas" });
                    p?.WaitForExit();
                }
                catch { }
            }
        }

        private static int RemoveMachineInstallConflict()
        {
            try
            {
                using var lifecycle = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName, MachineInstallDir);
                string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
                try { Registry.LocalMachine.DeleteSubKeyTree(RegKey, false); } catch { }
                try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
                try { File.Delete(Path.Combine(common, AppName + ".lnk")); } catch { }
                try { File.Delete(CommonDesktopLnk); } catch { }
                try { if (Directory.Exists(common)) Directory.Delete(common, false); } catch { }
                KillerFamily.Deployment.InstalledFileCleanup.Remove(MachineInstallDir, InstalledFiles);
                Services.CommandLinePath.Unregister(MachineInstallDir, machine: true);
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Installation conflict cleanup failed: " + error.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(error);
            }
        }

        /// <summary>
        /// Install, then relaunch from the installed location. An all-users install re-runs this
        /// exe elevated with /silent - the same machine-wide path winget and choco use - so UAC
        /// only appears when the user actually asked for it. False if elevation was declined.
        /// </summary>
        /// <summary>The family engine shape the kit's Shell/Install.cs names. The footer badge never
        /// takes this path: Shell/Install.Extras.cs runs the exit checks first and calls the
        /// overload below with the approval it captured.</summary>
        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers)
            => InstallAndRelaunch(wantDesktop, allUsers, Instance.CaptureExitApproval(), out _);

        /// <summary>Demo mode, under the name the kit's Shell/Install.cs reads.</summary>
        internal static bool IsDemo => Shell.DemoMode.IsDemo;

        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers, ExitApproval approval, out bool relaunchDeferred)
        {
            relaunchDeferred = false;
            if (allUsers)
            {
                if (!RunElevatedSilentInstall(wantDesktop)) return false;

                // Only ever one install: drop the per-user copy so there is a single Start Menu
                // entry and a single uninstall entry. Settings are deliberately left alone.
                RemovePerUserInstall();

                return Instance.TryCompleteRelaunch(approval,
                    () => new InstallRelaunch().StartInstalled(MachineInstallExe, ShutdownForRelaunch), out relaunchDeferred);
            }

            if (!DoInstall(wantDesktop) || !File.Exists(InstallExe)) return false;
            return Instance.TryCompleteRelaunch(approval,
                () => new InstallRelaunch().StartInstalled(InstallExe, ShutdownForRelaunch), out relaunchDeferred);
        }

        /// <summary>The installed copy starts once this process is gone; the exit checks already ran.</summary>
        private static void ShutdownForRelaunch() => Current.Dispatcher.Invoke(() =>
        {
            Instance.IsExiting = true;
            Current.Shutdown();
        });

        /// <summary>Re-run this exe elevated with /silent and wait for it to finish.</summary>
        private static bool RunElevatedSilentInstall(bool wantDesktop)
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName, wantDesktop ? "/silent /desktop" : "/silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",          // triggers the UAC prompt
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p is not null && p.ExitCode == 0 && File.Exists(MachineInstallExe);
            }
            catch
            {
                // Declining the UAC prompt throws Win32Exception 1223 (ERROR_CANCELLED).
                return false;
            }
        }

        /// <summary>Remove a per-user install: files, shortcuts and its HKCU install markers.
        /// The settings file in LOCALAPPDATA is deliberately left alone so theme, accent, locale
        /// and window placement survive the move to a machine-wide install.</summary>
        private static int RemovePerUserInstall()
        {
            try
            {
                using var lifecycle = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName, InstallDir);
                try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
                try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, false); } catch { }
                try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
                KillerFamily.Deployment.InstalledFileCleanup.Remove(InstallDir, InstalledFiles);
                Services.CommandLinePath.Unregister(InstallDir, machine: false);
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
                    key?.DeleteValue("Installed", throwOnMissingValue: false);
                    key?.DeleteValue("InstallPath", throwOnMissingValue: false);
                }
                catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
                catch { }
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Per-user installation cleanup failed: " + error.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(error);
            }
        }

        // ============================================================
        // Silent install - winget / choco / RMM and the Microsoft Store
        // ============================================================

        private static int DoSilentInstall(bool wantDesktop = false)
        {
            string? verified = null;
            try
            {
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                var (valid, _, _) = CodeSignature.GetSignerInfo();
                if (!valid)
                {
                    Console.Error.WriteLine("Silent install refused: EXE has no valid Authenticode signature.");
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                }

                // With admin rights (winget, choco, RMM) the install is machine-wide. Without them,
                // as the Microsoft Store runs installers, it is the normal per-user install with no
                // prompt. The app never needs admin rights.
                bool machine = KillerFamily.Deployment.InstallerExitCodes.IsElevated();
                string installDir = machine ? MachineInstallDir : InstallDir;
                using var installLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName, installDir);
                string installExe = machine ? MachineInstallExe : InstallExe;
                string startMenuDir = machine
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName)
                    : StartMenuDir;
                string startMenuLnk = Path.Combine(startMenuDir, AppName + ".lnk");
                RegistryKey hive = machine ? Registry.LocalMachine : Registry.CurrentUser;

                Directory.CreateDirectory(installDir);
                // The signature is checked again on a private copy inside the install folder, and that
                // copy is what gets published, so replacing the downloaded file after the first check
                // (it may sit in a folder other programs can write) cannot slip an unsigned one in.
                verified = Path.Combine(installDir, "." + Guid.NewGuid().ToString("N") + ".verify");
                File.Copy(src, verified);
                if (!CodeSignature.GetSignerInfo(verified).valid)
                {
                    Console.Error.WriteLine("Silent install refused: the copied EXE has no valid Authenticode signature.");
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                }
                if (File.Exists(installExe))
                {
                    try { File.SetAttributes(installExe, FileAttributes.Normal); } catch { }
                }
                KillerFamily.Deployment.InstalledImagePublication.Publish(verified, installExe, () =>
                    KillerFamily.Deployment.InstallationIntegration.Register(hive, RegKey, UninstallKey, machine,
                    installDir, startMenuDir, wantDesktop ? [startMenuLnk, machine ? CommonDesktopLnk : DesktopLnk] : [startMenuLnk], () =>
                {
                    Directory.CreateDirectory(startMenuDir);
                    CreateShortcut(startMenuLnk, installExe);
                    if (wantDesktop) CreateShortcut(machine ? CommonDesktopLnk : DesktopLnk, installExe);
                    WriteInstallKeys(hive, installDir, installExe);
                    Services.CommandLinePath.Register(installDir, machine);
                }));
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception ex)
            {
                // No UI on this path by definition, so the exit code is the only signal a
                // deployment tool gets.
                Console.Error.WriteLine("Silent install failed: " + ex.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
            }
            finally
            {
                if (verified != null) try { File.Delete(verified); } catch { }
            }
        }

        // ============================================================
        // Per-user install
        // ============================================================

        private static bool DoInstall(bool wantDesktop)
        {
            string src = Process.GetCurrentProcess().MainModule!.FileName;

            var (valid, _, _) = CodeSignature.GetSignerInfo();
            if (!valid)
            {
                ShowInstallDialog(
                    "Installation refused: the running EXE does not carry a valid Authenticode " +
                    "signature.\n\nOnly signed builds of KillerShot can be installed.");
                return false;
            }

            if (File.Exists(InstallExe))
            {
                string runningText = FileVersionInfo.GetVersionInfo(src).FileVersion ?? "";
                string installedText = FileVersionInfo.GetVersionInfo(InstallExe).FileVersion ?? "";
                if (Version.TryParse(runningText, out var runningVersion) &&
                    Version.TryParse(installedText, out var installedVersion) &&
                    runningVersion < installedVersion)
                {
                    var choice = ShowInstallDialog(
                        $"You are about to install an older version ({runningText}) over " +
                        $"the currently installed version ({installedText}).\n\nDowngrade anyway?",
                        true);
                    if (!choice) return false;
                }
            }

            try
            {
                using var installLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName, InstallDir);
                Directory.CreateDirectory(InstallDir);
                if (File.Exists(InstallExe))
                {
                    try { File.SetAttributes(InstallExe, FileAttributes.Normal); } catch { }
                }
                try
                {
                    KillerFamily.Deployment.InstalledImagePublication.Publish(src, InstallExe, () =>
                        KillerFamily.Deployment.InstallationIntegration.Register(Registry.CurrentUser, RegKey, UninstallKey, false,
                        InstallDir, StartMenuDir, wantDesktop ? [StartMenuLnk, DesktopLnk] : [StartMenuLnk], () =>
                    {
                        Directory.CreateDirectory(StartMenuDir);
                        CreateShortcut(StartMenuLnk, InstallExe);
                        if (wantDesktop) CreateShortcut(DesktopLnk, InstallExe);
                        WriteInstallKeys(Registry.CurrentUser, InstallDir, InstallExe);
                        Services.CommandLinePath.Register(InstallDir, machine: false);
                    }));
                }
                catch (Exception copyEx) when (copyEx is UnauthorizedAccessException or IOException)
                {
                    ShowInstallDialog(
                        "Couldn't write the installed copy at:\n" + InstallExe +
                        "\n\n" + copyEx.Message +
                        "\n\nClose any open KillerShot window, then run the installer again.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowInstallDialog("Installation failed:\n" + ex.Message);
                return false;
            }
        }

        /// <summary>Install markers plus the Add/Remove Programs entry. Same shape for both
        /// scopes; only the hive and the paths differ.</summary>
        private static void WriteInstallKeys(RegistryKey hive, string installDir, string installExe)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

            using (var key = hive.CreateSubKey(RegKey))
            {
                key.SetValue("Installed", 1);
                key.SetValue("InstallPath", installExe);
                key.SetValue("Version", version);
            }

            using (var key = hive.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Steve the Killer");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", installExe + ",0");
                key.SetValue("UninstallString", "\"" + installExe + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + installExe + "\" /uninstall-silent");
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
            }
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            // Reflection over IDispatch rather than `dynamic`, so this does not need the
            // Microsoft.CSharp runtime binder at run time.
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                object shell = Activator.CreateInstance(shellType)!;
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, [lnkPath])!;
                var sc = shortcut.GetType();
                sc.InvokeMember("TargetPath", BindingFlags.SetProperty,
                    null, shortcut, [targetPath]);
                sc.InvokeMember("WorkingDirectory", BindingFlags.SetProperty,
                    null, shortcut, [Path.GetDirectoryName(targetPath)!]);
                sc.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch { /* best-effort: a missing shortcut is not worth failing an install over */ }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        /// <summary>Machine-wide uninstall entries launch an asInvoker executable. If the
        /// Program Files copy is not already elevated, relaunch that same copy with UAC before
        /// showing the confirmation or touching HKLM/Program Files.</summary>
        private static int? RelaunchMachineUninstallElevatedIfNeeded(bool machine, bool silent, string operationId)
        {
            return UninstallWorkflow.RequestElevation(machine,
                KillerFamily.Deployment.InstallerExitCodes.IsElevated,
                () => Process.Start(new ProcessStartInfo(
                    Process.GetCurrentProcess().MainModule!.FileName,
                    (silent ? "/uninstall-silent " : "/uninstall ") + operationId)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                }), ex =>
                {
                    string message = "Uninstall could not request administrator access:\n" + ex.Message;
                    if (silent) Console.Error.WriteLine(message);
                    else ShowInstallDialog(message);
                });
        }

        private static string UninstallStagingRoot(bool machine) => machine ? MachineInstallDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

        private static int RunUninstallHelper(string[] args)
        {
            if (args.Length != 3 || (args[1] != "machine" && args[1] != "user") || !Guid.TryParseExact(args[2], "N", out _))
                return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
            bool machine = args[1] == "machine";
            if (machine && !KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                return KillerFamily.Deployment.InstallerExitCodes.AccessDenied;
            return new DeferredUninstall().RunWorker(Process.GetCurrentProcess().MainModule!.FileName,
                UninstallStagingRoot(machine), machine ? MachineInstallDir : InstallDir, InstalledFiles,
                machine, args[2], () => RemoveUninstallMetadata(machine),
                removeIntegration: () => RemoveUninstallIntegration(machine));
        }

        private static int Uninstall(bool silent, string? requestedOperationId)
        {
            string operationId = requestedOperationId ?? Guid.NewGuid().ToString("N");
            if (!Guid.TryParseExact(operationId, "N", out _)) return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
            bool machine = string.Equals(Process.GetCurrentProcess().MainModule?.FileName,
                                         MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            string operationDirectory = DeferredUninstall.OperationDirectory(UninstallStagingRoot(machine), operationId);
            int? elevatedResult = RelaunchMachineUninstallElevatedIfNeeded(machine, silent, operationId);
            if (elevatedResult.HasValue)
            {
                Console.Error.WriteLine("Uninstall result: " + elevatedResult.Value + ". Status and retained helper files: " + operationDirectory);
                return elevatedResult.Value;
            }

            // KillerShot often runs in the tray, and its exe cannot be removed while it does.
            if (OtherCopyRunning(machine ? MachineInstallExe : InstallExe))
            {
                const string running = "KillerShot is still running. Exit it from its tray icon, then uninstall again.";
                if (silent) Console.Error.WriteLine(running);
                else ShowInstallDialog(running);
                return KillerFamily.Deployment.InstallerExitCodes.Busy;
            }

            if (!silent)
            {
                var confirm = new Controls.ConfirmDialog(
                    "Uninstall KillerShot?",
                    "Your albums and captures will be kept.\n\nRemoval finishes after KillerShot closes. Status and retained helper files:\n" + operationDirectory,
                    "Uninstall",
                    "Cancel")
                { WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
                confirm.ShowDialog();
                if (!confirm.Confirmed) return KillerFamily.Deployment.InstallerExitCodes.Cancelled;
            }

            int result = new DeferredUninstall().Start(Process.GetCurrentProcess().MainModule!.FileName,
                UninstallStagingRoot(machine), machine ? MachineInstallDir : InstallDir, InstalledFiles, machine, operationId);
            Console.Error.WriteLine("Uninstall result: " + result + ". Status and retained helper files: " + operationDirectory);
            if (result != UninstallWorkflow.Pending && !silent)
                ShowInstallDialog("Uninstall could not start. Result: " + result + "\n\nRetained files and status, if created:\n" + operationDirectory);
            return result;
        }

        /// <summary>True when another KillerShot process runs from this exe. One that cannot be
        /// inspected, such as another account's, counts as not running.</summary>
        private static bool OtherCopyRunning(string exe)
        {
            int self = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
                using (process)
                {
                    if (process.Id == self) continue;
                    try { if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            return false;
        }

        /// <summary>Shortcuts, PATH and startup: removed while the program is still in
        /// place, so a failure here leaves an uninstall entry that still works.</summary>
        private static void RemoveUninstallIntegration(bool machine)
        {
            string startMenuDir = machine
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName)
                : StartMenuDir;
            string targetDir = machine ? MachineInstallDir : InstallDir;

            string shortcut = Path.Combine(startMenuDir, AppName + ".lnk");
            string desktop = machine ? CommonDesktopLnk : DesktopLnk;
            UninstallWorkflow.DeleteLiteral(shortcut);
            UninstallWorkflow.DeleteLiteral(desktop);
            if (!UninstallWorkflow.Absent(shortcut) || !UninstallWorkflow.Absent(desktop))
                throw new IOException("An installed shortcut remains.");
            if (Directory.Exists(startMenuDir) && Directory.GetFileSystemEntries(startMenuDir).Length == 0)
                Directory.Delete(startMenuDir, recursive: false);
            Services.CommandLinePath.Unregister(targetDir, machine);
            using (var environment = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(machine
                ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : "Environment"))
            {
                string value = environment?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                if (Services.CommandLinePath.WithoutEntry(value, targetDir) != value)
                    throw new IOException("The installed command path remains registered.");
            }
            if (!KillerFamily.Deployment.StartupRegistration.Set(Services.AppInfo.DisplayName, false))
                throw new UnauthorizedAccessException("The startup registration could not be removed.");
            using (var startup = Registry.CurrentUser.OpenSubKey(KillerFamily.Deployment.StartupRegistration.RunKeyPath))
                if (startup?.GetValue(Services.AppInfo.DisplayName) != null)
                    throw new IOException("The startup registration remains.");
        }

        private static void RemoveUninstallMetadata(bool machine)
        {
            var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;


            hive.DeleteSubKeyTree(RegKey, throwOnMissingSubKey: false);
            using (var remaining = hive.OpenSubKey(RegKey))
                if (remaining != null) throw new IOException("The install registration remains.");
            // Keep the uninstall entry available until every mandatory cleanup check succeeded.
            hive.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            using (var remaining = hive.OpenSubKey(UninstallKey))
                if (remaining != null) throw new IOException("The uninstall registration remains.");
        }

    }
}
