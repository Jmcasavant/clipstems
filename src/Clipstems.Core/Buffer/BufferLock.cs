namespace Clipstems.Core.Buffer;

/// <summary>
/// Exclusive ownership of a buffer folder across processes, so the tray app
/// and <c>clipstems watch</c> never both record into it or both attach the same
/// clip. An open file handle, so a crashed owner releases it automatically.
/// </summary>
public sealed class BufferLock : IDisposable
{
    private readonly FileStream _handle;

    private BufferLock(FileStream handle) => _handle = handle;

    /// <summary>Null when another process holds the folder.</summary>
    public static BufferLock? TryAcquire(string bufferRoot)
    {
        Directory.CreateDirectory(bufferRoot);
        try
        {
            var handle = new FileStream(Path.Combine(bufferRoot, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return new BufferLock(handle);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}
