using System.Collections.Concurrent;

namespace par.Services;

/// <summary>
/// Briefly holds an already-parsed log so another page can open it without re-uploading.
/// Entries are keyed by an unguessable id passed in the URL and expire automatically.
/// </summary>
public sealed class LogHandoffService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, (string FileName, GpuMonLog Log, string? PicoName, PicoLog? Pico, DateTime Expires)> _entries = new();

    public Guid Offer(string fileName, GpuMonLog log, string? picoName = null, PicoLog? pico = null)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _entries.Where(entry => entry.Value.Expires < now).Select(entry => entry.Key).ToArray())
            _entries.TryRemove(expired, out _);
        var id = Guid.NewGuid();
        _entries[id] = (fileName, log, picoName, pico, now + Lifetime);
        return id;
    }

    public (string FileName, GpuMonLog Log, string? PicoName, PicoLog? Pico)? Get(Guid id)
        => _entries.TryGetValue(id, out var entry) && entry.Expires >= DateTime.UtcNow ? (entry.FileName, entry.Log, entry.PicoName, entry.Pico) : null;
}
