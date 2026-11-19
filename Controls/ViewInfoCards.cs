using KillerFamily.Controls;
using KillerShot.Services;

namespace KillerShot.Controls;

internal static class ViewInfoCards
{
    internal static event Action? Changed;

    internal static bool Get() => Settings.GetBool("CarouselInfoCards", false);

    /// <summary>The Show info cards tile: filled while on, and it flips in place. Dimmed where the
    /// view shows no info cards, so the card's rows stay put.</summary>
    internal static CardTile Tile(bool enabled = true) =>
        new("\uE946", LocaleManager.Loc("Str_View_ShowInfoCards"), null, () =>
        {
            Settings.SetBool("CarouselInfoCards", !Get());
            Changed?.Invoke();
        }, false, enabled) { Checked = Get(), KeepsOpen = true };
}
