using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sidetrack.Core.Capture;

/// <summary>An app that has an audio session on an output device.</summary>
/// <param name="IsActive">True when it is producing sound right now.</param>
public sealed record AudioApp(string Exe, string DisplayName, bool IsActive);

/// <summary>Lists apps with audio sessions, for picking what a track records.</summary>
public static class AudioSessions
{
    private static readonly Guid IidSessionManager2 = typeof(IAudioSessionManager2).GUID;

    /// <summary>
    /// Apps with a session on any active output device, active ones first.
    /// Call from an MTA thread. Sidetrack itself and system sounds are left out.
    /// </summary>
    public static IReadOnlyList<AudioApp> List()
    {
        var processes = ProcessTree.Snapshot().ToDictionary(p => p.Pid, p => p.Exe);
        var byExe = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Wasapi.Check(enumerator.EnumAudioEndpoints(EDataFlow.Render, Wasapi.DeviceStateActive, out var devices), "EnumAudioEndpoints");
        Wasapi.Check(devices.GetCount(out var count), "IMMDeviceCollection.GetCount");
        for (uint d = 0; d < count; d++)
        {
            if (devices.Item(d, out var device) < 0) continue;
            var iid = IidSessionManager2;
            if (device.Activate(ref iid, Wasapi.ClsctxAll, IntPtr.Zero, out var obj) < 0) continue;
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var sessions) < 0 || sessions.GetCount(out var n) < 0) continue;
            for (int i = 0; i < n; i++)
            {
                if (sessions.GetSession(i, out var session) < 0) continue;
                try
                {
                    if (session.IsSystemSoundsSession() == 0) continue; // S_OK means it is the system sounds session
                    if (session.GetProcessId(out var pid) < 0 || pid == 0 || pid == Environment.ProcessId) continue;
                    if (!processes.TryGetValue(pid, out var exe)) continue;
                    session.GetState(out var state);
                    bool active = state == 1; // AudioSessionStateActive
                    if (byExe.TryGetValue(exe, out var known) && (known.IsActive || !active)) continue;
                    byExe[exe] = new AudioApp(exe, DisplayName(pid, exe), active);
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        return byExe.Values.OrderByDescending(a => a.IsActive).ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string DisplayName(uint pid, string exe)
    {
        try
        {
            if (ProcessTree.ImagePath(pid) is { } path &&
                FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                return description;
        }
        catch (FileNotFoundException) { }
        return Path.GetFileNameWithoutExtension(exe);
    }
}
