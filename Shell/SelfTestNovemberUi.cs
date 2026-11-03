using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using KillerShot.Services;
using System.Xml.Linq;
using System.Windows.Input;
using KillerShot.Controls;

namespace KillerShot.Shell;

internal static class SelfTestNovemberUi
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 4 || args[0] != "--selftest-november-ui") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            string screenshots = Path.Combine(folder, "screenshots");
            Directory.CreateDirectory(screenshots);
            FolderCaptures.UseFolder(screenshots);
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (args[2] == "resources")
            {
                CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
                CaptureLibrary.BackgroundWork = false;
                CaptureLibrary.OpenAuto();
                var window = new MainWindow();
                var unresolved = new HashSet<string>();
                foreach (string file in Directory.EnumerateFiles(Path.Combine(args[3], "Shell"), "*.cs"))
                    foreach (Match match in Regex.Matches(File.ReadAllText(file), "SetResourceReference\\([^;]*?\"([A-Za-z][A-Za-z0-9_]+)\"\\)"))
                    {
                        string key = match.Groups[1].Value;
                        if (!HasResource(window, key)) unresolved.Add(key);
                    }
                if (unresolved.Count != 0) throw new InvalidOperationException("Unresolved shell resources: " + string.Join(", ", unresolved));
                report.Add("All literal shell resource references resolve PASS");
            }
            else if (args[2] == "locales")
            {
                var keys = Regex.Matches(File.ReadAllText(Path.Combine(args[3], "Controls", "Controls.xaml")), @"DynamicResource\s+(Str_[A-Za-z0-9_]+)")
                    .Select(match => match.Groups[1].Value).Distinct().ToArray();
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                foreach (string locale in Directory.EnumerateFiles(Path.Combine(args[3], "Strings"), "*.xaml"))
                {
                    var present = XDocument.Load(locale).Root!.Elements().Select(element => (string?)element.Attribute(x + "Key")).ToHashSet();
                    var missing = keys.Where(key => !present.Contains(key)).ToArray();
                    if (missing.Length > 0) throw new InvalidOperationException(Path.GetFileName(locale) + " missing " + string.Join(", ", missing));
                }
                report.Add("Every Controls.xaml localized resource exists in every locale PASS");
            }
            else if (args[2] == "shelf-wheel")
            {
                var view = new LibraryView();
                var row = new LibraryRow
                {
                    Kind = LibraryRowKind.Shelf, Key = "test", Title = "Shelf",
                    Items = [new CaptureItem(1, DateTime.Today, 64, 32, "region", null, null, null, null),
                        new CaptureItem(2, DateTime.Today, 64, 32, "region", null, null, null, null)],
                };
                var panel = (StackPanel)view.BuildRow(row);
                var flow = panel.Children.OfType<CoverFlow>().Single();
                int initial = flow.SelectedIndex;
                if (flow.HandleWheel(120, ModifierKeys.None) || flow.SelectedIndex != initial)
                    throw new InvalidOperationException("A shelf consumed the vertical wheel instead of scrolling the library.");
                if (!flow.HandleWheel(120, ModifierKeys.Shift) || flow.SelectedIndex == initial)
                    throw new InvalidOperationException("Shift+wheel did not navigate shelf cards.");
                report.Add("Shelf vertical wheel bubbles; Shift+wheel changes cards PASS");
            }
            else if (args[2] == "picker-placement")
            {
                double x = SystemParameters.WorkArea.Right + 200;
                Settings.Set("FileDlgX", x.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Settings.Set("FileDlgY", "100");
                var dialog = new Controls.FileDialog();
                if (dialog.WindowStartupLocation != WindowStartupLocation.Manual || dialog.Left != x || dialog.Top != 100)
                    throw new InvalidOperationException("Saved placement outside the primary work area was discarded before monitor fitting.");
                report.Add("Saved secondary-monitor coordinates retained before monitor fitting PASS");
            }
            else if (args[2] == "released-title-press")
            {
                var dialog = new PromptDialog("Name", "", "OK")
                { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                dialog.Show();
                dialog.UpdateLayout();
                try
                {
                    var title = Descendants(dialog).OfType<Border>().Single(border => Panel.GetZIndex(border) == 1);
                    title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                }
                finally { dialog.Close(); }
                report.Add("Released promoted title press does not start a native drag or throw PASS");
            }
            else if (args[2] == "cli-routing")
            {
                string program = File.ReadAllText(Path.Combine(args[3], "KillerShot.Cli", "Program.cs"));
                string dispatch = program[program.IndexOf("private static Task<int> RunAsync", StringComparison.Ordinal)..program.IndexOf("private static async Task<int> QrAsync", StringComparison.Ordinal)];
                var verbs = Regex.Matches(dispatch, "case\\s+\"([^\"]+)\"\\s*:")
                    .Select(match => match.Groups[1].Value).Distinct();
                foreach (string verb in verbs)
                {
                    if (!Cli.Program.Commands.Contains(verb)) throw new InvalidOperationException("CLI verb not routed: " + verb);
                    var routed = KillerFamily.CommandLine.CommandLineEntry.GetArguments([verb], Cli.Program.Commands);
                    string expected = verb is "help" or "-h" or "/?" ? "--help" : verb;
                    if (routed is not { Length: 1 } || routed[0] != expected) throw new InvalidOperationException("CLI verb fell through: " + verb);
                }
                report.Add("Every implemented CLI verb routes through the app command entry PASS");
            }
            else throw new ArgumentException("Unknown UI self-test scenario: " + args[2]);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "november-ui-report.txt"), report);
        Application.Current.Shutdown(code);
        return true;
    }

    private static bool HasResource(DependencyObject node, string key)
    {
        if (node is FrameworkElement element && element.TryFindResource(key) != null) return true;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency && HasResource(dependency, key)) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
