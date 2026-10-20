using System.IO;
using System.Diagnostics;
using System.Text;

namespace KillerShot.Services;

internal sealed class ImageOpenRequests(string folder)
{
    private bool _draining;
    public bool IsDraining => _draining;
    public bool HasPending => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.request").Any();

    public string Publish(string path)
    {
        Directory.CreateDirectory(folder);
        string request = Path.Combine(folder, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.request");
        OutputFiles.WriteAtomic(request, false, stream => stream.Write(Encoding.UTF8.GetBytes(path)));
        return request;
    }

    public void Drain(Action<string> open)
    {
        if (_draining || !Directory.Exists(folder)) return;
        _draining = true;
        try
        {
            // Process every queued request in timestamp/ID order. Keep dirty-document prompts
            // serialized even when a modal prompt pumps another wake callback.
            for (int count = 0; count < 64; count++)
            {
                string? request = Directory.EnumerateFiles(folder, "*.request").Order(StringComparer.Ordinal).FirstOrDefault();
                if (request == null) return;
                string path = File.ReadAllText(request);
                OutputFiles.WriteAtomic(request + ".ack", true, stream => stream.Write("delivered"u8));
                open(path);
                File.Delete(request);
            }
        }
        finally { _draining = false; }
    }

    public async Task SendAsync(string path, string wakeName, TimeSpan timeout)
    {
        string request = Publish(path);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (File.Exists(request + ".ack"))
            {
                File.Delete(request + ".ack");
                return;
            }
            try { using var wake = EventWaitHandle.OpenExisting(wakeName); wake.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException("The image request remains queued, but the running KillerShot did not confirm delivery in time.");
    }
}
