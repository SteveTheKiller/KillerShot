using System.IO;
using System.Diagnostics.Tracing;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesSafety
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--selftest-files-safety") return false;
        string rejected = Path.Combine(Path.GetTempPath(), "KillerShot-FilesSafety-Rejected-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Settings.UseFolder(Path.Combine(rejected, "settings"));
        VideoCaptures.UsePlaybackFolder(Path.Combine(rejected, "playback"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(rejected, "native"));
        _ = Run(args.Length is >= 2 and <= 3 ? args[1] : "", args.Length == 3 ? args[2] : "all", rejected);
        return true;
    }

    private static async Task Run(string folder, string scenario, string rejected)
    {
        int checks = 0, exit = 1;
        bool created = false;
        var report = new StringBuilder();
        void Check(bool passed, string message)
        {
            checks++;
            report.AppendLine((passed ? "PASS: " : "FAIL: ") + message);
            if (!passed) throw new InvalidOperationException(message);
        }
        try
        {
            folder = Path.GetFullPath(folder);
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Use a fresh safety fixture.");
            Directory.CreateDirectory(folder);
            created = true;
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (scenario == "all")
            {
                async Task Case(string name, Func<string, Task> run)
                {
                    report.AppendLine("SCENARIO START: " + name);
                    string owned = Path.Combine(folder, name);
                    if (Directory.Exists(owned) || File.Exists(owned)) throw new IOException("Use a fresh scenario fixture: " + name);
                    Directory.CreateDirectory(owned);
                    await run(owned);
                    report.AppendLine("SCENARIO PASS: " + name);
                }
                await Case("containment", path => Containment(path, Check));
                await Case("same-folder-paste", path => SameFolderPaste(path, Check));
                await Case("folder-filter", path => FolderFilter(path, Check));
                await Case("readonly-cross-volume", path => ReadOnlyCrossVolumeMove(path, Check));
                await Case("cache", path => CacheRetention(path, Check, report));
                await Case("size", path => RequestedSize(path, Check));
                await Case("orientation", path => Orientations(path, Check, report));
                await Case("columns-focus", _ => ColumnFocus(Check, report));
                await Case("columns-menu", _ => ColumnMenu(Check, report));
                await Case("address-watcher", path => AddressWatcher(path, Check, report));
                await Case("cache-concurrency", path => CacheConcurrency(path, Check, report));
                await Case("address-overflow", path => AddressOverflow(path, Check, report));
                await Case("cache-workload-minimal", path => CacheWorkloadMinimal(path, Check, report));
                string[] expectedCases = ["containment", "same-folder-paste", "folder-filter", "readonly-cross-volume", "cache", "size", "orientation", "columns-focus", "columns-menu", "address-watcher", "cache-concurrency", "address-overflow", "cache-workload-minimal"];
                Check(checks >= 108 && expectedCases.All(name => report.ToString().Contains("SCENARIO PASS: " + name, StringComparison.Ordinal)),
                    "all thirteen isolated safety scenarios complete with every repair regression selected");
            }
            else if (scenario == "containment") await Containment(folder, Check);
            else if (scenario == "same-folder-paste") await SameFolderPaste(folder, Check);
            else if (scenario == "folder-filter") await FolderFilter(folder, Check);
            else if (scenario == "readonly-cross-volume") await ReadOnlyCrossVolumeMove(folder, Check);
            else if (scenario == "cache") await CacheRetention(folder, Check, report);
            else if (scenario == "size") await RequestedSize(folder, Check);
            else if (scenario == "orientation") await Orientations(folder, Check, report);
            else if (scenario == "columns-menu") await ColumnMenu(Check, report);
            else if (scenario == "columns-focus") await ColumnFocus(Check, report);
            else if (scenario == "address-watcher") await AddressWatcher(folder, Check, report);
            else if (scenario == "cache-concurrency") await CacheConcurrency(folder, Check, report);
            else if (scenario == "address-overflow") await AddressOverflow(folder, Check, report);
            else if (scenario == "cache-workload") await CacheWorkload(folder, Check, report);
            else if (scenario == "cache-workload-minimal") await CacheWorkloadMinimal(folder, Check, report);
            else throw new ArgumentException("Unknown safety scenario: " + scenario);
            exit = 0;
        }
        catch (Exception error)
        {
            if (!created)
            {
                checks++;
                report.AppendLine("Fixture admission failed; windows=" + Application.Current.Windows.Count);
                report.AppendLine("Protected playback=" + VideoCaptures.PlaybackFolder);
            }
            report.AppendLine(error.ToString());
        }
        finally
        {
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            string reportFolder = created ? folder : rejected;
            Directory.CreateDirectory(reportFolder);
            File.WriteAllText(Path.Combine(reportFolder, "files-safety-report.txt"), report.ToString());
            Application.Current.Shutdown(exit);
        }
    }

    private static async Task CacheWorkloadMinimal(string folder, Action<bool, string> check, StringBuilder report)
    {
        report.AppendLine("One wave of sixteen public requests. No TPL observer is instantiated.");
        string warmPath = Path.Combine(folder, "warm.png"), heldPath = Path.Combine(folder, "held.png");
        WriteImage(warmPath);
        WriteImage(heldPath, 2048, 1024);
        var warm = await IconCache.ForAsync(warmPath, 64);
        check(warm is BitmapSource { PixelWidth: 64, PixelHeight: 32 }, "minimal workload warms a real unrelated thumbnail");
        using var gate = new FileOplock(heldPath);
        using var stop = new CancellationTokenSource();
        var failsafe = new Thread(() => { if (!stop.Token.WaitHandle.WaitOne(7000)) gate.Dispose(); }) { IsBackground = true };
        failsafe.Start();
        Task<ImageSource?>[] pending = [];
        try
        {
            pending = Enumerable.Range(0, 16).Select(_ => IconCache.ForAsync(heldPath, 256)).ToArray();
            await Until(() => gate.Broken.WaitOne(0));
            check(gate.Broken.WaitOne(0) && !gate.Released, "minimal workload reaches actual held image-open boundary");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var unrelated = await IconCache.ForAsync(warmPath, 64);
            double latency = clock.Elapsed.TotalMilliseconds;
            bool held = !gate.Released;
            var completed = await Task.WhenAll(pending);
            report.AppendLine($"Unrelated latencyMs={latency:F1}, sameCachedObject={ReferenceEquals(warm, unrelated)}, heldAtCompletion={held}, publicTimeouts={completed.Count(image => image == null)}/16");
            check(held && ReferenceEquals(warm, unrelated) && latency < 500,
                "minimal observer-free unrelated cache request stays within 500ms while filesystem control remains held");
        }
        finally
        {
            gate.Dispose();
            stop.Cancel();
            failsafe.Join();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
        }
        var after = await IconCache.ForAsync(heldPath, 256);
        check(after is BitmapSource { PixelWidth: 256, PixelHeight: 128 }, "released held image becomes available through public API");
    }

    private static async Task CacheWorkload(string folder, Action<bool, string> check, StringBuilder report)
    {
        string cachedPath = Path.Combine(folder, "warm.png"), blockedPath = Path.Combine(folder, "held.png");
        WriteImage(cachedPath);
        WriteImage(blockedPath, 2048, 1024);
        var cached = await IconCache.ForAsync(cachedPath, 64);
        check(cached is BitmapSource { PixelWidth: 64, PixelHeight: 32 }, "workload warms an actual unrelated public thumbnail");
        using var observer = new WorkerTaskObserver();
        check(observer.Available, "runtime task diagnostics are available without private decoder instrumentation");
        CollectImages();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long initialPrivate = process.PrivateMemorySize64, initialWorking = process.WorkingSet64;
        long peakPrivate = initialPrivate, peakWorking = initialWorking;
        int peakThreads = process.Threads.Count, samples = 0;
        using var stop = new CancellationTokenSource();
        using var gate = new FileOplock(blockedPath);
        var sampler = new Thread(() =>
        {
            using var sampled = System.Diagnostics.Process.GetCurrentProcess();
            while (!stop.Token.WaitHandle.WaitOne(25))
            {
                sampled.Refresh();
                peakPrivate = Math.Max(peakPrivate, sampled.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, sampled.WorkingSet64);
                peakThreads = Math.Max(peakThreads, sampled.Threads.Count);
                samples++;
            }
        }) { IsBackground = true };
        var failsafe = new Thread(() => { if (!stop.Token.WaitHandle.WaitOne(6500)) gate.Dispose(); }) { IsBackground = true };
        sampler.Start();
        failsafe.Start();
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var waits = new List<(double Ms, bool Null)>();
        var unrelated = new List<(double Ms, bool Same)>();
        var beforeRelease = observer.Snapshot;
        double holdMs = 0, drainMs = 0;
        bool releasedEarly = false;
        try
        {
            for (int wave = 0; wave < 2; wave++)
            {
                async Task<(double Ms, bool Null)> Request()
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var image = await observer.Capture(() => IconCache.ForAsync(blockedPath, 256));
                    return (clock.Elapsed.TotalMilliseconds, image == null);
                }
                var pending = Enumerable.Range(0, 16).Select(_ => Request()).ToArray();
                if (wave == 0)
                {
                    await Until(() => gate.Broken.WaitOne(0));
                    check(gate.Broken.WaitOne(0) && !gate.Released, "sixteen-call workload reaches actual held filesystem-open boundary");
                }
                var cachedClock = System.Diagnostics.Stopwatch.StartNew();
                var hit = await IconCache.ForAsync(cachedPath, 64);
                unrelated.Add((cachedClock.Elapsed.TotalMilliseconds, ReferenceEquals(cached, hit)));
                waits.AddRange(await Task.WhenAll(pending));
                var snapshot = observer.Snapshot;
                report.AppendLine($"Wave {wave}: publicRequests=16, nulls={waits.Skip(wave * 16).Count(item => item.Null)}, cachedMs={unrelated[^1].Ms:F1}, cachedSame={unrelated[^1].Same}, runtimeTasks={snapshot}");
            }
            holdMs = elapsed.Elapsed.TotalMilliseconds;
            releasedEarly = gate.Released;
            beforeRelease = observer.Snapshot;
        }
        finally
        {
            gate.Dispose();
            var drain = System.Diagnostics.Stopwatch.StartNew();
            while (observer.Snapshot.Completed < observer.Snapshot.Scheduled && drain.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
            drainMs = drain.Elapsed.TotalMilliseconds;
            stop.Cancel();
            sampler.Join();
            failsafe.Join();
            process.Refresh();
            var completed = observer.Snapshot;
            report.AppendLine($"Workload holdMs={holdMs:F1}, totalMs={elapsed.Elapsed.TotalMilliseconds:F1}, releasedEarly={releasedEarly}, drainAfterReleaseMs={drainMs:F1}");
            report.AppendLine($"Runtime worker tasks beforeRelease={beforeRelease}, afterRelease={completed}. These are observed scheduled worker tasks, not decoder-entry counts.");
            report.AppendLine($"CPUms={process.TotalProcessorTime.TotalMilliseconds - cpuBefore:F1}, processors={Environment.ProcessorCount}, managedAllocatedBytes={GC.GetTotalAllocatedBytes(true) - allocatedBefore}, privateBaseline={initialPrivate}, privatePeak={peakPrivate}, workingBaseline={initialWorking}, workingPeak={peakWorking}, peakProcessThreads={peakThreads}, samples={samples}");
            report.AppendLine("Public request latenciesMs=" + string.Join(",", waits.Select(item => item.Ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
        }
        var final = observer.Snapshot;
        check(!releasedEarly && holdMs > 2000 && waits.Count == 32 && waits.Count(item => item.Null) >= 16,
            "two waves of sixteen real public requests include timeouts while the owned file remains held beyond two seconds");
        check(final.Scheduled > 0 && final.Started == final.Scheduled && final.Completed == final.Scheduled,
            "all observed worker tasks finish after controlled filesystem release");
        check(unrelated.All(item => item.Same && item.Ms < 500), "warmed unrelated cached requests remain within the existing 500ms responsiveness budget");
    }

    private sealed class WorkerTaskObserver : EventListener
    {
        private readonly object _gate = new();
        private readonly HashSet<int> _scheduled = [], _started = [], _completed = [];
        private int _captureThread;
        public bool Available { get; }
        public WorkerTaskObserver()
        {
            var provider = EventSource.GetSources().SingleOrDefault(source => source.Name == "System.Threading.Tasks.TplEventSource");
            Available = provider != null;
            if (provider != null) EnableEvents(provider, EventLevel.Informational, (EventKeywords)0x43);
        }
        public Task<ImageSource?> Capture(Func<Task<ImageSource?>> request)
        {
            Volatile.Write(ref _captureThread, Environment.CurrentManagedThreadId);
            try { return request(); }
            finally { Volatile.Write(ref _captureThread, 0); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventSource.Name != "System.Threading.Tasks.TplEventSource" || data.Payload is not { Count: >= 3 } payload || payload[2] is not int id) return;
            lock (_gate)
            {
                if (data.EventId == 7 && Volatile.Read(ref _captureThread) == Environment.CurrentManagedThreadId) _scheduled.Add(id);
                if (!_scheduled.Contains(id)) return;
                if (data.EventId == 8) _started.Add(id);
                if (data.EventId == 9) _completed.Add(id);
            }
        }
        public (int Scheduled, int Started, int Completed) Snapshot
        {
            get { lock (_gate) return (_scheduled.Count, _started.Count, _completed.Count); }
        }
    }

    private static async Task AddressOverflow(string folder, Action<bool, string> check, StringBuilder report)
    {
        string current = Directory.CreateDirectory(Path.Combine(folder, "retained")).FullName;
        string next = Directory.CreateDirectory(Path.Combine(folder, "next")).FullName;
        File.WriteAllText(Path.Combine(current, "activate.txt"), "owned");
        var probes = Enumerable.Range(0, 3).Select(index => new FileExistsGate(Path.Combine(folder, $"sentinel-{index}.txt"))).ToArray();
        foreach (var probe in probes) File.WriteAllText(probe.Path, "owned File.Exists boundary");
        using var browser = new Browser(null, path => probes.FirstOrDefault(probe => probe.Path == path) is { } probe ? probe.Read() : File.Exists(path));
        int hostCalls = 0;
        browser.OpenInEditor = _ => { hostCalls++; return Task.FromResult(true); };
        check(await browser.NavigateTo(current), "overflow fixture opens actual retained folder");
        async Task Enter(FileExistsGate probe, Task<bool> pending)
        {
            await Task.WhenAny(probe.Entered.Task, Task.Delay(2000));
            check(probe.Entered.Task.IsCompleted && !pending.IsCompleted, "actual File.Exists boundary entered and user address remains pending: " + Path.GetFileName(probe.Path));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pending = browser.NavigateAddress(probes[0].Path);
        try
        {
            await Enter(probes[0], pending);
            bool baseline = await pending;
            report.AppendLine($"Controlled File.Exists deadline: result={baseline}, elapsedMs={clock.ElapsedMilliseconds}");
            check(!baseline && clock.Elapsed >= TimeSpan.FromMilliseconds(4500) && clock.Elapsed < TimeSpan.FromSeconds(6),
                "controlled filesystem boundary reaches original five-second address deadline");
        }
        finally { await probes[0].ReleaseAndDrain(); }
        clock.Restart();
        pending = browser.NavigateAddress(probes[1].Path);
        try
        {
            await Enter(probes[1], pending);
            await Task.Run(() =>
            {
                for (int index = 0; index < 250; index++) File.WriteAllText(Path.Combine(current, $"burst-{index}.txt"), "actual event");
            });
            bool result = await pending;
            report.AppendLine($"250-file watcher maintenance: result={result}, elapsedMs={clock.ElapsedMilliseconds}");
            check(!result && clock.Elapsed >= TimeSpan.FromMilliseconds(4500) && clock.Elapsed < TimeSpan.FromSeconds(6),
                "250 actual watcher events do not cancel user address resolution before its deadline");
        }
        finally { await probes[1].ReleaseAndDrain(); }
        await Until(() => browser.State.Results.Count == 251 && !browser.State.IsLoading);
        check(browser.State.Results.Count == 251, "deferred watcher relist eventually reconciles all 250 actual creations");
        check(hostCalls == 0, "late timed-out filesystem probes do not activate the host");
        pending = browser.NavigateAddress(probes[2].Path);
        try
        {
            await Enter(probes[2], pending);
            await Task.Run(() =>
            {
                for (int index = 0; index < 250; index++) File.WriteAllText(Path.Combine(current, $"later-{index}.txt"), "later event");
            });
            check(await browser.NavigateTo(next), "later explicit navigation reaches independent folder during address resolution");
            bool superseded = await pending;
            await probes[2].ReleaseAndDrain();
            await Task.Delay(700);
            check(!superseded && browser.State.CurrentFolder == next && browser.State.Results.Count == 0 && hostCalls == 0,
                "later user navigation stays authoritative over old-folder watcher maintenance and late file activation");
        }
        finally { await probes[2].ReleaseAndDrain(); }
    }

    private sealed class FileExistsGate(string path)
    {
        public string Path { get; } = path;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Read()
        {
            Entered.TrySetResult();
            try
            {
                if (!Release.Task.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Owned File.Exists boundary failsafe elapsed.");
                return File.Exists(Path);
            }
            finally { Exited.TrySetResult(); }
        }
        public async Task ReleaseAndDrain()
        {
            Release.TrySetResult();
            if (Entered.Task.IsCompleted) await Exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task CacheConcurrency(string folder, Action<bool, string> check, StringBuilder report)
    {
        string cachedPath = Path.Combine(folder, "cached.png");
        string blockedPath = Path.Combine(folder, "blocked.png");
        WriteImage(cachedPath);
        WriteImage(blockedPath);
        var cached = await IconCache.ForAsync(cachedPath, 64);
        check(cached is BitmapSource { PixelWidth: 64, PixelHeight: 32 }, "unrelated actual image is cached through public ForAsync");
        using var gate = new FileOplock(blockedPath);
        Task<ImageSource?> pending = Task.FromResult<ImageSource?>(null);
        try
        {
            var metadata = Task.Run(() => File.GetLastWriteTimeUtc(blockedPath));
            check(await Task.WhenAny(metadata, Task.Delay(500)) == metadata, "controlled oplock leaves real timestamp metadata readable before decode");
            pending = IconCache.ForAsync(blockedPath, 64);
            bool broken = await Task.Run(() => gate.Broken.WaitOne(1500));
            check(broken && !pending.IsCompleted && !gate.Released, "actual decoder file open reaches and waits at owned oplock boundary");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var unrelated = await IconCache.ForAsync(cachedPath, 64);
            report.AppendLine($"Unrelated cached ForAsync durationMs={clock.ElapsedMilliseconds}, null={unrelated == null}, blockedReleased={gate.Released}");
            check(ReferenceEquals(cached, unrelated) && clock.Elapsed < TimeSpan.FromMilliseconds(500) && !gate.Released,
                "unrelated cached image remains promptly available while actual decoder open is blocked");
        }
        finally
        {
            gate.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
        }
        check(gate.Released, "owned oplock releases deterministically after blocked decoder case");
        string duplicates = Path.Combine(folder, "duplicates.png");
        WriteImage(duplicates);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => IconCache.ForAsync(duplicates, 96)));
        check(concurrent.All(image => image is BitmapSource { PixelWidth: 96, PixelHeight: 48 })
            && concurrent.All(image => ReferenceEquals(image, concurrent[0])), "concurrent duplicate public requests share one published cached thumbnail");
        File.SetLastWriteTimeUtc(duplicates, DateTime.UtcNow.AddMinutes(2));
        WriteImage(duplicates, 100, 300);
        File.SetLastWriteTimeUtc(duplicates, DateTime.UtcNow.AddMinutes(3));
        var edited = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => IconCache.ForAsync(duplicates, 96)));
        check(edited.All(image => image is BitmapSource { PixelWidth: 32, PixelHeight: 96 })
            && edited.All(image => ReferenceEquals(image, edited[0])) && !ReferenceEquals(edited[0], concurrent[0]),
            "edited-version duplicate public requests consistently publish the new aspect ratio");
        string cancellationPath = Path.Combine(folder, "cancellation.png");
        WriteImage(cancellationPath);
        using var cancellation = new CancellationTokenSource();
        using var held = new FileOplock(cancellationPath);
        var first = IconCache.ForAsync(cancellationPath, 96, token: cancellation.Token);
        Task<ImageSource?> survivor = Task.FromResult<ImageSource?>(null);
        try
        {
            await Until(() => held.Broken.WaitOne(0));
            check(held.Broken.WaitOne(0) && !held.Released && !first.IsCompleted,
                "cancellation control reaches actual held image-open boundary");
            survivor = IconCache.ForAsync(cancellationPath, 96);
            await Task.Delay(50);
            cancellation.Cancel();
            bool canceled = false;
            try { await first; }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { canceled = true; }
            check(canceled, "one public waiter observes its own cancellation token");
            check(!survivor.IsCompleted && !held.Released, "canceling one waiter leaves another pending at the controlled filesystem boundary");
        }
        finally
        {
            held.Dispose();
            await survivor.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var survivingImage = await survivor;
        check(survivingImage is BitmapSource { PixelWidth: 96, PixelHeight: 48 }
            && ReferenceEquals(survivingImage, await IconCache.ForAsync(cancellationPath, 96)),
            "surviving waiter receives and caches the actual image after release despite canceled peer");
    }

    private sealed class FileOplock : IDisposable
    {
        private readonly object _gate = new();
        private readonly EventWaitHandle _broken = new(false, EventResetMode.ManualReset);
        private Microsoft.Win32.SafeHandles.SafeFileHandle? _file;
        private IntPtr _overlapped;
        private Timer? _timeout;
        public WaitHandle Broken => _broken;
        public bool Released { get; private set; }

        public FileOplock(string path)
        {
            try
            {
                _file = CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (_file.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                _overlapped = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size * 3 + 8);
                for (int offset = 0; offset < IntPtr.Size * 3 + 8; offset++) System.Runtime.InteropServices.Marshal.WriteByte(_overlapped, offset, 0);
                System.Runtime.InteropServices.Marshal.WriteIntPtr(_overlapped, IntPtr.Size * 2 + 8, _broken.SafeWaitHandle.DangerousGetHandle());
                bool complete = DeviceIoControl(_file, 0x00090000, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, _overlapped);
                if (complete || System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 997)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Owned oplock was not granted pending.");
                _timeout = new Timer(_ => Dispose(), null, 8000, Timeout.Infinite);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (Released) return;
                Released = true;
                _timeout?.Dispose();
                if (_file is { IsInvalid: false })
                {
                    if (_overlapped != IntPtr.Zero)
                    {
                        CancelIoEx(_file, _overlapped);
                        GetOverlappedResult(_file, _overlapped, out _, true);
                    }
                    _file.Dispose();
                }
                if (_overlapped != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(_overlapped);
                _overlapped = IntPtr.Zero;
                _broken.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr overlapped);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetOverlappedResult(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr overlapped, out uint transferred, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool wait);
    }

    private static async Task AddressWatcher(string folder, Action<bool, string> check, StringBuilder report)
    {
        using var browser = new Browser();
        string current = Directory.CreateDirectory(Path.Combine(folder, "watched")).FullName;
        string typed = Path.Combine(current, "activate.txt");
        File.WriteAllText(typed, "handled host boundary");
        File.WriteAllText(Path.Combine(current, "delete.txt"), "delete");
        File.WriteAllText(Path.Combine(current, "rename.txt"), "rename");
        check(await browser.NavigateTo(current), "public watcher fixture opens owned folder");
        File.WriteAllText(Path.Combine(current, "control.txt"), "control");
        await Until(() => browser.State.Results.Any(entry => entry.FileName == "control.txt"));
        check(browser.State.Results.Any(entry => entry.FileName == "control.txt"), "independent control event reconciles without address activation");
        using var observed = new FileSystemWatcher(current) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        int events = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Event() { if (Interlocked.Increment(ref events) >= 3) delivered.TrySetResult(); }
        observed.Created += (_, _) => Event();
        observed.Deleted += (_, _) => Event();
        observed.Renamed += (_, _) => Event();
        File.WriteAllText(Path.Combine(current, "created.txt"), "created");
        File.Delete(Path.Combine(current, "delete.txt"));
        File.Move(Path.Combine(current, "rename.txt"), Path.Combine(current, "renamed.txt"));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        check(!browser.State.Results.Any(entry => entry.FileName == "created.txt")
            && browser.State.Results.Any(entry => entry.FileName == "delete.txt")
            && browser.State.Results.Any(entry => entry.FileName == "rename.txt"), "actual create/delete/rename events are pending before debounce publication");
        int hostCalls = 0;
        browser.OpenInEditor = path => { if (path == typed) hostCalls++; return Task.FromResult(true); };
        check(await browser.NavigateAddress(typed) && hostCalls == 1 && browser.State.CurrentFolder == current,
            "public typed nonimage uses handled host boundary and retains folder");
        await Until(() => browser.State.Results.Any(entry => entry.FileName == "created.txt")
            && browser.State.Results.All(entry => entry.FileName != "delete.txt" && entry.FileName != "rename.txt")
            && browser.State.Results.Any(entry => entry.FileName == "renamed.txt"));
        check(browser.State.Results.Any(entry => entry.FileName == "created.txt"), "queued creation survives typed file activation");
        check(browser.State.Results.All(entry => entry.FileName != "delete.txt"), "queued deletion survives typed file activation");
        check(browser.State.Results.Any(entry => entry.FileName == "renamed.txt") && browser.State.Results.All(entry => entry.FileName != "rename.txt"),
            "queued rename survives typed file activation");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.OpenInEditor = _ => { entered.TrySetResult(); return release.Task; };
        var activation = browser.NavigateAddress(typed);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        File.WriteAllText(Path.Combine(current, "during-activation.txt"), "during");
        await Until(() => browser.State.Results.Any(entry => entry.FileName == "during-activation.txt"));
        release.TrySetResult(true);
        check(await activation && browser.State.Results.Any(entry => entry.FileName == "during-activation.txt"),
            "watcher continues reconciling while handled typed file activation is pending");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resolution = browser.NavigateAddress(@"\\192.0.2.1\ks-address-watch-" + Guid.NewGuid().ToString("N") + @"\missing.txt");
        File.WriteAllText(Path.Combine(current, "during-resolution.txt"), "during resolution");
        await Until(() => browser.State.Results.Any(entry => entry.FileName == "during-resolution.txt"));
        bool result = await resolution;
        report.AppendLine($"Address resolution durationMs={clock.ElapsedMilliseconds}, result={result}");
        check(!result && clock.Elapsed < TimeSpan.FromSeconds(6) && browser.State.Results.Any(entry => entry.FileName == "during-resolution.txt")
            && browser.State.CurrentFolder == current, "events during bounded unreachable address resolution reconcile without restarting retained watcher");
    }

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
    }

    private static async Task Containment(string folder, Action<bool, string> check)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "A")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(source, "B")).FullName;
        File.WriteAllText(Path.Combine(source, "original.txt"), "original bytes");
        using var files = new FilesView();
        check(await files.NavigateTo(child), "owned child opens through public navigation");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { source });
        files.ReadClipboard = () => data;
        var result = await files.PasteFiles();
        check(result is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(child, "A"))
            && File.ReadAllText(Path.Combine(source, "original.txt")) == "original bytes",
            "public paste rejects a directory into its descendant before creating anything");
        check(await files.NavigateTo(source), "owned source opens for self-paste control");
        result = await files.PasteFiles();
        check(result is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(source, "A")),
            "public paste rejects a directory into itself before creating anything");
        var self = FileOps.CopyOrMove([source], Path.Combine(source, "."), false,
            _ => ConflictChoice.KeepBoth, _ => { }, CancellationToken.None);
        check(self is { Succeeded: 0, Failed.Count: > 0 } && !Directory.Exists(Path.Combine(source, "A")),
            "canonical public operation rejects normalized self destination");
        string loose = Path.Combine(folder, "loose.txt");
        File.WriteAllText(loose, "loose bytes");
        var mixed = FileOps.CopyOrMove([loose, source], Path.Combine(child, "..", "B") + Path.DirectorySeparatorChar,
            true, _ => ConflictChoice.KeepBoth, _ => { }, CancellationToken.None);
        check(mixed is { Succeeded: 0, Failed.Count: > 0 } && File.ReadAllText(loose) == "loose bytes"
            && !File.Exists(Path.Combine(child, "loose.txt")) && !Directory.Exists(Path.Combine(child, "A")),
            "mixed-source move rejects the entire normalized descendant batch without moving an earlier file");
        var dropped = await files.DropFiles(data, child, DragDropEffects.Copy, ModifierKeys.Control);
        check(dropped == null && files.CommandError != null && !Directory.Exists(Path.Combine(child, "A")),
            "public drop also rejects descendant copy without mutation");
        var duplicate = FileOps.CopyOrMove([loose], folder, false, _ => ConflictChoice.KeepBoth,
            _ => { }, CancellationToken.None);
        check(duplicate is { Succeeded: 1, Failed.Count: 0 } && duplicate.SucceededTargets.Single() != loose
            && File.ReadAllText(duplicate.SucceededTargets.Single()) == "loose bytes" && File.ReadAllText(loose) == "loose bytes",
            "same-folder file copy preserves public duplicate naming and both copies");
        string outside = Directory.CreateDirectory(Path.Combine(folder, "outside")).FullName;
        var ordinary = FileOps.CopyOrMove([source], outside, false, _ => ConflictChoice.KeepBoth,
            _ => { }, CancellationToken.None);
        check(ordinary is { Succeeded: 1, Failed.Count: 0 }
            && File.ReadAllText(Path.Combine(outside, "A", "original.txt")) == "original bytes"
            && Directory.Exists(Path.Combine(outside, "A", "B")), "ordinary directory copy to a sibling remains supported");
    }

    /// <summary>Copy then paste into the folder that holds the copied folder, the way Explorer users
    /// duplicate a folder, and cut then paste into the same folder, which Explorer treats as nothing.</summary>
    private static async Task SameFolderPaste(string folder, Action<bool, string> check)
    {
        string source = Directory.CreateDirectory(Path.Combine(folder, "A")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a bytes");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b bytes");
        using var files = new FilesView();
        int conflicts = 0;
        files.ConflictPrompt = _ => { conflicts++; return ConflictChoice.KeepBoth; };
        check(await files.NavigateTo(folder), "same-folder paste fixture opens through public navigation");
        DataObject Clip(int effect)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { source });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(effect)));
            return data;
        }
        string[] Inside() => Directory.GetFileSystemEntries(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] original = ["a.txt", "sub", Path.Combine("sub", "b.txt")];

        files.ReadClipboard = () => Clip(1);
        var copied = await files.PasteFiles();
        check(conflicts == 0 && Inside().SequenceEqual(original, StringComparer.OrdinalIgnoreCase)
            && File.ReadAllText(Path.Combine(folder, "A (2)", "a.txt")) == "a bytes"
            && File.ReadAllText(Path.Combine(folder, "A (2)", "sub", "b.txt")) == "b bytes"
            && copied is { Failed.Count: 0 },
            $"same-folder folder copy lands beside the original without self-conflicts (conflicts={conflicts}, inside={string.Join("|", Inside())})");

        conflicts = 0;
        files.ReadClipboard = () => Clip(2);
        var moved = await files.PasteFiles();
        check(conflicts == 0 && (moved == null || moved.Succeeded == 0) && Directory.Exists(source)
            && Inside().SequenceEqual(original, StringComparer.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(folder, "A (3)")),
            $"same-folder cut and paste changes nothing (conflicts={conflicts}, succeeded={moved?.Succeeded})");
    }

    /// <summary>Every row of a single-folder listing lives in that folder, so the filter must match
    /// names only; matching the folder made any part of the current path match every row.</summary>
    private static async Task FolderFilter(string folder, Action<bool, string> check)
    {
        string pictures = Directory.CreateDirectory(Path.Combine(folder, "Pictures")).FullName;
        File.WriteAllText(Path.Combine(pictures, "a.txt"), "a");
        File.WriteAllText(Path.Combine(pictures, "b.png"), "b");
        using var files = new FilesView();
        check(await files.NavigateTo(pictures), "filter fixture opens through public navigation");
        files.SetFilter("Pictures");
        check(files.VisibleCount == 0, $"filtering by the current folder's name hides every row (visible={files.VisibleCount})");
        files.SetFilter("a");
        check(files.VisibleCount == 1, $"filtering by a name matches only that row (visible={files.VisibleCount})");
        files.SetFilter("");
    }

    /// <summary>A move to another drive is a copy then a delete; camera cards hold read-only files.</summary>
    private static Task ReadOnlyCrossVolumeMove(string folder, Action<bool, string> check)
    {
        string source = Path.Combine(folder, "card.jpg");
        string target = Directory.CreateDirectory(Path.Combine(folder, "other-drive")).FullName;
        File.WriteAllText(source, "photo");
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
        bool first = true;
        void Rename(string from, string to, uint flags)
        {
            if (first) { first = false; throw new IOException("Not the same device.", unchecked((int)0x80070011)); }
            File.Move(from, to, overwrite: (flags & 1) != 0);
        }
        var result = FileOps.CopyOrMove([source], target, true, _ => ConflictChoice.Cancel, _ => { }, CancellationToken.None,
            (_, _, _) => throw new InvalidOperationException("No replacement expected."), renameFile: Rename);
        string moved = Path.Combine(target, "card.jpg");
        check(result is { Succeeded: 1, Failed.Count: 0 } && !File.Exists(source) && File.Exists(moved)
            && (File.GetAttributes(moved) & FileAttributes.ReadOnly) != 0,
            $"a read-only file moved across drives ends only at its destination, still read-only (succeeded={result.Succeeded}, failed={string.Join("|", result.Failed.Select(f => f.Error))}, sourceLeft={File.Exists(source)})");
        foreach (string file in new[] { source, moved })
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        return Task.CompletedTask;
    }

    private static void WriteImage(string path, int width = 320, int height = 160)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 220; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task<WeakReference> RequestWeak(string path)
    {
        var image = await IconCache.ForAsync(path, 64);
        if (image is not BitmapSource { PixelWidth: 64, PixelHeight: 32 })
            throw new InvalidOperationException("Public thumbnail did not decode owned image.");
        return new WeakReference(image);
    }

    private static void CollectImages()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task CacheRetention(string folder, Action<bool, string> check, StringBuilder report)
    {
        var references = new List<WeakReference>();
        for (int i = 0; i < 270; i++)
        {
            string path = Path.Combine(folder, $"photo-{i}.png");
            WriteImage(path);
            references.Add(await RequestWeak(path));
        }
        CollectImages();
        int alive = references.Count(reference => reference.IsAlive);
        report.AppendLine($"Public thumbnail objects retained after 270 requests and GC: {alive}");
        check(alive <= 256 && !references[0].IsAlive && references[^1].IsAlive,
            "photo cache retains at most 256 public thumbnail objects and releases evicted images");
        var hot = await RequestWeak(Path.Combine(folder, "photo-14.png"));
        string extra = Path.Combine(folder, "photo-270.png");
        WriteImage(extra);
        references.Add(await RequestWeak(extra));
        CollectImages();
        check(hot.IsAlive && references[14].IsAlive && !references[15].IsAlive,
            "public cache hit refreshes recency so the next oldest photo is evicted");
        string edited = Path.Combine(folder, "edited.png");
        var versions = new List<WeakReference>();
        for (int i = 0; i < 5; i++)
        {
            WriteImage(edited);
            File.SetLastWriteTimeUtc(edited, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i));
            versions.Add(await RequestWeak(edited));
        }
        CollectImages();
        report.AppendLine($"Public edited thumbnail versions retained after GC: {versions.Count(reference => reference.IsAlive)} of 5");
        check(versions.Take(4).All(reference => !reference.IsAlive) && versions[^1].IsAlive,
            "changed timestamps release all superseded public thumbnail objects");
    }

    private static async Task RequestedSize(string folder, Action<bool, string> check)
    {
        string path = Path.Combine(folder, "sizes.png");
        WriteImage(path);
        var small = await IconCache.ForAsync(path, 64) as BitmapSource;
        check(small is { PixelWidth: 64, PixelHeight: 32 }, "public small request decodes 64 by 32");
        var large = await IconCache.ForAsync(path, 256) as BitmapSource;
        check(large is { PixelWidth: 256, PixelHeight: 128 },
            "public large request after small request honors 256 pixels within the same shell tier");
    }

    private static readonly Color[] FixtureColors = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow, Colors.Magenta, Colors.Cyan];

    private static void WriteTaggedImage(string path, bool tiff, ushort orientation)
    {
        var pixels = new byte[96 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 96; x++)
            {
                Color color = FixtureColors[y / 32 * 3 + x / 32];
                int offset = (y * 96 + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
            }
        var image = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 96 * 4);
        var metadata = new BitmapMetadata(tiff ? "tiff" : "jpg");
        metadata.SetQuery(tiff ? "/ifd/{ushort=274}" : "/app1/ifd/{ushort=274}", orientation);
        BitmapEncoder encoder = tiff ? new TiffBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ReadColorCells(BitmapSource image, int columns, int rows)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var cells = new StringBuilder();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var pixel = new byte[4];
                converted.CopyPixels(new Int32Rect((2 * column + 1) * image.PixelWidth / (2 * columns),
                    (2 * row + 1) * image.PixelHeight / (2 * rows), 1, 1), pixel, 4, 0);
                var closest = FixtureColors.Select((color, index) => (Index: index,
                    Error: Math.Abs(color.B - pixel[0]) + Math.Abs(color.G - pixel[1]) + Math.Abs(color.R - pixel[2])))
                    .OrderBy(candidate => candidate.Error).First();
                cells.Append(closest.Error < 60 ? (char)('A' + closest.Index) : '?');
            }
        return cells.ToString();
    }

    private static async Task Orientations(string folder, Action<bool, string> check, StringBuilder report)
    {
        // Literal displayed cell orders for stored ABC / DEF, from the eight EXIF orientations.
        string[] expected = ["ABCDEF", "CBAFED", "FEDCBA", "DEFABC", "ADBECF", "DAEBFC", "FCEBDA", "CFBEAD"];
        foreach (bool tiff in new[] { false, true })
            for (ushort tag = 1; tag <= 8; tag++)
            {
                string path = Path.Combine(folder, $"tag-{tag}." + (tiff ? "tif" : "jpg"));
                WriteTaggedImage(path, tiff, tag);
                using (var stream = File.OpenRead(path))
                {
                    var frame = BitmapFrame.Create(stream);
                    var metadata = (BitmapMetadata)frame.Metadata;
                    check(Convert.ToUInt16(metadata.GetQuery(tiff ? "/ifd/{ushort=274}" : "/app1/ifd/{ushort=274}")) == tag,
                        $"fixture {(tiff ? "TIFF" : "JPEG")} stores actual orientation {tag}");
                }
                var image = await IconCache.ForAsync(path, 96) as BitmapSource;
                int width = tag >= 5 ? 64 : 96, height = tag >= 5 ? 96 : 64;
                string cells = image == null ? "null" : ReadColorCells(image, tag >= 5 ? 2 : 3, tag >= 5 ? 3 : 2);
                report.AppendLine($"{Path.GetFileName(path)} actual={image?.PixelWidth}x{image?.PixelHeight} cells={cells} expected={expected[tag - 1]}");
                check(image != null && image.PixelWidth == width && image.PixelHeight == height && cells == expected[tag - 1],
                    $"public {(tiff ? "TIFF" : "JPEG")} thumbnail orientation {tag} matches literal asymmetric cells and bounded dimensions");
                var small = await IconCache.ForAsync(path, 48) as BitmapSource;
                string smallCells = small == null ? "null" : ReadColorCells(small, tag >= 5 ? 2 : 3, tag >= 5 ? 3 : 2);
                check(small != null && small.PixelWidth == width / 2 && small.PixelHeight == height / 2 && smallCells == expected[tag - 1],
                    $"public {(tiff ? "TIFF" : "JPEG")} orientation {tag} downsamples to a bounded 48-pixel longest side");
            }
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    private sealed class TestKeyboard() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private static async Task ColumnMenu(Action<bool, string> check, StringBuilder report)
    {
        using var files = new FilesView();
        files.ViewState.CreatedVisible = false;
        var window = new Window { Content = files, Width = 1200, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            files.SetView(2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var header = (FrameworkElement)files.FindName("DetailsHeader");
            var name = Children<Button>(header).Single(button => Grid.GetColumn(button) == 1);
            check(name.IsVisible && name.ActualWidth > 0, "owned offscreen Details header is laid out");
            ContextMenu? offered = null;
            bool keyboardOpening = false;
            header.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, e) =>
            {
                keyboardOpening = e.CursorLeft == -1 && e.CursorTop == -1;
                offered = header.ContextMenu;
                e.Handled = true; // Observe the actual routed opening without displaying a popup.
            }), true);
            var keyboard = new TestKeyboard();
            keyboard.Focus(name);
            var presentation = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Fixture has no presentation source.");
            var key = new KeyEventArgs(keyboard, presentation, 0, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent };
            bool handled = InputManager.Current.ProcessInput(key);
            report.AppendLine($"Context keyboard opening={keyboardOpening}; input handled={handled}; menu items={offered?.Items.Count}");
            check(keyboardOpening && offered?.Items.Count == 4,
                "context-menu key on actual header routes to the four localized column choices");
            var menu = offered ?? throw new InvalidOperationException("Column menu was not offered.");
            var created = menu.Items.OfType<MenuItem>().Last();
            check(!files.ViewState.CreatedVisible && !created.IsChecked && Convert.ToString(created.Header) == "created",
                "keyboard column menu exposes the existing localized unchecked Created action");
            var peer = new MenuItemAutomationPeer(created);
            var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider
                ?? throw new InvalidOperationException("Column item has no toggle provider.");
            toggle.Toggle();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(files.ViewState.CreatedVisible && created.IsChecked,
                "actual column menu automation toggle makes Created visible");
        }
        finally { window.Close(); }
    }
    private static async Task ColumnFocus(Action<bool, string> check, StringBuilder report)
    {
        using var files = new FilesView();
        files.ViewState.CreatedVisible = false;
        files.ViewState.SizeVisible = files.ViewState.ModifiedVisible = files.ViewState.TypeVisible = true;
        var window = new Window { Content = files, Width = 1200, Height = 700, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            files.SetView(2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var header = (FrameworkElement)files.FindName("DetailsHeader");
            var headings = Children<Button>(header).OrderBy(Grid.GetColumn).ToArray();
            check(headings.Length == 6 && headings[0].IsVisible, "all actual header actions exist in offscreen fixture");
            KeyboardNavigation.SetTabNavigation(header, KeyboardNavigationMode.Cycle);
            FocusManager.SetIsFocusScope(header, true);
            FocusManager.SetFocusedElement(header, headings[0]);
            bool moved = headings[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            var focused = FocusManager.GetFocusedElement(header) as Button;
            report.AppendLine($"Forward after Name: moved={moved}, column={(focused == null ? -1 : Grid.GetColumn(focused))}");
            check(moved && focused == headings[2], "forward Tab skips hidden Folder and reaches Size");
            (focused ?? throw new InvalidOperationException("Column focus was not assigned."))
                .MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            check(FocusManager.GetFocusedElement(header) == headings[0], "backward Tab skips hidden Folder and returns to Name");
            headings[4].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[0], "forward Tab skips default hidden Created");
            files.ViewState.CreatedVisible = true;
            files.ViewState.LocationHidden = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            headings[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[1], "shown Folder returns to forward Tab traversal");
            headings[4].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[5], "shown Created returns to forward Tab traversal");
            files.ViewState.SizeVisible = files.ViewState.ModifiedVisible = files.ViewState.TypeVisible = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            headings[1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            check(FocusManager.GetFocusedElement(header) == headings[5], "forward Tab skips hidden Size Modified and Type");
            headings[5].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            check(FocusManager.GetFocusedElement(header) == headings[1], "backward Tab skips hidden Size Modified and Type");
        }
        finally { window.Close(); }
    }
}
