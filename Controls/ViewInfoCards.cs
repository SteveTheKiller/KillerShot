using System.Windows.Controls;
using KillerShot.Services;

namespace KillerShot.Controls;

internal static class ViewInfoCards
{
    internal static event Action? Changed;

    internal static bool Get(bool rolodex) => Settings.GetBool(rolodex ? "RolodexInfoCards" : "CarouselInfoCards", rolodex);

    internal static MenuItem Menu(bool rolodex)
    {
        var item = new MenuItem
        {
            Header = LocaleManager.Loc("Str_View_ShowInfoCards"), IsCheckable = true, IsChecked = Get(rolodex),
        };
        item.Click += (_, _) =>
        {
            Settings.SetBool(rolodex ? "RolodexInfoCards" : "CarouselInfoCards", !Get(rolodex));
            Changed?.Invoke();
        };
        return item;
    }
}
