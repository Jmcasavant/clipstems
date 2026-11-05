using System.IO;
using System.IO.Pipes;

namespace Clipstems.App.Services;

/// <summary>
/// One Clipstems per Windows session. A second launch signals the first
/// over a named pipe (so double-clicking the shortcut opens Settings) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Named pipes are machine-wide while the Local\ mutex is per session, so the session id keeps
    // the same user signed in twice (fast user switching, RDP) from signalling the wrong copy.
    private static readonly string Name = $"Clipstems-{Environment.UserName}-{SessionId()}";

    private static int SessionId()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId;
    }
    private readonly Mutex _mutex;
    private CancellationTokenSource? _listening;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{Name}", out bool created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SignalExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(TimeSpan.FromSeconds(2));
            pipe.WriteByte(1);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a background thread) each time another launch signals.</summary>
    public void Listen(Action onActivate)
    {
        _listening = new CancellationTokenSource();
        var cancel = _listening.Token;
        Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(cancel);
                    if (pipe.ReadByte() == 1) onActivate();
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { await Task.Delay(500, CancellationToken.None); }
            }
        });
    }

    public void Dispose()
    {
        _listening?.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
