using System.Windows.Controls;
using KillerShot.Services;

namespace KillerShot.Controls;

internal static class ViewInfoCards
{
    internal static event Action? Changed;

    internal static bool Get() => Settings.GetBool("CarouselInfoCards", false);

    internal static MenuItem Menu()
    {
        var item = new MenuItem
        {
            Header = LocaleManager.Loc("Str_View_ShowInfoCards"), IsCheckable = true, IsChecked = Get(),
        };
        item.Click += (_, _) =>
        {
            Settings.SetBool("CarouselInfoCards", !Get());
            Changed?.Invoke();
        };
        return item;
    }
}
