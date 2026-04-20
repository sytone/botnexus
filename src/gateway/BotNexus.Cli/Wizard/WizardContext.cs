namespace BotNexus.Cli.Wizard;

/// <summary>
/// Carries data between wizard steps as a typed key-value bag.
/// </summary>
public sealed class WizardContext
{
    private readonly Dictionary<string, object> _data = new(StringComparer.OrdinalIgnoreCase);

    public void Set<T>(string key, T value) where T : notnull
    {
        _data[key] = value;
    }

    public T Get<T>(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Wizard context does not contain key '{key}'.");

        return (T)value;
    }

    public bool TryGet<T>(string key, out T value)
    {
        if (_data.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public bool Has(string key) => _data.ContainsKey(key);

    public void Remove(string key) => _data.Remove(key);
}
