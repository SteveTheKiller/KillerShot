using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerShot.Services;

public enum RenderChoice { Auto, Hardware, Software }

/// <summary>
/// Hardware or software rendering, chosen in About. Remote-control tools that share the console
/// session (ScreenConnect, Kaseya Live Connect, VNC, TeamViewer) can show a hardware-rendered WPF
/// window as black, which is why Killendar forces software. KillerShot defaults to Auto: software
/// in a Remote Desktop session or while one of those tools is running, hardware otherwise.
/// </summary>
internal static class RenderModeService
{
    // Process names of remote-control agents that capture the console session.
    private static readonly string[] RemoteAgents =
    [
        "ScreenConnect.ClientService", "ScreenConnect.WindowsClient", "AgentMon", "KaseyaRemoteControlHost",
        "TeamViewer", "TeamViewer_Service", "tvnserver", "winvnc", "vncserver", "AnyDesk", "Splashtop", "SRService",
    ];

    public static RenderChoice Choice =>
        Enum.TryParse<RenderChoice>(Settings.Get("RenderMode"), out var c) ? c : RenderChoice.Auto;

    public static bool RemoteControlDetected()
    {
        if (SystemParameters.IsRemoteSession) return true;
        foreach (var name in RemoteAgents)
        {
            try
            {
                var found = Process.GetProcessesByName(name);
                bool any = found.Length > 0;
                foreach (var p in found) p.Dispose();
                if (any) return true;
            }
            catch { }
        }
        return false;
    }

    public static bool UsesSoftware(RenderChoice choice) => choice switch
    {
        RenderChoice.Software => true,
        RenderChoice.Hardware => false,
        _ => RemoteControlDetected(),
    };

    /// <summary>Applies the stored choice. Called at startup before any window exists.</summary>
    public static void Apply()
    {
        RenderOptions.ProcessRenderMode = UsesSoftware(Choice) ? RenderMode.SoftwareOnly : RenderMode.Default;
    }

    /// <summary>Stores and applies a new choice. Windows created after this use it; the open
    /// editor window picks it up the next time it opens.</summary>
    public static void Set(RenderChoice choice)
    {
        Settings.Set("RenderMode", choice.ToString());
        Apply();
    }
}
