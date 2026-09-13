using System.Runtime.CompilerServices;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Resolves <see cref="IPluginHost.FolderGrid"/> with a fallback for hosts built against an
/// older SDK. The host loads a plugin when the major SDK version matches, so this plugin can
/// still run on a 1.21 host, where <c>FolderGrid</c> does not exist and calling it throws
/// <see cref="MissingMethodException"/> at JIT time. The property read is isolated in its own
/// non-inlined method so the exception is caught before it unwinds into the caller.
/// </summary>
internal static class FolderGridResolver
{
    /// <summary>Falls back to the 5x3 layout described by <see cref="FolderLayout"/>.</summary>
    private static readonly FolderGridInfo Fallback =
        new(FolderLayout.Columns, FolderLayout.TotalSlots / FolderLayout.Columns, FolderLayout.BackSlotIndex);

    public static FolderGridInfo Resolve(IPluginHost host)
    {
        try
        {
            return ReadFolderGrid(host);
        }
        catch (MissingMethodException)
        {
            return Fallback;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FolderGridInfo ReadFolderGrid(IPluginHost host) => host.FolderGrid;
}
