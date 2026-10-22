using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-carousel-automation &lt;folder&gt;. Cover Flow
/// exposes its captures as a list: every capture is a named item, the selection is
/// readable and settable, an offscreen capture can be selected and opened, and the list follows
/// collection changes.</summary>
internal static class SelfTestCarouselAutomation
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-carousel-automation") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        LocaleManager.Apply(Locale.EnUS);
        int code = 1;
        var report = new List<string>();
        Window? window = null;
        try
        {
            var view = new LibraryView();
            foreach (bool timeline in new[] { false, true })
            {
                var row = view.BuildRow(new LibraryRow { Kind = LibraryRowKind.Header, Title = "Tickets",
                    Detail = "3 captures", IsCollection = true, CollectionId = 7, Timeline = timeline });
                var header = row is System.Windows.Controls.Panel panel && timeline
                    ? panel.Children.OfType<FrameworkElement>().Last() : row;
                window = new Window { Content = row, Width = 450, Height = 120, Left = -20000, Top = -20000, ShowActivated = false };
                window.SetResourceReference(Control.BackgroundProperty, "ContentPaneBrush");
                window.Show();
                var peer = UIElementAutomationPeer.CreatePeerForElement(header);
                if (peer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke ||
                    peer.GetAutomationControlType() != AutomationControlType.Button || !peer.GetName().Contains("Tickets"))
                    throw new InvalidOperationException("Collection header is not a named invokable control.");
                invoke.Invoke();
                await Task.Delay(100);
                if (header.ContextMenu?.IsOpen != true || header.ContextMenu.Items.OfType<MenuItem>().Count() < 3)
                    throw new InvalidOperationException("Invoking the collection header did not open management actions.");
                header.ContextMenu.IsOpen = false;
                header.IsEnabled = false;
                try { invoke.Invoke(); throw new InvalidOperationException("Disabled header accepted Invoke."); }
                catch (ElementNotEnabledException) { }
                header.IsEnabled = true;
                header.Focusable = false;
                window.Activate();
                System.Windows.Input.Keyboard.Focus(window);
                header.Focusable = true;
                byte[] RenderHeader(string name)
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                        (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, name + ".png"));
                    encoder.Save(file);
                    return pixels;
                }
                var unfocused = RenderHeader("header-unfocused-" + timeline);
                peer.SetFocus();
                await Task.Delay(50);
                if (!peer.HasKeyboardFocus() || unfocused.SequenceEqual(RenderHeader("header-focused-" + timeline)))
                    throw new InvalidOperationException("Header automation focus has no visible indicator.");
                window.Close();
                try { invoke.Invoke(); throw new InvalidOperationException("Detached header accepted Invoke."); }
                catch (ElementNotAvailableException) { }
                window = null;
            }
            report.Add("collection headers expose named buttons and invoke management actions in grid and timeline PASS");
            var items = Enumerable.Range(0, 80).Select(i => new CaptureItem(i, DateTime.Today, 320, 200, "window", "Picture " + i, null, null, null)).ToArray();
            var flow = new CoverFlow { Describe = i => i.Window ?? "" };
            window = new Window { Content = flow, Width = 900, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            foreach (FrameworkElement control in new FrameworkElement[] { flow })
            {
                var carousel = (ICaptureCarousel)control;
                CaptureItem? opened = null;
                if (control is CoverFlow f) { f.Items = items; f.ItemActivated += i => opened = i; }
                await Task.Delay(100);
                string kind = control.GetType().Name;
                var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                var children = peer.GetChildren() ?? [];
                if (children.Count != 80 || children.Select(c => c.GetName()).Distinct().Count() != 80 || !children[5].GetName().StartsWith("Picture 5, 6 / 80"))
                    throw new InvalidOperationException($"{kind}: captures are not exposed as 80 named items.");
                if (children.Any(c => c.GetAutomationControlType() != AutomationControlType.ListItem))
                    throw new InvalidOperationException($"{kind}: items are not list items.");
                var selection = (ISelectionProvider)peer.GetPattern(PatternInterface.Selection)!;
                if (selection.GetSelection().Length != 1) throw new InvalidOperationException($"{kind}: no selected item is reported.");

                var target = children[60];
                if (!target.IsOffscreen()) throw new InvalidOperationException($"{kind}: an offscreen capture claims to be on screen.");
                ((ISelectionItemProvider)target.GetPattern(PatternInterface.SelectionItem)!).Select();
                if (carousel.SelectedIndex != 60 || target.IsOffscreen() || !((ISelectionItemProvider)target.GetPattern(PatternInterface.SelectionItem)!).IsSelected)
                    throw new InvalidOperationException($"{kind}: selecting an offscreen capture did not bring it to the front.");
                ((IInvokeProvider)children[10].GetPattern(PatternInterface.Invoke)!).Invoke();
                if (opened?.Id != 10 || carousel.SelectedIndex != 10) throw new InvalidOperationException($"{kind}: invoking a capture did not open it.");

                // A collection change keeps the same capture selected and the item list current.
                var subset = items.Where(i => i.Id % 2 == 0).Reverse().ToArray();
                flow.Items = subset;
                await Task.Delay(50);
                children = peer.GetChildren() ?? [];
                var now = children[carousel.SelectedIndex];
                if (children.Count != 40 || !now.GetName().StartsWith("Picture 10,"))
                    throw new InvalidOperationException($"{kind}: the item list or selection did not follow the collection change.");
                report.Add($"{kind}: 80 named items, selection, offscreen select, invoke and collection change PASS");
            }
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(folder, "carousel-automation-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
