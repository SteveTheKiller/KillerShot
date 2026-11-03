using System.ComponentModel;
using System.Windows.Data;

namespace KillerShot.Files;

public static class Sorting
{
    public static void ApplySort(BrowseState state)
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(state.Results);
        using var refresh = view.DeferRefresh();
        view.CustomSort = null;
        view.SortDescriptions.Clear();
        view.GroupDescriptions.Clear();
        string? group = state.GroupDates ? state.SortIndex switch
        {
            4 => nameof(FileEntry.ModifiedGroup),
            6 => nameof(FileEntry.CreatedGroup),
            _ => null
        } : null;
        bool foldersFirst = group == null && state.FoldersOnTop;
        if (foldersFirst || state.SortIndex != 0 || !state.SortAsc)
            view.CustomSort = new EntryComparer(state.SortIndex, state.SortAsc, foldersFirst, view.Culture);
        if (group != null) view.GroupDescriptions.Add(new PropertyGroupDescription(group));
    }

    public static void ApplyFilter(BrowseState state)
    {
        var view = CollectionViewSource.GetDefaultView(state.Results);
        string query = state.FilterText.Trim();
        if (query.Length == 0)
        {
            if (view.Filter != null) view.Filter = null;
            return;
        }
        view.Filter = value => value is FileEntry entry
            && (entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Folder.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private sealed class EntryComparer(int column, bool ascending, bool foldersFirst,
        System.Globalization.CultureInfo? culture) : System.Collections.IComparer
    {
        private readonly System.Collections.Comparer _strings = culture == null || culture == System.Globalization.CultureInfo.InvariantCulture
            ? System.Collections.Comparer.DefaultInvariant : culture == System.Globalization.CultureInfo.CurrentCulture
                ? System.Collections.Comparer.Default : new(culture);
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not FileEntry left || y is not FileEntry right) return x == null ? -1 : y == null ? 1 : 0;
            if (foldersFirst)
            {
                int folder = right.IsDirectory.CompareTo(left.IsDirectory);
                if (folder != 0) return folder;
            }
            int order = column switch
            {
                1 => _strings.Compare(left.FileName, right.FileName),
                2 => _strings.Compare(left.Folder, right.Folder),
                3 => left.SizeBytes.CompareTo(right.SizeBytes),
                4 => left.Modified.CompareTo(right.Modified),
                5 => _strings.Compare(left.TypeName, right.TypeName),
                6 => left.Created.CompareTo(right.Created),
                _ => ascending ? 0 : left.Seq.CompareTo(right.Seq)
            };
            if (!ascending) order = -order;
            return order == 0 && column == 5 ? _strings.Compare(left.FileName, right.FileName) : order;
        }
    }
}
