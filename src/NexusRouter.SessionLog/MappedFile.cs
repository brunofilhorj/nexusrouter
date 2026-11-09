using System.IO.MemoryMappedFiles;

namespace NexusRouter.SessionLog;

/// <summary>
/// Fixed-size file mapped for a single writer and concurrent readers.
/// </summary>
internal sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private int _disposed;

    /// <summary>
    /// Creates or opens <paramref name="path"/> at exactly <paramref name="length"/> bytes.
    /// </summary>
    /// <param name="path">Absolute file path inside the session-log root.</param>
    /// <param name="length">Pre-allocated size.</param>
    public MappedFile(string path, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (stream.Length != length)
            {
                stream.SetLength(length);
            }
        }

        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, length, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        if (pointer is null)
        {
            throw new IOException($"The mapped view of '{path}' has no address.");
        }

        _pointer = pointer;
        Length = length;
    }

    /// <summary>
    /// Size of the mapping.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Base address. Valid until <see cref="Dispose"/>.
    /// </summary>
    public byte* Pointer => _pointer;

    /// <summary>
    /// Faults every page so a later append does not take a page fault.
    /// </summary>
    public void Touch()
    {
        byte* pointer = _pointer;
        for (long offset = 0; offset < Length; offset += 4096)
        {
            Volatile.Write(ref pointer[offset], Volatile.Read(ref pointer[offset]));
        }

        if (Length > 0)
        {
            Volatile.Write(ref pointer[Length - 1], Volatile.Read(ref pointer[Length - 1]));
        }
    }

    /// <summary>
    /// Releases the view and the mapping.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
        _view.Dispose();
        _file.Dispose();
    }
}
