using System.IO;
using System.Diagnostics;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestOpenRequests
{
    internal static bool TryRun(string[] args)
    {
        if (args is ["--selftest-open-request-send", var queue, var wake, var path])
        {
            _ = SendAsync(queue, wake, path);
            return true;
        }
        if (args is not ["--selftest-open-requests", var folder]) return false;
        _ = RunAsync(folder);
        return true;
    }

    private static async Task SendAsync(string queue, string wake, string path)
    {
        int code = 0;
        try { await new ImageOpenRequests(queue).SendAsync(path, wake, TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { code = 1; File.WriteAllText(Path.Combine(queue, path + ".error"), ex.ToString()); }
        App.Current.Shutdown(code);
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        int code = 1;
        var report = new List<string>();
        try
        {
            var requests = new ImageOpenRequests(Path.Combine(folder, "queue"));
            requests.Publish("A.png");
            requests.Publish("B.png");
            var delivered = new List<string>();
            requests.Drain(delivered.Add);
            if (!delivered.SequenceEqual(new[] { "A.png", "B.png" }))
                throw new InvalidOperationException("Concurrent pending image opens lost or reordered a request: " + string.Join(",", delivered));
            report.Add("All pending image opens are delivered in order: PASS");
            requests.Publish("outer.png");
            requests.Drain(path =>
            {
                delivered.Add(path);
                if (path != "outer.png") return;
                requests.Publish("nested.png");
                requests.Drain(_ => throw new InvalidOperationException("A nested wake reentered document replacement."));
            });
            if (!delivered.TakeLast(2).SequenceEqual(new[] { "outer.png", "nested.png" }))
                throw new InvalidOperationException("Nested wakes did not preserve request ordering.");
            report.Add("Nested modal wakes serialize replacement prompts and deliver later requests: PASS");
            await CheckProcessesAsync(folder, report);
            bool timedOut = false;
            try { await new ImageOpenRequests(Path.Combine(folder, "unavailable")).SendAsync("queued.png",
                @"Local\KillerShot-test-missing-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(150)); }
            catch (TimeoutException) { timedOut = true; }
            if (!timedOut) throw new InvalidOperationException("Unavailable receiver reported delivery success.");
            report.Add("Unavailable receiver returns a bounded explicit delivery failure: PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "open-requests-report.txt"), report);
        App.Current.Shutdown(code);
    }

    private static async Task CheckProcessesAsync(string folder, List<string> report)
    {
        string queue = Path.Combine(folder, "process-queue");
        string wakeName = @"Local\KillerShot-test-" + Guid.NewGuid().ToString("N");
        var requests = new ImageOpenRequests(queue);
        var delivered = new List<string>();
        Process Launch(string path)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--selftest-open-request-send", queue, wakeName, path }) start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new InvalidOperationException("Sender process failed to launch.");
        }
        using var first = Launch("child-A.png");
        await WaitForPending(1, first);
        using var second = Launch("child-B.png");
        await WaitForPending(2, second);
        if (first.HasExited || second.HasExited) throw new InvalidOperationException("A sender reported delivery before listener startup.");
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, wakeName);
        var listener = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => App.Current.Dispatcher.BeginInvoke(
            new Action(() => requests.Drain(delivered.Add))), null, Timeout.Infinite, false);
        try
        {
            // Same startup drain as App.OnStartup: publication can precede event creation.
            requests.Drain(delivered.Add);
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(6));
            if (first.ExitCode != 0 || second.ExitCode != 0 || !delivered.SequenceEqual(new[] { "child-A.png", "child-B.png" }))
                throw new InvalidOperationException("Delayed listener lost or reordered isolated-process image requests.");
            report.Add("Two isolated senders wait through delayed listener startup, receive delivery acknowledgments and retain FIFO order: PASS");
        }
        finally { listener.Unregister(null); }

        async Task WaitForPending(int count, Process sender)
        {
            for (int i = 0; i < 100; i++)
            {
                if (sender.HasExited) throw new InvalidOperationException("Sender exited before its atomic request was published: " + sender.ExitCode);
                if (Directory.Exists(queue) && Directory.EnumerateFiles(queue, "*.request").Count() >= count) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Sender did not publish its request.");
        }
    }
}
