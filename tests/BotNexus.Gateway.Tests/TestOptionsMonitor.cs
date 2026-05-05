using Microsoft.Extensions.Options;

internal sealed class TestOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
{
    private readonly List<Action<TOptions, string?>> _listeners = [];

    public TOptions CurrentValue { get; private set; } = currentValue;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        _listeners.Add(listener);
        return new ListenerRegistration(() => _listeners.Remove(listener));
    }

    /// <summary>Trigger a change notification for testing.</summary>
    public void RaiseChanged(TOptions newValue)
    {
        CurrentValue = newValue;
        foreach (var listener in _listeners.ToArray())
            listener(newValue, null);
    }

    private sealed class ListenerRegistration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
