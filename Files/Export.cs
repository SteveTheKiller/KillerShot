using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Files;

// KillerShell's export (Shell/Export.cs, Services/CsvExport.cs, Services/HtmlExporter.cs): one
// toolbar icon, CSV or HTML picked from a flyout, both written from the current listing.
// Column headers in the CSV stay English because it is read by machines. Partial of FilesView.
public partial class FilesView
{
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        ExportPopup.IsOpen = !ExportPopup.IsOpen;
        if (ExportPopup.IsOpen && ExportPopup.Child is UIElement child) Anim.FadeIn(child);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => ExportWithDialog(csv: true);
    private void ExportHtml_Click(object sender, RoutedEventArgs e) => ExportWithDialog(csv: false);

    private void ExportWithDialog(bool csv)
    {
        ExportPopup.IsOpen = false;
        OverflowPopup.IsOpen = false;
        if (ExportRows().Count == 0) { ShowCommandResult(Loc("Str_Status_NothingExport")); return; }
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dialog = new FileDialog(FileDialogMode.Save)
        {
            Filter = csv ? "CSV File|*.csv" : "HTML Files|*.html",
            FileName = $"KillerShot-{stamp}.{(csv ? "csv" : "html")}",
            Title = Loc(csv ? "Str_Export_TitleCsv" : "Str_Export_TitleHtml"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (csv) ExportCsvTo(dialog.FileName); else ExportHtmlTo(dialog.FileName, openAfter: true);
    }

    // The rows the user can see, in the order they see them, filter included.
    private List<FileEntry> ExportRows() =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(Navigation.Results).Cast<FileEntry>().ToList();

    public bool ExportCsvTo(string path) => ReportExport(path, () => WriteBytes(path, ListingCsv.Build(ExportRows())));

    public bool ExportHtmlTo(string path, bool openAfter)
    {
        string folder = Browser.IsThisPc(Navigation.CurrentFolder) ? Loc("Str_Nav_ThisPc") : Navigation.CurrentFolder;
        bool ok = ReportExport(path, () => WriteBytes(path, ListingHtml.Build(ExportRows(), folder)));
        // Opening is a courtesy: the file was written even when no browser takes it.
        if (ok && openAfter)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch { }
        return ok;
    }

    private bool ReportExport(string path, Action write)
    {
        try
        {
            write();
            ShowCommandResult(string.Format(CultureInfo.CurrentCulture, Loc("Str_Status_Exported"), path));
            return true;
        }
        catch (Exception error)
        {
            SetCommandError(string.Format(CultureInfo.CurrentCulture, Loc("Str_Status_ExportFailed"), error.Message));
            return false;
        }
    }

    private static void WriteBytes(string path, string text) =>
        OutputFiles.WriteAtomic(path, overwrite: true, stream =>
        {
            byte[] bom = Encoding.UTF8.GetPreamble();
            stream.Write(bom, 0, bom.Length);
            byte[] body = Encoding.UTF8.GetBytes(text);
            stream.Write(body, 0, body.Length);
        });
}

internal static class ListingCsv
{
    internal const string Header = "Name,Folder,SizeBytes,Modified";

    internal static string Build(IEnumerable<FileEntry> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", Text(row.FileName), Text(row.Folder),
                row.IsDirectory ? string.Empty : row.SizeBytes.ToString(CultureInfo.InvariantCulture),
                Text(row.Modified == default ? string.Empty : row.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    /// <summary>
    /// One quoted text cell. A cell a spreadsheet would read as a formula gets a leading
    /// apostrophe, because quoting alone does not stop it being evaluated (OWASP, CSV injection).
    /// </summary>
    internal static string Text(string s)
    {
        if (StartsLikeFormula(s)) s = "'" + s;
        return $"\"{s.Replace("\"", "\"\"")}\"";
    }

    private static bool StartsLikeFormula(string s)
    {
        if (s.Length == 0) return false;
        if (s[0] == '\t' || s[0] == '\r') return true;
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        if (i == s.Length) return false;
        // The fullwidth forms are included because some spreadsheets fold them to ASCII.
        char c = s[i];
        return c is '=' or '+' or '-' or '@' or (char)0xFF1D or (char)0xFF0B or (char)0xFF0D or (char)0xFF20;
    }
}

// The styled listing report: fourteen theme switcher, accent picker, click-to-sort columns.
// It opens in the theme, accent and language the app was in at export time.
internal static class ListingHtml
{
    private const string SiteUrl = "https://killershot.net";

    private static readonly (string Key, string Label, string File)[] ThemeFiles =
    [
        ("dark", "Dark", "Dark"), ("light", "Light", "Light"), ("black", "Black", "Black"), ("98se", "98SE", "98SE"),
        ("blood", "Blood", "Blood"), ("greed", "Greed", "Greed"), ("cyanotic", "Cyanotic", "Cyanotic"),
        ("ectoplasm", "Ectoplasm", "Ectoplasm"), ("decay", "Decay", "Decay"), ("mourning", "Mourning", "Mourning"),
        ("sepulchre", "Sepulchre", "Sepulchre"), ("delirium", "Delirium", "Delirium"), ("malaise", "Malaise", "Malaise"),
    ];

    private sealed record Palette(string Key, string Label, string Bg, string Surface, string Pane, string Accent,
        string Text, string Muted, string Border, string Hover);

    private static List<Palette> LoadPalettes()
    {
        var result = new List<Palette>();
        foreach (var (key, label, file) in ThemeFiles)
        {
            try
            {
                var d = (ResourceDictionary)Application.LoadComponent(new Uri($"/KillerShot;component/Themes/{file}.xaml", UriKind.Relative));
                string H(string name) => Hex(d[name], "#808080");
                result.Add(new Palette(key, label, H("BackgroundBrush"), H("SurfaceBrush"), H("PaneBrush"), H("PrimaryBrush"),
                    H("TextBrush"), H("MutedTextBrush"), H("PaneBorderBrush"), H("RowHoverBrush")));
            }
            catch { result.Add(new Palette(key, label, "#1e1e1e", "#252526", "#1e1e1e", "#50aee8", "#e0e0e0", "#9a9a9a", "#3c3c3c", "#2a2d2e")); }
        }
        return result;
    }

    private static string Hex(object? value, string fallback) =>
        value is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : fallback;

    private static string L(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string FormatSize(long b)
    {
        if (b <= 0) return "";
        if (b < 1024) return b + " B";
        double kb = b / 1024.0;
        if (kb < 1024) return kb.ToString("0", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (mb / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    internal static string Build(IReadOnlyList<FileEntry> rows, string folder)
    {
        string current = ThemeManager.Current == Theme.SE98 ? "98se" : ThemeManager.Current.ToString().ToLowerInvariant();
        string accent = Hex(Application.Current?.TryFindResource("PrimaryBrush"), "#50AEE8");
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var palettes = LoadPalettes();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang='{LocaleManager.LanguageTag}' class='theme-{current}'><head><meta charset='utf-8'/>");
        sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
        sb.AppendLine("<title>KillerShot Report</title>");
        sb.AppendLine("<style>");
        foreach (var t in palettes)
            sb.Append("html.theme-").Append(t.Key).Append("{--bg:").Append(t.Bg).Append(";--surface:").Append(t.Surface)
              .Append(";--pane:").Append(t.Pane).Append(";--accent:").Append(t.Accent).Append(";--text:").Append(t.Text)
              .Append(";--muted:").Append(t.Muted).Append(";--border:").Append(t.Border).Append(";--hover:").Append(t.Hover).AppendLine("}");
        sb.AppendLine("*{box-sizing:border-box}");
        sb.AppendLine("body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,'Segoe UI',Roboto,sans-serif;padding:24px;line-height:1.5}");
        sb.AppendLine(".wrap{max-width:1250px;margin:0 auto}");
        sb.AppendLine(".topbar{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin-bottom:8px}");
        sb.AppendLine(".brand{display:flex;align-items:center;gap:10px;min-height:38px}");
        sb.AppendLine(".wordmark{font-family:Consolas,'Courier New',monospace;font-size:27px;font-weight:700;letter-spacing:.5px}");
        sb.AppendLine(".wordmark .k{color:var(--text)}.wordmark .s{color:var(--accent)}");
        sb.AppendLine(".switchers{display:flex;flex-direction:column;gap:6px;align-items:flex-end}");
        sb.AppendLine(".swrow{display:flex;align-items:center;gap:8px}");
        sb.AppendLine(".swlabel{color:var(--muted);font-size:10px;font-family:Consolas,monospace;letter-spacing:.5px;text-transform:uppercase}");
        sb.AppendLine(".themesw{display:flex;gap:7px;align-items:center}");
        sb.AppendLine(".themesw button{width:18px;height:18px;border-radius:50%;border:2px solid var(--border);cursor:pointer;padding:0;outline:none;transition:transform .1s}");
        sb.AppendLine(".themesw button:hover{transform:scale(1.15)}.themesw button.active{border-color:var(--text)}");
        sb.AppendLine(".meta{color:var(--muted);font-size:13px;margin:0 0 6px}.meta b{color:var(--text)}");
        sb.AppendLine(".query{color:var(--muted);font-size:14px;margin:0 0 18px}.query b{color:var(--accent);font-weight:600}");
        sb.AppendLine(".tablewrap{position:relative;overflow-x:auto;border:1px solid var(--border);border-radius:8px;background:var(--pane);box-shadow:0 10px 30px rgba(0,0,0,.45)}");
        sb.AppendLine("table{position:relative;z-index:1;border-collapse:collapse;width:100%;min-width:720px}");
        sb.AppendLine("th{background:var(--surface);color:var(--muted);text-align:left;padding:10px 14px;font-size:12px;font-weight:600;font-family:Consolas,monospace;letter-spacing:.3px;position:sticky;top:0;white-space:nowrap;user-select:none}");
        sb.AppendLine("th:hover{color:var(--text)}th .arrow{display:inline-block;width:10px;font-size:10px;opacity:.7}");
        sb.AppendLine(".sortbtn{background:none;border:0;padding:0;margin:0;font:inherit;color:inherit;letter-spacing:inherit;text-align:inherit;cursor:pointer}");
        sb.AppendLine(".sortbtn:focus-visible,.themesw button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}");
        sb.AppendLine("td{border-top:1px solid var(--border);padding:7px 14px;font-size:13px;vertical-align:top}");
        sb.AppendLine("td.name{font-family:Consolas,monospace;font-weight:600;white-space:nowrap}");
        sb.AppendLine("td.dir{font-family:Consolas,monospace;font-size:12px;color:var(--muted);word-break:break-all}");
        sb.AppendLine("td.num{font-family:Consolas,monospace;white-space:nowrap;text-align:right}");
        sb.AppendLine("td.mod{font-family:Consolas,monospace;white-space:nowrap}");
        sb.AppendLine("tbody tr:hover td{background:var(--hover)}");
        sb.AppendLine(".footer{color:var(--muted);font-size:11px;margin-top:18px;opacity:.85}");
        sb.AppendLine(".footer a{color:var(--accent);text-decoration:none}.footer a:hover{text-decoration:underline}");
        sb.AppendLine("@media(max-width:640px){body{padding:12px}.wordmark{font-size:22px}th,td{padding:8px 10px;font-size:12px}}");
        sb.AppendLine("</style></head><body><div class='wrap'>");
        sb.AppendLine("<div class='topbar'><div class='brand'><span class='wordmark'><span class='k'>Killer</span><span class='s'>Shot</span></span></div><div class='switchers'>");
        sb.AppendLine($"<div class='swrow'><span class='swlabel'>{Esc(L("Str_Lbl_Theme", "theme"))}</span><div class='themesw' id='themesw'></div></div>");
        sb.AppendLine($"<div class='swrow'><span class='swlabel'>{Esc(L("Str_Lbl_Accent", "accent"))}</span><div class='themesw' id='accentsw'></div></div>");
        sb.AppendLine("</div></div>");
        sb.AppendLine("<p class='meta'>" + string.Format(CultureInfo.InvariantCulture, L("Str_Rpt_MetaList", "Generated {0} · {1} items"),
            $"<b>{ts}</b>", $"<b>{rows.Count.ToString("N0", CultureInfo.InvariantCulture)}</b>") + "</p>");
        sb.AppendLine("<p class='query'>" + string.Format(CultureInfo.InvariantCulture, L("Str_Rpt_Listing", "Listing of {0}."), $"<b>{Esc(folder)}</b>") + "</p>");
        sb.AppendLine("<div class='tablewrap'><table id='tbl'><thead><tr>");
        foreach (var (type, label) in new[]
        {
            ("text", L("Str_Col_Name", "name")), ("text", L("Str_Col_Folder", "location")),
            ("num", L("Str_Col_Size", "size")), ("num", L("Str_Col_Modified", "modified")),
        })
            sb.AppendLine($"<th data-type='{type}' aria-sort='none'><button type='button' class='sortbtn'>{Esc(label)} <span class='arrow' aria-hidden='true'></span></button></th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var r in rows)
        {
            long size = r.IsDirectory ? 0 : r.SizeBytes;
            bool noDate = r.Modified == default;
            sb.Append("<tr>")
              .Append($"<td class='name'>{Esc(r.FileName)}</td>")
              .Append($"<td class='dir'>{Esc(r.Folder)}</td>")
              .Append($"<td class='num' data-sort='{size}'>{FormatSize(size)}</td>")
              .Append($"<td class='mod' data-sort='{(noDate ? "0" : r.Modified.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))}'>{(noDate ? "" : r.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}</td>")
              .AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></div>");
        string genBy = string.Format(CultureInfo.InvariantCulture, L("Str_Rpt_GenBy", "Generated by {0}"),
            $"<a href='{SiteUrl}' target='_blank' rel='noopener'>KillerShot</a>");
        sb.AppendLine($"<p class='footer'>{genBy} &middot; &copy; 2026 Steve the Killer</p>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>");
        sb.AppendLine("var THEMES=[" + string.Join(",", palettes.Select(t => "['" + t.Key + "','" + t.Label + "','" + t.Pane + "']")) + "];");
        sb.AppendLine("var sw=document.getElementById('themesw');");
        sb.AppendLine("function setTheme(t){document.documentElement.className='theme-'+t;var k=sw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-t')===t)?'active':'';}");
        sb.AppendLine("THEMES.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-t',a[0]);b.style.background=a[2];b.onclick=function(){setTheme(a[0])};sw.appendChild(b);});");
        sb.AppendLine($"setTheme('{current}');");
        sb.AppendLine("var ACCENTS=[['#DD504B','Red'],['#E8962C','Orange'],['#1EA54C','Green'],['#1FB8A8','Teal'],['#50AEE8','Blue'],['#B982E3','Purple']];");
        sb.AppendLine("var asw=document.getElementById('accentsw');");
        sb.AppendLine("function setAccent(c){if(c){document.documentElement.style.setProperty('--accent',c);}var k=asw.children;for(var i=0;i<k.length;i++)k[i].className=(k[i].getAttribute('data-c').toLowerCase()===String(c).toLowerCase())?'active':'';}");
        sb.AppendLine("ACCENTS.forEach(function(a){var b=document.createElement('button');b.title=a[1];b.setAttribute('data-c',a[0]);b.style.background=a[0];b.onclick=function(){setAccent(a[0])};asw.appendChild(b);});");
        sb.AppendLine($"setAccent('{accent}');");
        sb.AppendLine("var tbl=document.getElementById('tbl'),tb=tbl.tBodies[0],sortCol=-1,sortDir=1;");
        sb.AppendLine("var ths=tbl.tHead.rows[0].cells;");
        sb.AppendLine("for(var c=0;c<ths.length;c++){(function(i){ths[i].getElementsByTagName('button')[0].onclick=function(){");
        sb.AppendLine("sortDir=(sortCol===i)?-sortDir:1;sortCol=i;var num=ths[i].getAttribute('data-type')==='num';");
        sb.AppendLine("var rows=Array.prototype.slice.call(tb.rows);rows.sort(function(a,b){var x,y;if(num){x=parseFloat(a.cells[i].getAttribute('data-sort'))||0;y=parseFloat(b.cells[i].getAttribute('data-sort'))||0;}else{x=a.cells[i].innerText.toLowerCase();y=b.cells[i].innerText.toLowerCase();}return (x>y?1:x<y?-1:0)*sortDir;});");
        sb.AppendLine("for(var r=0;r<rows.length;r++)tb.appendChild(rows[r]);");
        sb.AppendLine("for(var h=0;h<ths.length;h++){ths[h].getElementsByClassName('arrow')[0].textContent='';ths[h].setAttribute('aria-sort','none');}");
        sb.AppendLine("ths[i].setAttribute('aria-sort',sortDir>0?'ascending':'descending');");
        sb.AppendLine("ths[i].getElementsByClassName('arrow')[0].textContent=sortDir>0?'\\u25B2':'\\u25BC';};})(c);}");
        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
