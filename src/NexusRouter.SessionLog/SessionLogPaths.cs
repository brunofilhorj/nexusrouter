using NexusRouter.Domain.Models;

namespace NexusRouter.SessionLog;

/// <summary>
/// Path checks for names that come from the network.
/// </summary>
internal static class SessionLogPaths
{
    /// <summary>
    /// Accepts 1 to 64 characters from <c>[A-Za-z0-9_-]</c>, excluding Windows device names.
    /// The rule lives in <see cref="ClientKey.IsValid"/>.
    /// </summary>
    /// <param name="key">Session, dispatcher, or client key.</param>
    /// <param name="paramName">Parameter name for the exception.</param>
    public static void EnsureKey(string key, string paramName)
    {
        if (!ClientKey.IsValid(key))
        {
            throw new ArgumentException(
                "The session-log key must have 1 to 64 characters from [A-Za-z0-9_-] and must not be a reserved name.",
                paramName);
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
