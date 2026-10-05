using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>The part of a folder's items one page shows.</summary>
/// <param name="First">Index of the first item on the page.</param>
/// <param name="Count">Number of items on the page.</param>
/// <param name="Page">Zero-based page number.</param>
/// <param name="PageCount">Number of pages; 1 when every item fits.</param>
/// <param name="PreviousSlot">Slot of the previous-page tile, or -1 when the folder is not paged.</param>
/// <param name="NextSlot">Slot of the next-page tile, or -1 when the folder is not paged.</param>
internal readonly record struct FolderPage(int First, int Count, int Page, int PageCount, int PreviousSlot, int NextSlot)
{
    public bool Paged => PageCount > 1;
}

/// <summary>
/// Splits a folder's items into pages when they do not fit its grid. A folder with more items than free slots
/// gives its last two slots in reading order to a previous and a next tile, which wrap around. Not thread-safe:
/// the owning provider calls it under its own lock.
/// </summary>
internal sealed class FolderPager(AudioFolderGrid grid)
{
    private int _page;
    private int _pageCount = 1;

    /// <summary>
    /// Lays out <paramref name="itemCount"/> items on the current page. A list that shrank moves the page back to
    /// its last one, so the folder never shows an empty page.
    /// </summary>
    public FolderPage Layout(int itemCount)
    {
        int free = grid.FreeSlots;

        // Two navigation tiles need a third slot for at least one item.
        bool paged = itemCount > free && free >= 3;
        int perPage = paged ? free - 2 : free;

        _pageCount = paged ? (itemCount + perPage - 1) / perPage : 1;
        _page = Math.Clamp(_page, 0, _pageCount - 1);

        int first = _page * perPage;
        return new FolderPage(first, Math.Clamp(itemCount - first, 0, perPage), _page, _pageCount,
            paged ? grid.SlotForIndex(free - 2) : -1,
            paged ? grid.SlotForIndex(free - 1) : -1);
    }

    /// <summary>Turns <paramref name="delta"/> pages, wrapping around. False when there is only one page.</summary>
    public bool Turn(int delta)
    {
        if (_pageCount <= 1) return false;

        _page = (((_page + delta) % _pageCount) + _pageCount) % _pageCount;
        return true;
    }

    /// <summary>Adds the previous and next tiles of a paged folder; <paramref name="turn"/> gets -1 or +1.</summary>
    public static void AddNavigation(List<FolderEntry> entries, FolderPage page, IPluginHost host, Action<int> turn)
    {
        if (!page.Paged) return;

        // The page number shows on both tiles, so it reads the same whichever one the eye lands on.
        string position = $"{page.Page + 1}/{page.PageCount}";
        entries.Add(NavigationEntry(page.PreviousSlot, $"{host.Tr("Previous")} {position}", () => turn(-1)));
        entries.Add(NavigationEntry(page.NextSlot, $"{host.Tr("Next")} {position}", () => turn(+1)));
    }

    private static FolderEntry NavigationEntry(int slot, string text, Action press) => new()
    {
        SlotIndex = slot,
        Text = text,
        TextSize = 14,
        BackColor = PluginColor.FromRgb(0x30, 0x30, 0x30),
        OnPress = () =>
        {
            press();
            return Task.CompletedTask;
        }
    };
}
