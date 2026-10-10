using System.Runtime.InteropServices;

namespace Sidetrack.Core.Capture;

/// <summary>
/// Raises <see cref="DefaultChanged"/> when the Windows default input or
/// output device changes, so captures that follow the default can move to
/// the new device instead of recording the old one (or nothing).
/// Construct on an MTA thread.
/// </summary>
public sealed class DefaultDeviceWatcher : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Client _client;

    public DefaultDeviceWatcher()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        _client = new Client(this);
        Wasapi.Check(_enumerator.RegisterEndpointNotificationCallback(_client), "RegisterEndpointNotificationCallback");
    }

    /// <summary>Raised on a Core Audio thread; handlers must not block.</summary>
    public event Action<DeviceKind>? DefaultChanged;

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        Marshal.ReleaseComObject(_enumerator);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Client(DefaultDeviceWatcher owner) : IMMNotificationClient
    {
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            // Windows reports each role separately; the console role is the one captures follow.
            if (role == ERole.Console && flow is EDataFlow.Render or EDataFlow.Capture)
                owner.DefaultChanged?.Invoke(flow == EDataFlow.Render ? DeviceKind.Output : DeviceKind.Input);
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
