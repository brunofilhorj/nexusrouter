using System.Collections.Concurrent;
using System.Text.Json;

namespace ClientProbe.Fix;

/// <summary>Sequence numbers of one FIX session, as the probe sees them.</summary>
/// <param name="LastOut">MsgSeqNum (tag 34) of the last message the probe sent. 0 before the first Logon.</param>
/// <param name="NextInExpected">MsgSeqNum the probe expects on the next message from the acceptor.</param>
public sealed record FixSeqState(int LastOut, int NextInExpected)
{
    /// <summary>State of a session that never logged on, or right after a reset.</summary>
    public static FixSeqState Initial { get; } = new(0, 1);
}

/// <summary>
/// Keeps <see cref="FixSeqState"/> per session key across reconnects, so a Logon after a drop
/// continues the sequence instead of restarting at 1. With a directory configured, the state also
/// survives a probe restart (one small JSON file per session, written via temp file + rename).
/// </summary>
public sealed class FixSeqStore
{
    private readonly ConcurrentDictionary<string, FixSeqState> _states = new(StringComparer.Ordinal);
    private readonly string? _directory;
    private readonly object _ioGate = new();

    /// <summary>Creates the store.</summary>
    /// <param name="directory">Folder for the JSON files. Null or empty keeps the state in memory only.</param>
    public FixSeqStore(string? directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>Session key: SenderCompID and TargetCompID, the pair the acceptor keys its state on.</summary>
    /// <param name="senderCompId">SenderCompID (tag 49) sent by the probe.</param>
    /// <param name="targetCompId">TargetCompID (tag 56) sent by the probe.</param>
    /// <returns>Key safe for a file name.</returns>
    public static string Key(string senderCompId, string targetCompId) =>
        $"{Sanitize(senderCompId)}-{Sanitize(targetCompId)}";

    /// <summary>Current state, loaded from disk the first time a key is used.</summary>
    /// <param name="key">Value from <see cref="Key"/>.</param>
    /// <returns>The state, or <see cref="FixSeqState.Initial"/>.</returns>
    public FixSeqState Get(string key) => _states.GetOrAdd(key, Load);

    /// <summary>Replaces the state in memory. Call <see cref="Flush"/> to write it to disk.</summary>
    /// <param name="key">Value from <see cref="Key"/>.</param>
    /// <param name="state">New state.</param>
    public void Set(string key, FixSeqState state) => _states[key] = state;

    /// <summary>Writes the in-memory state of <paramref name="key"/> to disk, when a directory is configured.</summary>
    /// <param name="key">Value from <see cref="Key"/>.</param>
    public void Flush(string key)
    {
        if (_directory is null || !_states.TryGetValue(key, out var state))
        {
            return;
        }

        lock (_ioGate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = PathFor(key);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persistence is best effort; the in-memory state still covers reconnects.
            }
        }
    }

    private FixSeqState Load(string key)
    {
        if (_directory is null)
        {
            return FixSeqState.Initial;
        }

        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return FixSeqState.Initial;
            }

            var state = JsonSerializer.Deserialize<FixSeqState>(File.ReadAllText(path));
            return state is { LastOut: >= 0, NextInExpected: >= 1 } ? state : FixSeqState.Initial;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return FixSeqState.Initial;
        }
    }

    private string PathFor(string key) => Path.Combine(_directory!, $"{key}.json");

    private static string Sanitize(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '_' or '.'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
