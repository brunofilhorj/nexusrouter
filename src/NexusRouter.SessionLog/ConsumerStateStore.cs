using System.Globalization;
using System.Text.Json;

using NexusRouter.Domain.Abstractions;

namespace NexusRouter.SessionLog;

/// <summary>
/// Small JSON file of outbound sequence, expected inbound sequence, epoch, and cursors.
/// </summary>
internal sealed class ConsumerStateStore : IConsumerState
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly Func<long>? _readNextOut;
    private readonly Action? _changed;
    private readonly long[] _expected = new long[256];
    private readonly long[] _cursors = new long[256];
    private long _nextOut = 1;
    private long _nextInExpected = 1;
    private long _epoch;

    /// <summary>
    /// Creates a store. <paramref name="readNextOut"/> supplies the live counter when an index owns it.
    /// </summary>
    /// <param name="path">Absolute path of <c>state.json</c> or <c>cursor.json</c>.</param>
    /// <param name="readNextOut">Live next-outbound reader. Null for a broker cursor.</param>
    /// <param name="changed">Wakes the per-message flusher. Null when flush is periodic.</param>
    public ConsumerStateStore(string path, Func<long>? readNextOut, Action? changed)
    {
        _path = path;
        _readNextOut = readNextOut;
        _changed = changed;
    }

    /// <inheritdoc />
    public long NextOut => _readNextOut?.Invoke() ?? Volatile.Read(ref _nextOut);

    /// <inheritdoc />
    public long NextInExpected
    {
        get => Volatile.Read(ref _nextInExpected);
        set
        {
            Volatile.Write(ref _nextInExpected, value);
            _changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public long Epoch
    {
        get => Volatile.Read(ref _epoch);
        set
        {
            Volatile.Write(ref _epoch, value);
            _changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public long GetExpected(ushort stream) => Volatile.Read(ref _expected[stream]);

    /// <inheritdoc />
    public void SetExpected(ushort stream, long inSeq)
    {
        Volatile.Write(ref _expected[stream], inSeq);
        _changed?.Invoke();
    }

    /// <inheritdoc />
    public long Cursor(ushort stream) => Volatile.Read(ref _cursors[stream]);

    /// <inheritdoc />
    public void SetCursor(ushort stream, long value)
    {
        Volatile.Write(ref _cursors[stream], value);
        _changed?.Invoke();
    }

    /// <summary>
    /// Loads a file when it exists.
    /// </summary>
    /// <returns>The document, or null when the file is absent.</returns>
    public StateDocument? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        StateDocument? document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(_path), Json);
        if (document is null)
        {
            throw new InvalidOperationException($"Consumer state '{_path}' is unreadable.");
        }

        _nextOut = document.NextOut < 1 ? 1 : document.NextOut;
        _nextInExpected = document.NextInExpected < 1 ? 1 : document.NextInExpected;
        _epoch = document.Epoch;
        Copy(document.Expected, _expected);
        Copy(document.Cursors, _cursors);
        return document;
    }

    /// <summary>
    /// Writes the live values via a temporary file and an atomic rename.
    /// </summary>
    public void Flush()
    {
        var document = new StateDocument
        {
            NextOut = NextOut,
            NextInExpected = NextInExpected,
            Epoch = Epoch,
            UpdatedAt = DateTimeOffset.UtcNow,
            Expected = Dump(_expected),
            Cursors = Dump(_cursors),
        };
        string json = JsonSerializer.Serialize(document, Json);
        string temporary = _path + ".tmp";
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Copy(Dictionary<string, long>? source, long[] target)
    {
        if (source is null)
        {
            return;
        }

        foreach ((string key, long value) in source)
        {
            if (ushort.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out ushort stream))
            {
                target[stream] = value;
            }
        }
    }

    private static Dictionary<string, long> Dump(long[] values)
    {
        var map = new Dictionary<string, long>();
        for (int i = 0; i < values.Length; i++)
        {
            long value = Volatile.Read(ref values[i]);
            if (value != 0)
            {
                map[i.ToString(CultureInfo.InvariantCulture)] = value;
            }
        }

        return map;
    }

    /// <summary>
    /// JSON shape of <c>state.json</c> and <c>cursor.json</c>.
    /// </summary>
    internal sealed class StateDocument
    {
        /// <summary>Next outbound sequence.</summary>
        public long NextOut { get; set; } = 1;

        /// <summary>Next expected inbound sequence.</summary>
        public long NextInExpected { get; set; } = 1;

        /// <summary>Epoch.</summary>
        public long Epoch { get; set; }

        /// <summary>When the flusher wrote the file.</summary>
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>Expected ingress sequence per stream.</summary>
        public Dictionary<string, long>? Expected { get; set; }

        /// <summary>Broker cursor per stream.</summary>
        public Dictionary<string, long>? Cursors { get; set; }
    }
}
