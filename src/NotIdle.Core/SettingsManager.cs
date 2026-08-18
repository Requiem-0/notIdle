using System.Threading;

namespace NotIdle.Core;

/// <summary>
/// Owns the one authoritative <see cref="Settings"/> instance: loads it at
/// startup, normalizes and persists every change, and notifies subscribers.
///
/// Callers always get a defensive copy, so no one can mutate live configuration
/// out from under the controller thread.
/// </summary>
public sealed class SettingsManager
{
    private readonly ISettingsStore _store;
    private readonly Lock _gate = new();
    private Settings _current;

    /// <summary>Raised after a successful save, with a fresh snapshot. May fire on any thread.</summary>
    public event Action<Settings>? Changed;

    public SettingsManager(ISettingsStore store)
    {
        _store = store;
        _current = store.Load();
        _current.Normalize();
    }

    /// <summary>A private copy of the current settings.</summary>
    public Settings Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    /// <summary>Normalize, persist, and publish a whole new settings object.</summary>
    public Settings Apply(Settings settings)
    {
        Settings snapshot = settings.Clone();
        snapshot.Normalize();

        lock (_gate) _current = snapshot;

        // Persisting must never take down the app — an unwritable %APPDATA% just
        // means the change is session-only.
        try { _store.Save(snapshot); }
        catch { /* best effort */ }

        Changed?.Invoke(snapshot.Clone());
        return snapshot;
    }

    /// <summary>Read-modify-write helper for single-field toggles.</summary>
    public Settings Update(Action<Settings> mutate)
    {
        Settings draft = Current;
        mutate(draft);
        return Apply(draft);
    }
}
