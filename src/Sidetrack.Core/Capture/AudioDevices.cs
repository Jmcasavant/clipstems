using System.Runtime.InteropServices;

namespace Sidetrack.Core.Capture;

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
    /// The default device for <paramref name="kind"/>, or the first active one
    /// whose name contains <paramref name="nameContains"/> (case-insensitive).
    /// </summary>
    internal static IMMDevice Resolve(DeviceKind kind, string? nameContains)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var flow = kind == DeviceKind.Output ? EDataFlow.Render : EDataFlow.Capture;
        if (string.IsNullOrEmpty(nameContains))
        {
            Wasapi.Check(enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device), $"default {kind} device");
            return device;
        }
        var match = List().FirstOrDefault(d => d.Kind == kind && d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No active {kind.ToString().ToLowerInvariant()} device matches \"{nameContains}\".");
        Wasapi.Check(enumerator.GetDevice(match.Id, out var found), $"GetDevice({match.Name})");
        return found;
    }

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
            Wasapi.PropVariantClear(ref value);
        }
    }
}
