using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerFamily.Controls;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// What the self-tests read on a family tile card (ShotCards): tiles are found by their automation
/// name, the way a screen reader finds them; a filled tile is on; Delete for the one thing a card
/// is about sits in the header. NoTextRows is the card rule that everything is a tile.
/// </summary>
internal static class SelfTestCards
{
    /// <summary>The card's tiles in order, without the header's Delete.</summary>
    public static IEnumerable<Button> Tiles(ItemsControl card) =>
        ShotCards.Buttons(card).Where(button => !Equals(button.Tag, ShotCards.HeaderDeleteTag));

    public static string Name(Button tile) => AutomationProperties.GetName(tile);

    public static string[] Names(ItemsControl card) => Tiles(card).Select(Name).ToArray();

    // A tile drops a trailing "..." from its caption, so a menu string still finds its tile.
    private static bool Named(Button tile, string name) => Trim(Name(tile)) == Trim(name);
    private static string Trim(string text) => text.TrimEnd('.', '\u2026').TrimEnd();

    public static Button? Find(ItemsControl card, string name) => Tiles(card).FirstOrDefault(tile => Named(tile, name));

    public static Button Tile(ItemsControl card, string name) =>
        Find(card, name) ?? throw new InvalidOperationException($"The card has no '{name}' tile. It has: {string.Join(" | ", Names(card))}");

    /// <summary>A tile, or the header's Delete, by name: what a click on the card can run.</summary>
    public static Button Command(ItemsControl card, string name) =>
        ShotCards.Buttons(card).FirstOrDefault(button => Named(button, name))
        ?? throw new InvalidOperationException($"The card has no '{name}' command. It has: {string.Join(" | ", ShotCards.Buttons(card).Select(Name))}");

    /// <summary>Whether an on/off tile is on (the kit fills it while on).</summary>
    public static bool IsOn(Button tile) => tile.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue;

    public static void Click(Button tile) => tile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tile));

    /// <summary>The header's Delete, or null when the card has none.</summary>
    public static Button? HeaderDelete(ItemsControl card) =>
        ShotCards.Buttons(card).FirstOrDefault(button => Equals(button.Tag, ShotCards.HeaderDeleteTag));

    /// <summary>The rows a flyout tile opens.</summary>
    public static IList<object> FlyoutRows(ItemsControl card, string name) =>
        ShotCards.GetFlyoutRows(Tile(card, name)) ?? throw new InvalidOperationException($"The '{name}' tile opens no list.");

    /// <summary>A flyout row by its header text.</summary>
    public static MenuItem Row(ItemsControl card, string tile, string header) =>
        FlyoutRows(card, tile).OfType<MenuItem>().SingleOrDefault(row => Equals(row.Header, header) || Convert.ToString(row.Header) == header)
        ?? throw new InvalidOperationException($"The '{tile}' tile has no '{header}' row.");

    /// <summary>The card's title (the header slot's automation name).</summary>
    public static string? Title(ItemsControl card) =>
        card.Items.OfType<MenuItem>().FirstOrDefault() is { } first ? AutomationProperties.GetName(first) : null;

    /// <summary>Null when the card is only a header and tile grids; otherwise what breaks the rule.</summary>
    public static string? TextRows(ItemsControl card)
    {
        int index = 0;
        foreach (var item in card.Items)
        {
            if (item is Separator) return "a separator";
            if (item is not MenuItem row) return "a " + item.GetType().Name;
            string name = AutomationProperties.GetName(row) is { Length: > 0 } named ? named : Convert.ToString(row.Header) ?? "";
            if (row.Header is not FrameworkElement) return "the text row '" + row.Header + "'";
            if (row.Items.Count > 0) return "the submenu '" + name + "'";
            if (row.Icon != null || !string.IsNullOrEmpty(row.InputGestureText) || row.IsCheckable) return "the menu row '" + name + "'";
            // After the header, every slot is a row of tiles.
            if (index++ > 0 && !ShotCards.ButtonsIn(row).Any()) return "the text row '" + name + "'";
        }
        return null;
    }

    /// <summary>Throws when the card holds a text row, or when it has no tiles.</summary>
    public static void CheckCard(ItemsControl card, string what)
    {
        if (TextRows(card) is { } problem) throw new InvalidOperationException($"The {what} card holds {problem}; a card has tiles only.");
        if (!Tiles(card).Any()) throw new InvalidOperationException($"The {what} card has no tiles.");
    }

    /// <summary>Throws unless the card's header carries Delete under this name.</summary>
    public static Button CheckHeaderDelete(ItemsControl card, string name, string what)
    {
        var delete = HeaderDelete(card) ?? throw new InvalidOperationException($"The {what} card has no Delete in its header.");
        if (!Named(delete, name)) throw new InvalidOperationException($"The {what} card's header Delete is named '{Name(delete)}', not '{name}'.");
        if (Tiles(card).Any(tile => Named(tile, name))) throw new InvalidOperationException($"The {what} card has Delete as a tile as well as in its header.");
        return delete;
    }

    // --selftest-cards <new folder> [tray-repair|keyboard-reorder]: the customizable card's own checks.
    internal static bool TryRun(string[] args)
    {
        if (args.Length is not (2 or 3) || args[0] != "--selftest-cards") return false;
        _ = RunAsync(System.IO.Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : null);
        return true;
    }

    private static async Task RunAsync(string folder, string? only)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            if (System.IO.Directory.Exists(folder)) throw new InvalidOperationException("Use a new cards test folder.");
            System.IO.Directory.CreateDirectory(folder);
            Settings.UseFolder(System.IO.Path.Combine(folder, "settings"));
            App.InitializeKit();
            ThemeManager.Apply(Theme.Black);
            int failures = 0;
            foreach (var (name, run) in new (string, Func<List<string>, Task>)[] { ("tray-repair", TrayRepairAsync), ("keyboard-reorder", KeyboardReorderAsync) })
            {
                if (only is not null && only != name) continue;
                try { await run(report); report.Add($"{name}: PASS"); }
                catch (Exception error) { failures++; report.Add($"{name}: FAIL {error.Message}"); }
            }
            code = failures == 0 ? 0 : 1;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "cards-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static CardBuilder.EditDriver Driver(DependencyObject card) =>
        Visuals(card).Select(d => d.GetValue(CardBuilder.EditDriverProperty)).OfType<CardBuilder.EditDriver>().FirstOrDefault()
        ?? throw new InvalidOperationException("The card has no edit driver.");

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    // BF-202: a user who once edited the tray card has the repair tiles named in its drawer; a lost shortcut must still
    // bring them out, and that must not be written into the user's layout.
    private static async Task TrayRepairAsync(List<string> report)
    {
        const string saved = "v2:capture|window|monitor|open|exit/winsetting|retry|qr";
        Settings.Set("Card.Tray.Hidden", saved);
        CaptureSettings.SetShortcutText(ShortcutAction.Window, "Ctrl+NoSuchKey");
        using var tray = new TrayHost();
        tray.RegisterShortcuts();
        if (tray.FailedShortcuts.Count == 0) throw new InvalidOperationException("The test needs a lost shortcut.");
        var menu = tray.TestShowMenu();
        await Task.Delay(250);
        var order = Driver(menu).Order();
        report.Add("tray-repair: card order while a shortcut is lost = " + string.Join("|", order));
        menu.IsOpen = false;
        await Task.Delay(100);
        if (!order.Contains("winsetting") || !order.Contains("retry"))
            throw new InvalidOperationException("The repair tiles stayed in the drawer of an edited tray card while a shortcut was lost.");
        if (Settings.Get("Card.Tray.Hidden") != saved)
            throw new InvalidOperationException("Showing the repair tiles was written into the saved layout: " + Settings.Get("Card.Tray.Hidden"));
        // An edit made while a shortcut is lost saves the user's move, and the repair tiles keep their own place.
        menu = tray.TestShowMenu();
        await Task.Delay(250);
        Driver(menu).Editing(true);
        var folderTile = Tile(menu, LocaleManager.Loc("Str_Tray_OpenFolder"));
        folderTile.Focus();
        folderTile.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(folderTile), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Task.Delay(60);
        Driver(menu).Editing(false);
        menu.IsOpen = false;
        await Task.Delay(100);
        string edited = Settings.Get("Card.Tray.Hidden") ?? "";
        report.Add("tray-repair: saved after hiding Open folder while a shortcut is lost = " + edited);
        var parts = edited.StartsWith("v2:", StringComparison.Ordinal) ? edited[3..].Split('/') : ["", ""];
        var shownIds = parts[0].Split('|');
        var stashedIds = parts.Length > 1 ? parts[1].Split('|') : [];
        if (!stashedIds.Contains("folder")) throw new InvalidOperationException("The user's move was not saved: " + edited);
        if (shownIds.Contains("winsetting") || shownIds.Contains("retry") || !stashedIds.Contains("winsetting") || !stashedIds.Contains("retry"))
            throw new InvalidOperationException("Saving while a shortcut is lost moved the repair tiles out of the user's drawer: " + edited);
        if (stashedIds.Contains("savelast") || stashedIds.Contains("printlast"))
            throw new InvalidOperationException("Saving while a shortcut is lost hid Save last and Print last: " + edited);
        // Every shortcut off, so none is lost here even when another copy of KillerShot holds the real ones.
        foreach (var action in Enum.GetValues<ShortcutAction>()) CaptureSettings.SetShortcutText(action, "");
        tray.RegisterShortcuts();
        if (tray.FailedShortcuts.Count != 0) throw new InvalidOperationException("Clearing the bad shortcut left a failure: " + string.Join(", ", tray.FailedShortcuts));
        menu = tray.TestShowMenu();
        await Task.Delay(250);
        order = Driver(menu).Order();
        report.Add("tray-repair: card order once shortcuts are claimed = " + string.Join("|", order));
        menu.IsOpen = false;
        await Task.Delay(100);
        if (order.Contains("winsetting") || order.Contains("retry"))
            throw new InvalidOperationException("The repair tiles stayed on the card after every shortcut was claimed.");
    }

    // BF-101: in edit mode Alt+arrows move the focused tile one slot (Up/Down a row), saved like a drag, focus stays on
    // the moved tile, and the new position is announced.
    private static async Task KeyboardReorderAsync(List<string> report)
    {
        Settings.Set("Card.KeyTest.Hidden", "");
        var menu = ShotCards.Menu();
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = 100;
        menu.VerticalOffset = 100;
        menu.Items.Add(ShotCards.Header(menu, "Keys"));
        string[] names = ["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"];
        var tiles = names.Select(n => new CardTile("", n, null, () => { }) { Id = n.ToLowerInvariant() }).ToList();
        menu.Items.Add(ShotCards.Editable(menu, "KeyTest", ["alpha"], [], tiles));
        menu.IsOpen = true;
        await Task.Delay(250);
        var driver = Driver(menu);
        driver.Editing(true);
        await Task.Delay(100);

        async Task Press(string tile, Key key)
        {
            var button = Tile(menu, tile);
            button.Focus();
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            // Alt+arrow arrives as a system key: Key.System, with the arrow in SystemKey.
            (typeof(KeyEventArgs).GetMethod("MarkSystem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("KeyEventArgs.MarkSystem is missing.")).Invoke(args, null);
            button.RaiseEvent(args);
            await Task.Delay(60);
        }
        void Expect(string what, params string[] expected)
        {
            var order = driver.Order();
            report.Add($"keyboard-reorder: after {what}: {string.Join("|", order)}");
            if (!order.SequenceEqual(expected)) throw new InvalidOperationException($"After {what} the card order is {string.Join("|", order)}, not {string.Join("|", expected)}.");
        }
        void Focused(string tile)
        {
            var now = Tile(menu, tile);
            // A hidden desktop never activates the card, so keyboard focus may not land; the card's focus scope says
            // where it would.
            if (!now.IsKeyboardFocused && FocusManager.GetFocusedElement(FocusManager.GetFocusScope(now)) != now) throw new InvalidOperationException($"Focus left the moved tile {tile}.");
        }
        string Announced() =>
            Visuals(menu).OfType<FrameworkElement>().Where(e => AutomationProperties.GetLiveSetting(e) != AutomationLiveSetting.Off)
                .Select(AutomationProperties.GetName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

        await Press("Bravo", Key.Right);
        Expect("Alt+Right on Bravo", "alpha", "charlie", "bravo", "delta", "echo", "foxtrot");
        Focused("Bravo");
        string said = Announced();
        report.Add("keyboard-reorder: announced '" + said + "'");
        if (!said.Contains("Bravo") || !said.Contains('3') || !said.Contains('6')) throw new InvalidOperationException($"The move was not announced with its new position (live text '{said}').");
        if (!(Settings.Get("Card.KeyTest.Hidden") ?? "").StartsWith("v2:alpha|charlie|bravo|", StringComparison.Ordinal))
            throw new InvalidOperationException("The keyboard move was not saved: " + Settings.Get("Card.KeyTest.Hidden"));
        await Press("Bravo", Key.Down);
        Expect("Alt+Down on Bravo", "alpha", "charlie", "delta", "echo", "foxtrot", "bravo");
        Focused("Bravo");
        await Press("Bravo", Key.Up);
        Expect("Alt+Up on Bravo", "alpha", "charlie", "bravo", "delta", "echo", "foxtrot");
        await Press("Bravo", Key.Left);
        Expect("Alt+Left on Bravo", "alpha", "bravo", "charlie", "delta", "echo", "foxtrot");
        await Press("Alpha", Key.Right);
        Expect("Alt+Right on locked Alpha", "alpha", "bravo", "charlie", "delta", "echo", "foxtrot");
        driver.Editing(false);
        menu.IsOpen = false;
        await Task.Delay(100);
    }
}
