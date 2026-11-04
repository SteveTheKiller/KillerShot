using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestNovemberInstall
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-november-install") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new installation test folder.");
            Directory.CreateDirectory(folder);
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            ChecksumLineOrders();
            report.Add("release checksums are found in hash-first and name-first lines for the exact asset name PASS");
            report.Add(ReleaseImageIdentity(folder));
            StoreBuildDoesNotSelfUpdate();
            report.Add("a Store build reports updates through the Microsoft Store and never downloads the framework-dependent exe PASS");
            PerUserHelperUninstall(folder);
            report.Add("per-user uninstall removes the Print Screen helper before the worker, silent mode reports it as a leftover, and a helper relaunch is never rejected PASS");
            CompanionConfigurationRequired(folder);
            report.Add("configured companion requires complete launch arguments before publication is prepared PASS");
            ConfiguredCompanionLaunch(folder, fail: false);
            ConfiguredCompanionLaunch(folder, fail: true);
            report.Add("actual companion process receives configured arguments; failure report retains configured name and retry arguments PASS");
            PerUserLeaseBlocksPublication(folder);
            report.Add("per-user publication reports Busy without image or metadata changes under the installation lease PASS");
            PerUserTargetChangeCancelsPublication(folder, removed: false);
            PerUserTargetChangeCancelsPublication(folder, removed: true);
            report.Add("per-user publication preserves a replacement installation and does not recreate a removed target PASS");
            PerUserLeaseCoversMetadata(folder);
            report.Add("per-user publication keeps its lifecycle lease through image and metadata publication PASS");
            PublicationLeftovers(folder);
            report.Add("publication leftovers include install, previous and failed images PASS");
            StaleFailureReports(folder);
            report.Add("stale update failure reports swept; recent and unrelated reports preserved PASS");
            OversizedChecksumRejected(folder).GetAwaiter().GetResult();
            report.Add("oversized release checksum metadata rejected before image download PASS");
            InstallLeaseBlocksUpdate(folder);
            report.Add("machine update publication refuses while installation owns the target lease PASS");
            LockedConflictReportsFailure(folder);
            report.Add("locked conflict image removal reports failure and preserves the image PASS");
            ImpostorPublisherRejected();
            report.Add("unsigned development copy rejects a publisher containing the trusted name as a fragment PASS");
            DevelopmentSignerPin();
            report.Add("unsigned development update requires pinned signer; signed publisher renewal accepted PASS");
            RevocationPolicy();
            report.Add("revocation requested; revoked, bad digest and unknown failures rejected; offline network fallback verified PASS");
            DeferredRemovalIncludesLeftovers(folder);
            report.Add("deferred uninstall removes publication leftovers and preserves unrelated files PASS");
            InitiatingUserCleanup();
            report.Add("initiating user cleanup runs after accepted elevation and preserves canceled requests PASS");
            PublishedImageRefreshesCompanion(folder);
            report.Add("verified published update refreshes the installed companion before relaunch PASS");
            PublishedImageRefreshesCompanion(folder, fail: true);
            report.Add("companion refresh failure returns nonzero and keeps the verified main update with a retry report PASS");
            CompanionOwnership(folder);
            report.Add("uninstall preserves machine-owned companion and removes orphaned companion PASS");
            UpdateLeaseBlocksInstall(folder);
            report.Add("update publication mutex blocks a competing install for the same target PASS");
            LockedConflictCompanionPreservesMain(folder);
            report.Add("locked conflict companion keeps main executable and registration retryable PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "november-install-report.txt"), report);
            App.Instance.Shutdown(code);
        }
        return true;
    }

    private static void ChecksumLineOrders()
    {
        const string exeHash = "5b130931047701a403a2533f8773863f3e72724f1ae863e6c04b786c691d5e16";
        const string storeHash = "abcd0931047701a403a2533f8773863f3e72724f1ae863e6c04b786c691d5e16";
        string? hashFirst = UpdateService.ChecksumFor($"{exeHash}  KillerShot.exe\n", "KillerShot.exe");
        string? binaryMarker = UpdateService.ChecksumFor($"{exeHash} *KillerShot.exe\r\n", "KillerShot.exe");
        string? otherAsset = UpdateService.ChecksumFor($"{storeHash}  KillerShot-Store.exe\n", "KillerShot.exe");
        string? nameFirst = UpdateService.ChecksumFor($"KillerShot.exe  {exeHash}\n", "KillerShot.exe");
        string? store = UpdateService.ChecksumFor($"{exeHash}  KillerShot.exe\n{storeHash}  KillerShot-Store.exe\n", "KillerShot-Store.exe");
        if (hashFirst != exeHash || binaryMarker != exeHash || otherAsset != null || nameFirst != exeHash || store != storeHash)
            throw new InvalidOperationException($"Checksum lookup failed: hashFirst={hashFirst}, binary={binaryMarker}, other={otherAsset}, nameFirst={nameFirst}, store={store}");
    }

    /// <summary>The release image is a single-file bundle and a development build's exe is an
    /// apphost; neither carries a managed assembly header, so identity must come from the version resource.</summary>
    private static string ReleaseImageIdentity(string folder)
    {
        string running = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        string copy = Path.Combine(folder, "identity", "renamed-update.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(running, copy);
        RequireReleaseIdentity(copy);
        string bundle = Path.Combine(AppContext.BaseDirectory, "single-file", AppInfo.ExeName);
        if (!File.Exists(bundle))
            return "release image identity read from a renamed apphost; single-file bundle not staged beside the test, SKIPPED for bundle PASS";
        RequireReleaseIdentity(bundle);
        return "release image identity read from a renamed apphost and the published single-file bundle PASS";
    }

    private static void StoreBuildDoesNotSelfUpdate()
    {
        bool original = UpdateService.IsStoreBuild;
        try
        {
            UpdateService.IsStoreBuild = false;
            var regular = UpdateService.Target();
            if (regular.Unsupported || regular.Asset != AppInfo.ExeName)
                throw new InvalidOperationException($"A regular build stopped updating: unsupported={regular.Unsupported}, asset={regular.Asset}.");
            UpdateService.IsStoreBuild = true;
            var store = UpdateService.Target();
            if (!store.Unsupported)
                throw new InvalidOperationException("A Store build would download " + store.Asset + " over its self-contained copy.");
            if (App.Current.TryFindResource(UpdateService.StoreChannelMessageKey) is not string message || message.Length == 0)
                throw new InvalidOperationException("The Store update message is missing.");
            var host = new StoreAboutHost();
            new KillerShot.Features.About.AboutController(host).Show();
            if (host.UpdateText != message || host.UpdateVisible != true || host.UpdateEnabled != false)
                throw new InvalidOperationException($"The Store About card offered an update: text={host.UpdateText}, visible={host.UpdateVisible}, enabled={host.UpdateEnabled}.");
        }
        finally { UpdateService.IsStoreBuild = original; }
    }

    private static void PerUserHelperUninstall(string folder)
    {
        string root = Path.Combine(folder, "helper-uninstall");
        string userRoot = Path.Combine(root, "user"), machineRoot = Path.Combine(root, "machine");
        string worker = Path.Combine(DeferredUninstall.OperationDirectory(userRoot, Guid.NewGuid().ToString("N")), DeferredUninstall.WorkerName);
        if (DeferredUninstall.IsHelperRequest(["/remove-printscreen-helper"], worker, machineRoot, userRoot))
            throw new InvalidOperationException("A Print Screen helper relaunch from the uninstall worker is rejected before it can run.");

        string helper = Path.Combine(machineRoot, "KillerShot.PrintScreen.exe");
        Directory.CreateDirectory(machineRoot);
        File.WriteAllText(helper, "helper");
        string machineExe = Path.Combine(machineRoot, "KillerShot.exe");
        int elevations = 0;
        string? silentLeftover = App.RemoveHelperBeforeUserUninstall(silent: true, helper, machineExe, () => elevations++);
        if (silentLeftover != helper || elevations != 0)
            throw new InvalidOperationException($"Silent per-user uninstall asked for elevation or lost the helper: leftover={silentLeftover}, elevations={elevations}.");
        string? interactive = App.RemoveHelperBeforeUserUninstall(silent: false, helper, machineExe, () => { elevations++; File.Delete(helper); });
        if (interactive != null || elevations != 1 || File.Exists(helper))
            throw new InvalidOperationException($"Interactive per-user uninstall did not remove the helper: leftover={interactive}, elevations={elevations}.");
        File.WriteAllText(helper, "helper");
        string? declined = App.RemoveHelperBeforeUserUninstall(silent: false, helper, machineExe,
            () => throw new System.ComponentModel.Win32Exception(1223));
        if (declined != helper) throw new InvalidOperationException("A declined helper removal was not reported as a leftover.");

        string installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(installed);
        string source = Path.Combine(root, "synthetic-source.exe");
        File.WriteAllText(source, "synthetic image");
        File.Copy(source, Path.Combine(installed, "KillerShot.exe"));
        var processes = new UninstallProcesses();
        var uninstall = new DeferredUninstall(processes);
        string operation = Guid.NewGuid().ToString("N");
        int started = uninstall.Start(source, userRoot, installed, ["KillerShot.exe"], false, operation, leftovers: [helper]);
        if (started != UninstallWorkflow.Pending) throw new InvalidOperationException("Per-user deferred uninstall did not start: " + started);
        processes.Worker = true;
        string directory = DeferredUninstall.OperationDirectory(userRoot, operation);
        int result = uninstall.RunWorker(Path.Combine(directory, DeferredUninstall.WorkerName), userRoot, installed, ["KillerShot.exe"],
            false, operation, () => { }, attempts: 1, retryMilliseconds: 0, removeIntegration: () => { });
        var receipt = UninstallWorkflow.ReadReceipt(Path.Combine(directory, DeferredUninstall.ReceiptName));
        if (result != 0 || receipt.State != "Completed" || !receipt.Leftovers.Contains(helper))
            throw new InvalidOperationException($"Per-user uninstall receipt: result={result}, state={receipt.State}, leftovers={string.Join(",", receipt.Leftovers)}.");
    }

    private sealed class StoreAboutHost : KillerShot.Features.About.IAboutHost
    {
        public System.Windows.Window Window { get; } = new();
        public string Loc(string key) => App.Current.TryFindResource(key) as string ?? key;
        public void SetStatus(string text) { }
        public string Version { set { } }
        public string ReleaseDate { set { } }
        public string Publisher { set { } }
        public string Alias { set { } }
        public bool AliasVisible { set { } }
        public string Thumbprint { set { } }
        public string Sha256 { set { } }
        public string? UpdateText { get; private set; }
        string KillerShot.Features.About.IAboutHost.UpdateText { set => UpdateText = value; }
        public bool? UpdateVisible { get; private set; }
        bool KillerShot.Features.About.IAboutHost.UpdateVisible { set => UpdateVisible = value; }
        public bool? UpdateEnabled { get; private set; }
        bool KillerShot.Features.About.IAboutHost.UpdateEnabled { set => UpdateEnabled = value; }
        public bool StartupUpdateCheck { set { } }
        public void ShowCard() { }
    }

    private static void RequireReleaseIdentity(string exe)
    {
        System.Reflection.AssemblyName name;
        try { name = ReleaseIdentity.ReadIdentity(exe); }
        catch (System.Security.SecurityException error)
        { throw new InvalidOperationException("Release identity refused " + exe + ": " + error.Message, error); }
        var expected = typeof(App).Assembly.GetName().Version!;
        if (name.Name != AppInfo.DisplayName || name.Version?.ToString(3) != expected.ToString(3))
            throw new InvalidOperationException($"Release identity of {exe} was {name.Name} {name.Version}.");
    }

    private static void CompanionConfigurationRequired(string folder)
    {
        string root = Path.Combine(folder, "companion-configuration");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "app.exe"), download = Path.Combine(root, "download.exe");
        File.WriteAllText(target, "original application");
        File.WriteAllText(download, "downloaded update");
        UpdateImagePublication.RememberVerified(download,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download))), new string('A', 40));
        bool rejected = false;
        try
        {
            UpdateImagePublication.Script(download, target, false, "https://fixture.invalid/releases", "KillerShot",
                companion: Path.Combine(root, "companion.exe"));
        }
        catch (ArgumentException) { rejected = true; }
        if (!rejected || File.Exists(download + ".recovery.txt"))
            throw new InvalidOperationException("An incompletely configured companion was accepted or wrote publication side effects.");
    }

    private static void ConfiguredCompanionLaunch(string folder, bool fail)
    {
        string root = Path.Combine(folder, fail ? "configured-companion-failure" : "configured-companion-success");
        Directory.CreateDirectory(root);
        string download = Path.Combine(root, "download.exe"), target = Path.Combine(root, "app.exe");
        string companion = Path.Combine(root, "companion.exe");
        // A known Windows executable gives the process boundary a deterministic exit without installation.
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        File.Copy(source, download);
        File.WriteAllText(target, "original application");
        File.WriteAllText(companion, "companion present");
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download)));
        if (expected != sourceHash) throw new IOException("The companion process fixture copy did not match its source.");
        UpdateImagePublication.RememberVerified(download, expected, new string('A', 40));
        string arguments = fail ? "/d /c exit 42" : "/d /c exit 0";
        string script = UpdateImagePublication.Script(download, target, true, "https://fixture.invalid/releases", "KillerShot",
            overrides: "$ops.Publisher={param($path,$plan) $true};$ops.CaptureOriginal={param($plan)};"
                + "$ops.Metadata={param($plan)};$ops.Relaunch={param($plan)};$ops.Recover={param($plan)};$ops.ShowReport={param($path)};",
            companion: companion, companionArguments: arguments, companionName: "fixture launch helper");
        using var helper = Process.Start(UpdateService.HelperStart(script))
            ?? throw new IOException("Configured companion process fixture did not launch.");
        if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Configured companion process fixture did not finish."); }
        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
        if (actual != expected || (!fail && helper.ExitCode != 0) || (fail && helper.ExitCode == 0))
            throw new InvalidOperationException("Actual companion process did not honor its configured launch arguments.");
        if (fail)
        {
            string report = File.ReadAllText(download + ".recovery.txt");
            if (!report.Contains("fixture launch helper", StringComparison.Ordinal) || !report.Contains(arguments, StringComparison.Ordinal)
                || !report.Contains("returned 42", StringComparison.Ordinal))
                throw new InvalidOperationException("Companion failure did not preserve its configured name, process result and retry arguments.");
        }
    }

    private static void PerUserLeaseBlocksPublication(string folder)
    {
        string root = Path.Combine(folder, "per-user-publication");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "KillerShot.exe"), download = Path.Combine(root, "download.exe");
        string metadata = Path.Combine(root, "metadata.txt");
        File.WriteAllText(target, "original application");
        File.WriteAllText(download, "new application");
        UpdateImagePublication.RememberVerified(download,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download))), new string('A', 40));
        string encodedMetadata = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(metadata));
        string script = UpdateImagePublication.Script(download, target, false, "https://fixture.invalid/releases", "KillerShot",
            overrides: "$ops.Publisher={param($path,$plan) $true};$ops.CaptureOriginal={param($plan)};"
                + "$ops.Metadata={param($plan) [IO.File]::WriteAllText([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + encodedMetadata + "')),'registered')};"
                + "$ops.Relaunch={param($plan)};$ops.Recover={param($plan)};$ops.ShowReport={param($path)};");
        using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerShot", root);
        using var helper = Process.Start(UpdateService.HelperStart(script))
            ?? throw new IOException("Per-user publication fixture did not launch.");
        if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Per-user publication fixture did not finish."); }
        string current = File.ReadAllText(target);
        bool registered = File.Exists(metadata);
        string reportPath = download + ".recovery.txt";
        string report = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "(report removed)";
        if (helper.ExitCode != KillerFamily.Deployment.InstallerExitCodes.Busy || current != "original application" || registered
            || !File.Exists(download) || !report.Contains("Another KillerTools install or update is running", StringComparison.Ordinal))
            throw new InvalidOperationException($"Per-user update ignored lifecycle lease: exit={helper.ExitCode}, target={current}, metadata={registered}, report={report}");
    }

    private static void PerUserTargetChangeCancelsPublication(string folder, bool removed)
    {
        string root = Path.Combine(folder, removed ? "per-user-removed" : "per-user-replaced");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "KillerShot.exe"), download = Path.Combine(root, "download.exe");
        string metadata = Path.Combine(root, "metadata.txt");
        File.WriteAllText(target, "original application");
        File.WriteAllText(download, "downloaded update");
        UpdateImagePublication.RememberVerified(download,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download))), new string('A', 40));
        string encodedMetadata = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(metadata));
        string script = UpdateImagePublication.Script(download, target, false, "https://fixture.invalid/releases", "KillerShot",
            overrides: "$ops.Publisher={param($path,$plan) $true};$ops.CaptureOriginal={param($plan)};"
                + "$ops.Metadata={param($plan) [IO.File]::WriteAllText([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + encodedMetadata + "')),'registered')};"
                + "$ops.Relaunch={param($plan)};$ops.Recover={param($plan)};$ops.ShowReport={param($path)};");
        if (removed) File.Delete(target);
        else File.WriteAllText(target, "replacement installation");
        using var helper = Process.Start(UpdateService.HelperStart(script))
            ?? throw new IOException("Changed-target publication fixture did not launch.");
        if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Changed-target publication fixture did not finish."); }
        bool targetPreserved = removed ? !File.Exists(target) : File.ReadAllText(target) == "replacement installation";
        if (helper.ExitCode == 0 || !targetPreserved || File.Exists(metadata) || !File.Exists(download))
            throw new InvalidOperationException($"Per-user update did not cancel after its original target changed: removed={removed}, exit={helper.ExitCode}, targetPreserved={targetPreserved}, metadata={File.Exists(metadata)}.");
    }

    private static void PerUserLeaseCoversMetadata(string folder)
    {
        string root = Path.Combine(folder, "per-user-metadata-lease");
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "KillerShot.exe"), download = Path.Combine(root, "download.exe");
        string ready = Path.Combine(root, "metadata-ready.txt"), release = Path.Combine(root, "metadata-release.txt");
        File.WriteAllText(target, "original application");
        File.WriteAllText(download, "new application");
        UpdateImagePublication.RememberVerified(download,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download))), new string('A', 40));
        string readyData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ready));
        string releaseData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(release));
        string script = UpdateImagePublication.Script(download, target, false, "https://fixture.invalid/releases", "KillerShot",
            overrides: "$ops.Publisher={param($path,$plan) $true};$ops.CaptureOriginal={param($plan)};"
                + "$ops.Metadata={param($plan) $ready=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + readyData
                + "'));$release=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + releaseData
                + "'));[IO.File]::WriteAllText($ready,'metadata');$until=[DateTime]::UtcNow.AddSeconds(10);"
                + "while(-not [IO.File]::Exists($release) -and [DateTime]::UtcNow -lt $until){[Threading.Thread]::Sleep(20)};"
                + "if(-not [IO.File]::Exists($release)){throw 'Metadata fixture was not released.'}};"
                + "$ops.Relaunch={param($plan)};$ops.Recover={param($plan)};$ops.ShowReport={param($path)};");
        using var helper = Process.Start(UpdateService.HelperStart(script))
            ?? throw new IOException("Metadata lease publication fixture did not launch.");
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(ready) && !helper.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
            if (!File.Exists(ready)) throw new TimeoutException("The generated helper did not reach metadata publication.");
            bool busy = false;
            try { using var contender = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerShot", root); }
            catch (Exception error) { busy = KillerFamily.Deployment.InstallerExitCodes.FromException(error) == KillerFamily.Deployment.InstallerExitCodes.Busy; }
            if (!busy || File.ReadAllText(target) != "new application")
                throw new InvalidOperationException("Per-user lifecycle lease did not span image and metadata publication.");
        }
        finally
        {
            File.WriteAllText(release, "released");
            if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Metadata lease publication fixture did not finish."); }
        }
        if (helper.ExitCode != 0) throw new InvalidOperationException("Metadata lease publication did not finish successfully.");
    }

    private static void PublicationLeftovers(string folder)
    {
        string token = "0123456789abcdef0123456789abcdef";
        string[] expected = ["KillerShot.exe.install-" + token, "KillerShot.exe.previous-" + token,
            "KillerShot.exe.failed-" + token];
        foreach (string file in expected) File.WriteAllText(Path.Combine(folder, file), "leftover");
        File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "preserve");
        string[] observed = ExecutableDeployment.UninstallLeftovers(folder, "KillerShot.exe");
        if (!expected.Order().SequenceEqual(observed.Order()))
            throw new InvalidOperationException("Uninstall missed publication leftovers: " + string.Join(",", observed));
    }

    private static void StaleFailureReports(string folder)
    {
        string stale = Path.Combine(folder, "KillerShot_update_0123456789abcdef0123456789abcdef.exe.update-failure.txt");
        string recent = Path.Combine(folder, "KillerShot_update_1123456789abcdef0123456789abcdef.exe.update-failure.txt");
        string unrelated = Path.Combine(folder, "unrelated.exe.update-failure.txt");
        foreach (string path in new[] { stale, recent, unrelated }) File.WriteAllText(path, "failure");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddHours(-2));
        UpdateService.SweepStaleDownloads(folder, DateTime.UtcNow);
        if (File.Exists(stale) || !File.Exists(recent) || !File.Exists(unrelated))
            throw new InvalidOperationException("Stale update failure report sweep did not preserve its ownership and age boundary.");
    }

    private static async Task OversizedChecksumRejected(string folder)
    {
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        try
        {
            await UpdateService.DownloadVerifiedAsync(http, new Uri("https://fixture.invalid/image"),
                new Uri("https://fixture.invalid/sums"), "KillerShot.exe", false, "v9.0.0", folder,
                1024, TimeSpan.FromSeconds(2));
        }
        catch (HttpDownloadSizeException) { return; }
        throw new InvalidOperationException("Oversized release checksum metadata was accepted.");
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsolutePath != "/sums")
                throw new InvalidOperationException("An image download started after oversized metadata.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', 1024 * 1024 + 1)) });
        }
    }

    private static void InstallLeaseBlocksUpdate(string folder)
    {
        string target = Path.Combine(folder, "KillerShot.exe");
        string staged = Path.Combine(folder, ".update-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(target, "original");
        File.WriteAllText(staged, "new image");
        string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
        UpdateImagePublication.RememberVerified(staged,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(staged))), new string('A', 40));
        using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerShot", folder);
        var start = TrustedUpdate.Publication(staged, target, "9.0.0", originalHash,
            int.MaxValue, 1, int.MaxValue, 1, new string('A', 40));
        using var helper = Process.Start(start) ?? throw new IOException("Publication fixture did not launch.");
        if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Publication fixture did not finish."); }
        string report = File.ReadAllText(staged + ".recovery.txt");
        if (File.ReadAllText(target) != "original" || !report.Contains("Another KillerTools install or update is running", StringComparison.Ordinal))
            throw new InvalidOperationException("Update did not honor the installation target lease. " + report);
    }

    private static void LockedConflictReportsFailure(string folder)
    {
        string conflict = Path.Combine(folder, "conflict");
        Directory.CreateDirectory(conflict);
        string image = Path.Combine(conflict, "KillerShot.exe");
        File.WriteAllText(image, "locked installation");
        using var held = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        int result = 0;
        try { KillerFamily.Deployment.InstalledFileCleanup.Remove(conflict, "KillerShot.exe"); }
        catch (Exception error) { result = KillerFamily.Deployment.InstallerExitCodes.FromException(error); }
        if (result == 0 || !File.Exists(image))
            throw new InvalidOperationException("Conflict cleanup reported success while its executable remained locked.");
    }

    private static void ImpostorPublisherRejected()
    {
        try
        {
            ReleaseIdentity.VerifySigner((false, "(not signed)"), (true, "Unrelated Stephen Riley Company"), false);
        }
        catch (System.Security.SecurityException) { return; }
        throw new InvalidOperationException("Unsigned development copy accepted an unrelated publisher containing a trusted name fragment.");
    }

    private static void LockedConflictCompanionPreservesMain(string folder)
    {
        string root = Path.Combine(folder, "locked-conflict-companion");
        Directory.CreateDirectory(root);
        string image = Path.Combine(root, "KillerShot.exe"), companion = Path.Combine(root, "KillerShot.PrintScreen.exe");
        File.WriteAllText(image, "main image");
        File.WriteAllText(companion, "locked companion");
        using var held = new FileStream(companion, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool failed = false;
        try { KillerFamily.Deployment.InstalledFileCleanup.Remove(root, "KillerShot.exe", "KillerShot.PrintScreen.exe"); }
        catch (IOException) { failed = true; }
        if (!failed || !File.Exists(image))
            throw new InvalidOperationException("Locked companion removal discarded the executable needed to retry uninstall.");
    }

    private static void DevelopmentSignerPin()
    {
        bool rejected = false;
        try { ReleaseIdentity.VerifySigner((false, ""), (true, "Open Source Developer Stephen Riley"), false, new string('A', 40)); }
        catch (System.Security.SecurityException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("A development copy accepted an unpinned certificate.");
        ReleaseIdentity.VerifySigner((false, ""), (true, "Open Source Developer Stephen Riley"), false,
            "E478E4940DFD3547DAF2199494B399214FB3E0FD");
        ReleaseIdentity.VerifySigner((true, "Open Source Developer Stephen Riley"),
            (true, "Open Source Developer Stephen Riley"), true, new string('B', 40));
    }

    private static void RevocationPolicy()
    {
        bool checkedRevocation = false;
        bool valid = CodeSignature.VerifyTrust((revocation, flags) =>
        {
            checkedRevocation = revocation == 1 && flags == 0x80;
            return 0;
        });
        if (!valid || !checkedRevocation) throw new InvalidOperationException("Authenticode did not request chain revocation checks.");
        foreach (uint failure in new uint[] { 0x800B010C, 0x80096010, 0x80092012 })
        {
            int calls = 0;
            if (CodeSignature.VerifyTrust((_, _) => { calls++; return failure; }) || calls != 1)
                throw new InvalidOperationException("A non-network trust failure used an offline fallback.");
        }
        int offlineCalls = 0;
        if (!CodeSignature.VerifyTrust((revocation, flags) => ++offlineCalls == 1 ? 0x80092013u
            : revocation == 0 && flags == 0x1010 ? 0u : 1u) || offlineCalls != 2)
            throw new InvalidOperationException("Offline revocation did not require successful integrity and chain verification.");
    }

    private static void DeferredRemovalIncludesLeftovers(string folder)
    {
        string installed = Path.Combine(folder, "deferred");
        Directory.CreateDirectory(installed);
        string source = Path.Combine(folder, "synthetic-source.exe");
        File.WriteAllText(source, "synthetic image");
        File.Copy(source, Path.Combine(installed, "KillerShot.exe"));
        string leftover = "KillerShot.exe.previous-0123456789abcdef0123456789abcdef";
        File.WriteAllText(Path.Combine(installed, leftover), "old image");
        File.WriteAllText(Path.Combine(installed, "unrelated.txt"), "preserve");
        var processes = new UninstallProcesses();
        var uninstall = new DeferredUninstall(processes);
        string operation = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(folder, "uninstall-stage");
        int started = uninstall.Start(source, staging, installed, ["KillerShot.exe"], false, operation);
        if (started != UninstallWorkflow.Pending) throw new InvalidOperationException("Synthetic deferred uninstall did not start: " + started);
        processes.Worker = true;
        bool removedMetadata = false;
        int result = uninstall.RunWorker(Path.Combine(DeferredUninstall.OperationDirectory(staging, operation), DeferredUninstall.WorkerName),
            staging, installed, ["KillerShot.exe"], false, operation, () => removedMetadata = true, attempts: 1, retryMilliseconds: 0);
        if (result != 0 || !removedMetadata || File.Exists(Path.Combine(installed, "KillerShot.exe"))
            || File.Exists(Path.Combine(installed, leftover)) || !File.Exists(Path.Combine(installed, "unrelated.txt")))
            throw new InvalidOperationException("Deferred uninstall failed to remove its publication leftovers: " + result);
    }

    private sealed class UninstallProcesses : IInstallRelaunchProcesses
    {
        internal bool Worker;
        public IInstallRelaunchProcess Current() => new UninstallProcess(Worker ? 2 : 1);
        public IInstallRelaunchProcess? Find(int id) => null;
        public bool Start(ProcessStartInfo start) => true;
    }

    private static void InitiatingUserCleanup()
    {
        bool cleaned = false;
        int? result = UninstallWorkflow.RequestElevation(true, () => false,
            () => Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /c exit 997")
                { UseShellExecute = false, CreateNoWindow = true }), _ => { }, 5000, () => cleaned = true);
        if (result != UninstallWorkflow.Pending || !cleaned)
            throw new InvalidOperationException("Accepted elevated uninstall did not clean the initiating user context.");
        cleaned = false;
        result = UninstallWorkflow.RequestElevation(true, () => false,
            () => throw new System.ComponentModel.Win32Exception(1223), _ => { }, 5000, () => cleaned = true);
        if (result != KillerFamily.Deployment.InstallerExitCodes.Cancelled || cleaned)
            throw new InvalidOperationException("Canceled elevation changed initiating user state.");
    }

    private sealed class UninstallProcess(int id) : IInstallRelaunchProcess
    {
        public int Id => id;
        public long CreationFileTime => 123456;
        public bool WaitForExit(int milliseconds) => true;
        public void Dispose() { }
    }

    private static void PublishedImageRefreshesCompanion(string folder, bool fail = false)
    {
        string root = Path.Combine(folder, fail ? "companion-failure" : "companion-update");
        Directory.CreateDirectory(root);
        string download = Path.Combine(root, "download.exe"), target = Path.Combine(root, "KillerShot.exe");
        string companion = Path.Combine(root, "KillerShot.PrintScreen.exe");
        File.WriteAllText(download, "new application");
        File.WriteAllText(target, "old application");
        File.WriteAllText(companion, "old companion");
        UpdateImagePublication.RememberVerified(download,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download))), new string('A', 40));
        string script = UpdateImagePublication.Script(download, target, false, "https://fixture.invalid/releases", "KillerShot",
            overrides: "$ops.Publisher={param($path,$plan) $true};$ops.CaptureOriginal={param($plan)};"
                + "$ops.Metadata={param($plan)};$ops.Relaunch={param($plan)};$ops.Recover={param($plan)};"
                + (fail ? "$ops.RefreshCompanion={param($plan) throw 'synthetic companion access failure'};"
                    : "$ops.RefreshCompanion={param($plan) [IO.File]::WriteAllText($plan.Companion,'new companion')};"),
            companion: companion, companionArguments: "/refresh-test-companion", companionName: "fixture companion");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ImmutablePowerShell.Arguments(script)) { UseShellExecute = false, CreateNoWindow = true };
        using var helper = Process.Start(start) ?? throw new IOException("Companion update fixture did not start.");
        if (!helper.WaitForExit(20000)) { helper.Kill(); throw new TimeoutException("Companion update fixture did not finish."); }
        if (fail)
        {
            if (helper.ExitCode == 0 || File.ReadAllText(target) != "new application" || File.ReadAllText(companion) != "old companion"
                || !File.ReadAllText(download + ".recovery.txt").Contains("/refresh-test-companion", StringComparison.Ordinal)
                || !File.ReadAllText(download + ".recovery.txt").Contains("fixture companion", StringComparison.Ordinal))
                throw new InvalidOperationException("Companion refresh failure lost the main update or its visible retry report.");
            return;
        }
        if (helper.ExitCode != 0 || File.ReadAllText(target) != "new application" || File.ReadAllText(companion) != "new companion")
            throw new InvalidOperationException("Published update did not refresh its installed companion.");
    }

    private static void CompanionOwnership(string folder)
    {
        string root = Path.Combine(folder, "companion-owner");
        Directory.CreateDirectory(root);
        string machine = Path.Combine(root, "KillerShot.exe"), companion = Path.Combine(root, "KillerShot.PrintScreen.exe");
        File.WriteAllText(machine, "machine installation");
        File.WriteAllText(companion, "helper");
        ExecutableDeployment.RemoveCompanionIfUnowned(machine, companion);
        if (!File.Exists(companion)) throw new InvalidOperationException("Per-user uninstall removed the machine installation's helper.");
        File.Delete(machine);
        ExecutableDeployment.RemoveCompanionIfUnowned(machine, companion);
        if (File.Exists(companion)) throw new InvalidOperationException("Per-user uninstall left its orphaned helper.");
        File.WriteAllText(companion, "locked helper");
        using var held = new FileStream(companion, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool metadataRemoved = false;
        int result = UninstallWorkflow.CompleteDeferred(root, ["KillerShot.exe"], () => metadataRemoved = true,
            attempts: 1, retryMilliseconds: 0,
            removeIntegration: () => ExecutableDeployment.RemoveCompanionIfUnowned(machine, companion));
        if (result == 0 || metadataRemoved || !File.Exists(companion))
            throw new InvalidOperationException("Companion cleanup failure did not preserve uninstall registration for retry.");
    }

    private static void UpdateLeaseBlocksInstall(string folder)
    {
        using var mutex = new Mutex(false, KillerFamily.Deployment.InstallerExitCodes.MutexName("KillerShot", folder));
        if (!mutex.WaitOne(0)) throw new InvalidOperationException("Synthetic update lease was already held.");
        try
        {
            var probe = Task.Run(() =>
            {
                try { using var install = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerShot", folder); return 0; }
                catch (Exception error) { return KillerFamily.Deployment.InstallerExitCodes.FromException(error); }
            });
            if (!probe.Wait(5000) || probe.Result != KillerFamily.Deployment.InstallerExitCodes.Busy)
                throw new InvalidOperationException("A competing install ignored the update publication mutex.");
        }
        finally { mutex.ReleaseMutex(); }
    }
}
