using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// Resolves an audio endpoint's display name, preferring a user-defined alias
/// stored in <see cref="IPluginSettings"/> over the OS-provided friendly name.
/// </summary>
public sealed class AudioAliasStore
{
    internal const string KeyPrefix = "alias:";

    private readonly IPluginSettings _settings;

    public AudioAliasStore(IPluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Raised after <see cref="Reload"/> so listeners can refresh their UI.</summary>
    public event Action? Changed;

    /// <summary>Returns the alias if set, otherwise the shortened friendly name.</summary>
    public string Resolve(AudioEndpointInfo ep) => Resolve(ep.Id, ep.FriendlyName);

    public string Resolve(string endpointId, string fallback)
    {
        var alias = _settings.Get<string>(KeyPrefix + endpointId);
        if (!string.IsNullOrWhiteSpace(alias)) return alias.Trim();
        return ShortenName(fallback);
    }

    /// <summary>All endpoint IDs that currently have a non-empty alias persisted.</summary>
    public IEnumerable<string> KnownAliasedIds
    {
        get
        {
            foreach (var key in _settings.Keys)
            {
                if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                var value = _settings.Get<string>(key);
                if (string.IsNullOrWhiteSpace(value)) continue;
                yield return key[KeyPrefix.Length..];
            }
        }
    }

    /// <summary>Notifies listeners that aliases may have changed. Call after Save.</summary>
    public void Reload() => Changed?.Invoke();

    /// <summary>
    /// Removes any <c>alias:*</c> key whose value is empty or whitespace, so the
    /// settings file doesn't accumulate cleared entries. Saves if anything changed.
    /// </summary>
    public void CleanupEmpty()
    {
        var toRemove = new List<string>();
        foreach (var key in _settings.Keys)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
            var value = _settings.Get<string>(key);
            if (string.IsNullOrWhiteSpace(value)) toRemove.Add(key);
        }
        if (toRemove.Count == 0) return;
        foreach (var key in toRemove) _settings.Remove(key);
        _settings.Save();
    }

    /// <summary>Friendly-name fallback shortening — same heuristic as the old folder code.</summary>
    private static string ShortenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var paren = name.IndexOf('(');
        return paren > 1 ? name[..paren].TrimEnd() : name;
    }
}
