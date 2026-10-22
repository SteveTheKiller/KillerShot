using System.Diagnostics;
using System.IO;
using KillerFamily.Deployment;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>File-backed installation checks. No real process launch, registry, PATH or shortcut changes.</summary>
internal static class SelfTestInstallation
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-installation") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new installation test folder.");
            Directory.CreateDirectory(folder);
            ReplacementSurvivesUninstall(Path.Combine(folder, "replacement"), report, false);
            ReplacementSurvivesUninstall(Path.Combine(folder, "same-image"), report, true);
            PublicationBlocksUninstall(Path.Combine(folder, "publication-lock"), report);
            WorkerBlocksPublication(Path.Combine(folder, "worker-lock"), report);
            LeaseScopesAreIndependent(folder, report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "installation-report.txt"), report);
            App.Instance.Shutdown(code);
        }
        return true;
    }

    private static void ReplacementSurvivesUninstall(string folder, List<string> report, bool sameImage)
    {
        string targetDirectory = Path.Combine(folder, "installed");
        Directory.CreateDirectory(targetDirectory);
        string target = Path.Combine(targetDirectory, "KillerShot.exe");
        string source = Path.Combine(folder, "source.exe");
        File.WriteAllText(source, "original image");
        File.Copy(source, target);
        var processes = new FakeProcesses();
        var uninstall = new DeferredUninstall(processes);
        string operation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(folder, "staging");
        int started = uninstall.Start(source, staging, targetDirectory, ["KillerShot.exe"], false, operation);
        if (started != UninstallWorkflow.Pending) throw new InvalidOperationException("The synthetic uninstall did not start.");
        string expected = sameImage ? "original image" : "new installation";
        File.WriteAllText(source, expected);
        InstalledImagePublication.Publish(source, target, () => { });
        processes.Worker = true;
        bool metadataRemoved = false, integrationRemoved = false;
        string worker = Path.Combine(DeferredUninstall.OperationDirectory(staging, operation), DeferredUninstall.WorkerName);
        int result = uninstall.RunWorker(worker, staging, targetDirectory, ["KillerShot.exe"], false, operation,
            () => metadataRemoved = true, attempts: 1, retryMilliseconds: 0, removeIntegration: () => integrationRemoved = true);
        report.Add($"replacement result={result}, image remains={File.Exists(target)}, metadata removed={metadataRemoved}, integration removed={integrationRemoved}");
        if (result != InstallerExitCodes.Busy || !File.Exists(target) || File.ReadAllText(target) != expected
            || metadataRemoved || integrationRemoved)
            throw new InvalidOperationException("Deferred uninstall removed or changed a replacement installation.");
        report.Add((sameImage ? "same-byte" : "changed-image") + " replacement survives deferred uninstall PASS");
    }

    private static void PublicationBlocksUninstall(string folder, List<string> report)
    {
        string targetDirectory = Path.Combine(folder, "installed");
        Directory.CreateDirectory(targetDirectory);
        string source = Path.Combine(folder, "source.exe");
        string target = Path.Combine(targetDirectory, "KillerShot.exe");
        File.WriteAllText(source, "image");
        int uninstallResult = -1;
        InstalledImagePublication.Publish(source, target, () =>
        {
            // Another thread models another process while publication is registering its image.
            var contender = Task.Run(() => new DeferredUninstall(new FakeProcesses()).Start(source,
                Path.Combine(folder, "staging"), targetDirectory, ["KillerShot.exe"], false, Guid.NewGuid().ToString("N")));
            if (!contender.Wait(5000)) throw new TimeoutException("Uninstall waited while installation held its lease.");
            uninstallResult = contender.Result;
        });
        if (uninstallResult != InstallerExitCodes.Busy || File.ReadAllText(target) != "image")
            throw new InvalidOperationException($"Uninstall staging raced installation registration: {uninstallResult}.");
        report.Add("uninstall staging reports Busy during image publication and registration PASS");
    }

    private static void WorkerBlocksPublication(string folder, List<string> report)
    {
        string targetDirectory = Path.Combine(folder, "installed");
        Directory.CreateDirectory(targetDirectory);
        string source = Path.Combine(folder, "source.exe");
        string target = Path.Combine(targetDirectory, "KillerShot.exe");
        File.WriteAllText(source, "image");
        File.Copy(source, target);
        var processes = new FakeProcesses();
        var uninstall = new DeferredUninstall(processes);
        string operation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(folder, "staging");
        if (uninstall.Start(source, staging, targetDirectory, ["KillerShot.exe"], false, operation) != UninstallWorkflow.Pending)
            throw new InvalidOperationException("The synthetic worker did not start.");
        int CompetingInstall()
        {
            var contender = Task.Run(() =>
            {
                try { InstalledImagePublication.Publish(source, target, () => { }); return InstallerExitCodes.Success; }
                catch (Exception error) { return InstallerExitCodes.FromException(error); }
            });
            if (!contender.Wait(5000)) throw new TimeoutException("Installation waited while uninstall held its lease.");
            return contender.Result;
        }
        processes.Worker = true;
        int duringIntegration = -1, beforeMetadata = -1;
        bool waitedWithoutLease = false;
        processes.WaitForParent = () =>
        {
            var probe = Task.Run(() =>
            {
                using var lifecycle = InstallerExitCodes.Acquire("KillerShot", targetDirectory);
                return true;
            });
            if (!probe.Wait(5000)) throw new TimeoutException("Worker took its lease before waiting for its parent.");
            waitedWithoutLease = probe.Result;
            return true;
        };
        string worker = Path.Combine(DeferredUninstall.OperationDirectory(staging, operation), DeferredUninstall.WorkerName);
        int result = uninstall.RunWorker(worker, staging, targetDirectory, ["KillerShot.exe"], false, operation,
            () => beforeMetadata = CompetingInstall(), attempts: 1, retryMilliseconds: 0,
            removeIntegration: () => duringIntegration = CompetingInstall());
        report.Add($"worker result={result}, installer during integration={duringIntegration}, installer before metadata={beforeMetadata}");
        if (result != InstallerExitCodes.Success || duringIntegration != InstallerExitCodes.Busy
            || beforeMetadata != InstallerExitCodes.Busy || File.Exists(target) || !waitedWithoutLease)
            throw new InvalidOperationException("Installation raced the deferred worker's cleanup transaction.");
        report.Add("worker holds the lifecycle lease through integration, image and metadata removal PASS");
        report.Add("worker waits for its parent before acquiring the lifecycle lease PASS");
    }

    private static void LeaseScopesAreIndependent(string folder, List<string> report)
    {
        string first = Path.Combine(folder, "scope-one"), second = Path.Combine(folder, "scope-two");
        using var lifecycle = InstallerExitCodes.Acquire("KillerShot", first);
        var probe = Task.Run(() =>
        {
            int sameScope;
            try
            {
                using var same = InstallerExitCodes.Acquire("KillerShot", first.ToUpperInvariant() + Path.DirectorySeparatorChar + ".");
                sameScope = InstallerExitCodes.Success;
            }
            catch (Exception error) { sameScope = InstallerExitCodes.FromException(error); }
            using var distinct = InstallerExitCodes.Acquire("KillerShot", second);
            return sameScope;
        });
        if (!probe.Wait(5000) || probe.Result != InstallerExitCodes.Busy)
            throw new InvalidOperationException("Lifecycle leases did not normalize or separate target scopes.");
        report.Add("same target spellings share a lease; independent target directories do not block PASS");
    }

    private sealed class FakeProcesses : IInstallRelaunchProcesses
    {
        public bool Worker { get; set; }
        public Func<bool>? WaitForParent { get; set; }
        public IInstallRelaunchProcess Current() => new FakeProcess(Worker ? 2 : 1);
        public IInstallRelaunchProcess? Find(int id) => WaitForParent == null ? null : new FakeProcess(id, WaitForParent);
        public bool Start(ProcessStartInfo start) => true;
    }

    private sealed class FakeProcess(int id, Func<bool>? wait = null) : IInstallRelaunchProcess
    {
        public int Id => id;
        public long CreationFileTime => 123456;
        public bool WaitForExit(int milliseconds) => wait?.Invoke() ?? true;
        public void Dispose() { }
    }
}
