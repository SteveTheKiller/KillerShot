using System.Globalization;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>A database and folder query result, independent of the library's controls.</summary>
internal sealed record LibraryListing(IReadOnlyList<CaptureItem> Items, IReadOnlyList<CaptureCollection> Collections,
    IReadOnlyList<CaptureItem> FolderItems, int Total, bool LinkedPhotos)
{
    internal static LibraryListing Read(string source, string search)
    {
        var collections = CaptureLibrary.Collections();
        var items = CaptureLibrary.Items(search.Length > 0 ? search : null);
        var folderItems = FolderCaptures.Snapshot(search.Length > 0 ? search : null);
        bool Matches(CaptureItem item) => source == "all" || (source == "c0"
            ? item.CollectionId == null || !collections.Any(c => c.Id == item.CollectionId)
            : "c" + item.CollectionId?.ToString(CultureInfo.InvariantCulture) == source);
        int total;
        if (FolderCaptures.IsSource(source))
        {
            items = FolderCaptures.ItemsForSource(source, search.Length > 0 ? search : null);
            total = search.Length > 0 ? FolderCaptures.ItemsForSource(source).Count : items.Count;
            collections = [];
        }
        else
        {
            items = items.Where(Matches).ToList();
            total = search.Length > 0 ? CaptureLibrary.CountItems(source) : items.Count;
            if (source != "all") collections = collections.Where(c => "c" + c.Id.ToString(CultureInfo.InvariantCulture) == source).ToList();
        }
        if (source != "all") folderItems = [];
        bool linkedPhotos = FolderCaptures.LinkedCollections().Any(folder => FolderCaptures.LinkedItems(folder.Source, search).Count > 0);
        return new(items, collections, folderItems, total, linkedPhotos);
    }
}
