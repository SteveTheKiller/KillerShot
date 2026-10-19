using System.Security.Cryptography;
using System.Text;

namespace KillerShot.Services;

internal static partial class CaptureLibrary
{
    private static ExternalAlbumChanges? _externalChanges;

    private static string ChangeEventName => @"Local\KillerShot-Album-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(FilePath).ToUpperInvariant())));

    private static void ObserveExternalChanges()
    {
        lock (_gate)
        {
            if (_db == null || !BackgroundWork || _externalChanges != null) return;
            _externalChanges = new ExternalAlbumChanges(ChangeEventName);
        }
    }

    private static void NotifyExternalChange()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ChangeEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Album change notification failed: {0}", error.Message); }
    }

    private sealed class ExternalAlbumChanges : IDisposable
    {
        private readonly EventWaitHandle _signal;
        private readonly RegisteredWaitHandle _wait;

        internal ExternalAlbumChanges(string name)
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_externalChanges, this) || _db == null) return;
                    ClearThumbCache();
                }
                RaiseChanged();
                StartOcr();
                StartQrReading();
            }, null, Timeout.Infinite, false);
        }

        public void Dispose()
        {
            _wait.Unregister(null);
            _signal.Dispose();
        }
    }
}
