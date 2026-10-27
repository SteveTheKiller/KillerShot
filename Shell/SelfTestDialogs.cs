using System.Reflection;
using KillerShot.Controls;

namespace KillerShot.Shell;

/// <summary>Self-test helpers for the family file picker.</summary>
internal static class SelfTestDialogs
{
    /// <summary>Accepts the picker with its current FileName. Assigning DialogResult directly does
    /// not work: the picker cancels that close to run its fade, and WPF resets a canceled
    /// DialogResult to null. This records the result the way Accept does and closes through the fade.</summary>
    internal static void Accept(FileDialog dialog)
    {
        typeof(FileDialog).GetField("_pendingResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(dialog, (bool?)true);
        typeof(FileDialog).GetProperty(nameof(FileDialog.FileNames))!.SetValue(dialog, new[] { dialog.FileName });
        dialog.Close();
    }
}
