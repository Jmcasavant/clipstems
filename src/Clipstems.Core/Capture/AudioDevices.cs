using System.Runtime.InteropServices;

namespace Clipstems.Core.Capture;

public enum DeviceKind { Output, Input }

public sealed record AudioDevice(string Id, string Name, DeviceKind Kind, bool IsDefault);

/// <summary>Lists and resolves active audio endpoints.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> List()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var result = new List<AudioDevice>();
        foreach (var (flow, kind) in new[] { (EDataFlow.Render, DeviceKind.Output), (EDataFlow.Capture, DeviceKind.Input) })
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var def) >= 0 && def.GetId(out var id) >= 0)
                defaultId = id;
            Wasapi.Check(enumerator.EnumAudioEndpoints(flow, Wasapi.DeviceStateActive, out var collection), "EnumAudioEndpoints");
            Wasapi.Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0 || device.GetId(out var deviceId) < 0) continue;
                result.Add(new AudioDevice(deviceId, FriendlyName(device) ?? deviceId, kind, deviceId == defaultId));
            }
        }
        return result;
    }

    /// <summary>
    /// The default device for <paramref name="kind"/>, else the active one whose
    /// endpoint id is <paramref name="idOrName"/> (what the settings window
    /// saves, so same-named devices stay distinct), else the first whose name
    /// contains it (what the CLI takes), case-insensitive.
    /// </summary>
    internal static IMMDevice Resolve(DeviceKind kind, string? idOrName)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var flow = kind == DeviceKind.Output ? EDataFlow.Render : EDataFlow.Capture;
        if (string.IsNullOrEmpty(idOrName))
        {
            Wasapi.Check(enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device), $"default {kind} device");
            return device;
        }
        var active = List().Where(d => d.Kind == kind).ToList();
        var match = active.FirstOrDefault(d => string.Equals(d.Id, idOrName, StringComparison.OrdinalIgnoreCase))
            ?? active.FirstOrDefault(d => d.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(IsEndpointId(idOrName)
                ? "the chosen device is not connected"
                : $"no connected {kind.ToString().ToLowerInvariant()} device matches \"{idOrName}\"");
        Wasapi.Check(enumerator.GetDevice(match.Id, out var found), $"GetDevice({match.Name})");
        return found;
    }

    /// <summary>Windows endpoint ids look like <c>{0.0.1.00000000}.{guid}</c>.</summary>
    public static bool IsEndpointId(string value) => value.StartsWith("{0.0.", StringComparison.Ordinal);

    internal static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) < 0) return null;
        var key = Wasapi.DeviceFriendlyName;
        if (store.GetValue(ref key, out var value) < 0) return null;
        try
        {
            return value.VarType == Wasapi.VtLpwstr ? Marshal.PtrToStringUni(value.Value) : null;
        }
        finally
        {
            _ = Wasapi.PropVariantClear(ref value); // nothing useful to do if freeing fails
        }
    }
}
