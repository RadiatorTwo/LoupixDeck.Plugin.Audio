using System.Runtime.CompilerServices;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Plugin-owned mirror of the SDK's <c>FolderGridInfo</c>, with the same fields and the same
/// <see cref="SlotForIndex"/> algorithm. Kept as a separate type so no provider, command or
/// static field in this plugin ever mentions the SDK type in a signature — see
/// <see cref="FolderGridResolver"/> for why that matters.
/// </summary>
internal sealed record AudioFolderGrid(int Columns, int Rows, int BackSlotIndex)
{
    /// <summary>Addressable slots — entries outside this range are dropped by the host.</summary>
    public int TotalSlots => Columns * Rows;

    /// <summary>
    /// Fills the grid in reading order, skipping the reserved back slot, and stops when the
    /// grid is full. Returns the slot index for the n-th entry, or -1 when it does not fit.
    /// </summary>
    public int SlotForIndex(int entryIndex)
    {
        int slot = 0;
        for (int i = 0; i <= entryIndex; i++)
        {
            if (slot == BackSlotIndex) slot++;
            if (slot >= TotalSlots) return -1;
            if (i == entryIndex) return slot;
            slot++;
        }
        return -1;
    }
}

/// <summary>
/// Resolves the active device's folder grid with a fallback for hosts built against an older
/// SDK. The host loads a plugin when the plugin's <c>SdkVersion</c> major matches the host's SDK
/// major, so this plugin (SDK 1.22) still loads into a 1.21 host, where the SDK assembly the
/// host resolves has neither the <c>IPluginHost.FolderGrid</c> member nor the
/// <c>FolderGridInfo</c> type at all. Merely JITting a method whose signature mentions
/// <c>FolderGridInfo</c> — a field, a parameter, a static initializer — triggers a
/// <see cref="TypeLoadException"/> on such a host, regardless of whether that method body ever
/// runs; a missing member on an otherwise-known type would instead throw
/// <see cref="MissingMethodException"/>. Either failure is possible here depending on how the
/// old assembly differs, so both are caught.
///
/// To contain the blast radius, <c>FolderGridInfo</c> appears nowhere outside
/// <see cref="ReadFolderGrid"/>: that single method is marked
/// <see cref="MethodImplOptions.NoInlining"/> so its own JIT failure cannot be deferred into a
/// caller, and it converts the SDK type to the plugin-owned <see cref="AudioFolderGrid"/> before
/// returning. Every other member here — <see cref="Resolve"/>, its fallback, and every provider
/// and command that consumes the result — deals only in <see cref="AudioFolderGrid"/>, built
/// from the <see cref="FolderLayout"/> <c>const</c>s that have existed unchanged since the first
/// 1.x SDK release, so they resolve on any 1.x host.
/// </summary>
internal static class FolderGridResolver
{
    /// <summary>Falls back to the 5x3 layout described by <see cref="FolderLayout"/>.</summary>
    private static readonly AudioFolderGrid Fallback =
        new(FolderLayout.Columns, FolderLayout.TotalSlots / FolderLayout.Columns, FolderLayout.BackSlotIndex);

    public static AudioFolderGrid Resolve(IPluginHost host)
    {
        try
        {
            return ReadFolderGrid(host);
        }
        catch (MissingMethodException)
        {
            return Fallback;
        }
        catch (TypeLoadException)
        {
            return Fallback;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AudioFolderGrid ReadFolderGrid(IPluginHost host)
    {
        FolderGridInfo grid = host.FolderGrid;
        return new AudioFolderGrid(grid.Columns, grid.Rows, grid.BackSlotIndex);
    }
}
