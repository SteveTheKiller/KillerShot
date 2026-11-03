using System.ComponentModel;
using System.Windows.Data;

namespace KillerShot.Files;

public static class Sorting
{
    public static void ApplySort(BrowseState state)
    {
        var view = CollectionViewSource.GetDefaultView(state.Results);
        using var refresh = view.DeferRefresh();
        view.SortDescriptions.Clear();
        view.GroupDescriptions.Clear();
        string? group = state.GroupDates ? state.SortIndex switch
        {
            4 => nameof(FileEntry.ModifiedGroup),
            6 => nameof(FileEntry.CreatedGroup),
            _ => null
        } : null;
        if (group == null && state.FoldersOnTop)
            view.SortDescriptions.Add(new SortDescription(nameof(FileEntry.IsDirectory), ListSortDirection.Descending));
        string? property = state.SortIndex switch
        {
            1 => nameof(FileEntry.FileName), 2 => nameof(FileEntry.Folder), 3 => nameof(FileEntry.SizeBytes),
            4 => nameof(FileEntry.Modified), 5 => nameof(FileEntry.TypeName), 6 => nameof(FileEntry.Created), _ => null
        };
        if (property == null && !state.SortAsc) property = nameof(FileEntry.Seq);
        if (property != null)
            view.SortDescriptions.Add(new SortDescription(property,
                state.SortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
        if (state.SortIndex == 5)
            view.SortDescriptions.Add(new SortDescription(nameof(FileEntry.FileName), ListSortDirection.Ascending));
        if (group != null) view.GroupDescriptions.Add(new PropertyGroupDescription(group));
    }

    public static void ApplyFilter(BrowseState state)
    {
        var view = CollectionViewSource.GetDefaultView(state.Results);
        string query = state.FilterText.Trim();
        view.Filter = query.Length == 0 ? null : value => value is FileEntry entry
            && (entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Folder.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
