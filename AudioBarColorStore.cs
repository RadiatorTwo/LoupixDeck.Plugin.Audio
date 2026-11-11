using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Resolves the fill color of a side-strip volume bar: the color set for the bar's device,
/// otherwise the default bar color, otherwise the built-in green. Colors are stored as
/// <c>#RRGGBB</c> strings in <see cref="IPluginSettings"/>.
/// </summary>
internal sealed class AudioBarColorStore(IPluginSettings settings)
{
    internal const string DefaultKey = "strip.barColor";
    internal const string KeyPrefix = "color:";

    /// <summary>The color setting kind, shown by the host as a color picker (SDK 1.37).</summary>
    internal const PluginSettingKind ColorKind = PluginSettingKind.Color;

    internal static readonly PluginColor BuiltInFill = new(0x4C, 0xAF, 0x50);

    /// <summary>The fill for a bar; <paramref name="endpointId"/> is null for an app bar.</summary>
    public PluginColor Resolve(string? endpointId)
    {
        if (endpointId != null && TryParse(settings.Get<string>(KeyPrefix + endpointId), out PluginColor own))
            return own;

        return TryParse(settings.Get<string>(DefaultKey), out PluginColor fallback) ? fallback : BuiltInFill;
    }

    /// <summary>
    /// Removes any <c>color:*</c> key whose value is empty, so the settings file does not collect
    /// cleared entries. Saves if anything changed.
    /// </summary>
    public void CleanupEmpty()
    {
        List<string> toRemove = settings.Keys
            .Where(key => key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                          && string.IsNullOrWhiteSpace(settings.Get<string>(key)))
            .ToList();
        if (toRemove.Count == 0) return;

        foreach (string key in toRemove) settings.Remove(key);
        settings.Save();
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RGB</c>, with or without the leading <c>#</c>.</summary>
    internal static bool TryParse(string? text, out PluginColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6
            || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;

        color = new PluginColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
