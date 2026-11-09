namespace NexusRouter.SessionLog;

/// <summary>
/// Path checks for names that come from the network.
/// </summary>
internal static class SessionLogPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Accepts 1 to 64 characters from <c>[A-Za-z0-9_-]</c>, excluding Windows device names.
    /// </summary>
    /// <param name="key">Session, dispatcher, or client key.</param>
    /// <param name="paramName">Parameter name for the exception.</param>
    public static void EnsureKey(string key, string paramName)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64 || Reserved.Contains(key))
        {
            throw new ArgumentException("The session-log key is empty, too long, or reserved.", paramName);
        }

        foreach (char ch in key)
        {
            if (ch is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                throw new ArgumentException("The session-log key may contain only letters, digits, '_' and '-'.", paramName);
            }
        }
    }

    /// <summary>
    /// Combines <paramref name="parts"/> under <paramref name="root"/> and rejects a path that escapes the root.
    /// </summary>
    /// <param name="root">Session-log directory.</param>
    /// <param name="parts">Relative segments. Each one must already be a safe key or a generated file name.</param>
    /// <returns>The absolute path.</returns>
    public static string Under(string root, params string[] parts)
    {
        string fullRoot = Path.GetFullPath(root);
        string combined = fullRoot;
        foreach (string part in parts)
        {
            if (part.Contains("..", StringComparison.Ordinal)
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("A session-log path segment is not a file name.");
            }

            combined = Path.Combine(combined, part);
        }

        string full = Path.GetFullPath(combined);
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.Equals(fullRoot, StringComparison.Ordinal) && !full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The session-log path escapes the root directory.");
        }

        return full;
    }

    /// <summary>
    /// Reads <c>yyyyMMdd</c> from a day folder or a <c>.cidx</c> file name.
    /// </summary>
    /// <param name="name">Folder or file name.</param>
    /// <param name="day">The trading day encoded in the name.</param>
    /// <returns>False when the name is not a day folder or a client index.</returns>
    public static bool TryParseTradingDay(string name, out DateOnly day)
    {
        ReadOnlySpan<char> span = name;
        if (span.EndsWith(".cidx", StringComparison.Ordinal))
        {
            span = span[..^5];
        }

        if (span.Length < 10 || span[8] != '-')
        {
            day = default;
            return false;
        }

        return DateOnly.TryParseExact(span[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day);
    }
}
