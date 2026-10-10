using System.Runtime.InteropServices;
using Sidetrack.Core.Audio;
using Sidetrack.Core.Buffer;

namespace Sidetrack.Core.Capture;

/// <summary>
/// One initialized WASAPI capture stream in <see cref="PcmFormat"/>. Mic and
/// system output use a device client; an app uses process loopback on its
/// process tree. Create and run it on an MTA thread.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private const long BufferDuration = 2_000_000; // 200 ms
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _ready = new(false);

    private CaptureSession(IAudioClient client, bool loopback, string description)
    {
        _client = client;
        Description = description;
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = PcmFormat.Channels,
            SamplesPerSec = PcmFormat.SampleRate,
            AvgBytesPerSec = PcmFormat.SampleRate * PcmFormat.BlockAlign,
            BlockAlign = PcmFormat.BlockAlign,
            BitsPerSample = PcmFormat.BytesPerSample * 8,
        };
        uint flags = Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality;
        if (loopback) flags |= Wasapi.StreamFlagsLoopback;
        Wasapi.Check(client.Initialize(Wasapi.ShareModeShared, flags, BufferDuration, 0, ref format, IntPtr.Zero), "IAudioClient.Initialize");
        Wasapi.Check(client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
        var iid = Wasapi.IidAudioCaptureClient;
        Wasapi.Check(client.GetService(ref iid, out var service), "IAudioClient.GetService");
        _capture = (IAudioCaptureClient)service;
    }

    public string Description { get; }

    /// <summary>The process whose tree is captured, for app sources.</summary>
    public uint? ProcessId { get; private init; }

    /// <summary>
    /// Opens <paramref name="source"/>. Returns null when an app or game
    /// source has no running process (nothing to capture yet), which is not an error.
    /// </summary>
    public static CaptureSession? Open(CaptureSource source)
    {
        switch (source.Kind)
        {
            case SourceKind.Microphone:
            case SourceKind.SystemOutput:
            {
                var kind = source.Kind == SourceKind.Microphone ? DeviceKind.Input : DeviceKind.Output;
                var device = AudioDevices.Resolve(kind, source.Device);
                var iid = Wasapi.IidAudioClient;
                Wasapi.Check(device.Activate(ref iid, Wasapi.ClsctxAll, IntPtr.Zero, out var client), "IMMDevice.Activate");
                var name = AudioDevices.FriendlyName(device) ?? "unknown device";
                return new CaptureSession((IAudioClient)client, loopback: kind == DeviceKind.Output,
                    source.Kind == SourceKind.Microphone ? $"mic: {name}" : $"system output: {name}");
            }
            case SourceKind.Application:
            {
                (uint Pid, string Exe)? target = source.ProcessId is { } fixedPid
                    ? ProcessTree.IsAlive(fixedPid) ? (fixedPid, $"pid {fixedPid}") : null
                    : ProcessTree.FindRoot(source.Apps);
                if (target is not { } t) return null;
                return new CaptureSession(ActivateProcessLoopback(t.Pid, include: true), loopback: true, $"{t.Exe} (pid {t.Pid})")
                {
                    ProcessId = t.Pid,
                };
            }
            case SourceKind.SteamGame:
            {
                var game = Steam.SteamGames.Running(Steam.SteamInstall.FindSteamPath());
                if (game?.InstallDirectory is not { } dir || ProcessTree.FindRootUnder(dir) is not { } root) return null;
                return new CaptureSession(ActivateProcessLoopback(root.Pid, include: true), loopback: true, $"{game.Name}: {root.Exe} (pid {root.Pid})")
                {
                    ProcessId = root.Pid,
                };
            }
            case SourceKind.AllExceptSelf:
                return new CaptureSession(ActivateProcessLoopback((uint)Environment.ProcessId, include: false), loopback: true,
                    "all apps except Sidetrack");
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    /// <summary>
    /// True when this Windows build supports process loopback, probed by
    /// activating it against our own process.
    /// </summary>
    public static bool ProcessLoopbackSupported(out string? error)
    {
        try
        {
            var client = ActivateProcessLoopback((uint)Environment.ProcessId, include: true);
            Marshal.ReleaseComObject(client);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Pumps packets into <paramref name="writer"/> until cancelled or
    /// <paramref name="keepGoing"/> returns false (checked about every 100 ms).
    /// </summary>
    public unsafe void Run(SegmentWriter writer, Func<bool> keepGoing, CancellationToken cancel)
    {
        var clock = QpcClock.Anchor();
        long reanchorAt = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 30;
        Wasapi.Check(_client.Start(), "IAudioClient.Start");
        try
        {
            while (!cancel.IsCancellationRequested && keepGoing())
            {
                _ready.WaitOne(100);
                while (true)
                {
                    Wasapi.Check(_capture.GetNextPacketSize(out var pending), "GetNextPacketSize");
                    if (pending == 0) break;
                    int hr = _capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);
                    Wasapi.Check(hr, "IAudioCaptureClient.GetBuffer");
                    if (hr == Wasapi.AudclntSBufferEmpty) break;
                    try
                    {
                        var pcm = new ReadOnlySpan<byte>((void*)data, (int)frames * PcmFormat.BlockAlign);
                        bool timestampValid = (flags & Wasapi.BufferFlagsTimestampError) == 0;
                        writer.Write(pcm, clock.ToUtcTicks((long)qpc), (flags & Wasapi.BufferFlagsSilent) != 0, timestampValid);
                    }
                    finally
                    {
                        _capture.ReleaseBuffer(frames);
                    }
                }
                if (DateTime.UtcNow.Ticks >= reanchorAt)
                {
                    clock = QpcClock.Anchor();
                    reanchorAt = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 30;
                }
            }
        }
        finally
        {
            _client.Stop();
            writer.CloseCurrent();
        }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        _ready.Dispose();
    }

    private static IAudioClient ActivateProcessLoopback(uint processId, bool include)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; { TargetProcessId; ProcessLoopbackMode } }
        IntPtr parameters = Marshal.AllocHGlobal(12);
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.WriteInt32(parameters, 0, Wasapi.ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, (int)processId);
            Marshal.WriteInt32(parameters, 8, include ? Wasapi.ProcessLoopbackIncludeTree : Wasapi.ProcessLoopbackExcludeTree);
            // PROPVARIANT VT_BLOB { cbSize at +8, pBlobData at +16 }
            Marshal.StructureToPtr(new PropVariant { VarType = Wasapi.VtBlob, Value = 12, Value2 = parameters }, variant, false);

            var handler = new ActivationHandler();
            var iid = Wasapi.IidAudioClient;
            Wasapi.Check(Wasapi.ActivateAudioInterfaceAsync(Wasapi.ProcessLoopbackDevice, ref iid, variant, handler, out var operation),
                "ActivateAudioInterfaceAsync");
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Process loopback activation did not complete within 5 s.");
            Wasapi.Check(operation.GetActivateResult(out var activateHr, out var client), "GetActivateResult");
            Wasapi.Check(activateHr, "Process loopback activation");
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new();

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Completed.Set();
            return 0;
        }
    }
}
