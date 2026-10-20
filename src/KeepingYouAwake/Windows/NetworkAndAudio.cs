using System.Runtime.InteropServices;
using KeepingYouAwake.Core.Platform;
using static KeepingYouAwake.Windows.NativeMethods;

namespace KeepingYouAwake.Windows;

internal enum WiFiReadStatus
{
    Connected,
    NotConnected,
    /// <summary>Windows 11 24H2+ hides SSIDs from apps without location access.</summary>
    AccessDenied,
    /// <summary>No Wi-Fi adapter, or the WLAN service isn't running.</summary>
    Unavailable,
}

internal readonly record struct WiFiReading(WiFiReadStatus Status, IReadOnlyList<string> Ssids)
{
    /// <summary>The reading for the connected interfaces' SSIDs; none means not connected or hidden from this app.</summary>
    public static WiFiReading From(IReadOnlyList<string> ssids, bool accessDenied) =>
        ssids.Count > 0 ? new(WiFiReadStatus.Connected, ssids)
        : new(accessDenied ? WiFiReadStatus.AccessDenied : WiFiReadStatus.NotConnected, []);
}

/// <summary>The Native Wifi calls the probe needs, with native buffers already copied into managed memory.</summary>
internal interface IWlanNative
{
    /// <summary>Opens a client handle; false when the WLAN service is unavailable.</summary>
    bool TryOpen(out IntPtr client);

    void Close(IntPtr client);

    /// <summary>The <c>WLAN_INTERFACE_INFO_LIST</c>, or null when enumeration fails.</summary>
    byte[]? EnumerateInterfaces(IntPtr client);

    /// <summary>The Win32 result and, on success, the <c>WLAN_CONNECTION_ATTRIBUTES</c> of an interface.</summary>
    (int Result, byte[]? Attributes) QueryCurrentConnection(IntPtr client, Guid interfaceGuid);
}

/// <summary>Reads the joined Wi-Fi networks through the Native Wifi API.</summary>
internal sealed class WlanWiFiProbe(IWlanNative? native = null) : IWiFiProbe
{
    private static readonly WiFiReading Unavailable = new(WiFiReadStatus.Unavailable, []);
    private readonly IWlanNative native = native ?? new WlanNative();

    public IReadOnlyList<string> CurrentSsids() => ReadNow().Ssids;

    /// <summary>Reads with the real API.</summary>
    public static WiFiReading Read() => new WlanWiFiProbe().ReadNow();

    public WiFiReading ReadNow()
    {
        try
        {
            if (!native.TryOpen(out var client)) { return Unavailable; }
            try { return ReadInterfaces(client); }
            finally { native.Close(client); }
        }
        catch (DllNotFoundException)
        {
            return Unavailable; // Windows Server without the WLAN feature
        }
    }

    private WiFiReading ReadInterfaces(IntPtr client)
    {
        if (native.EnumerateInterfaces(client) is not { } list) { return Unavailable; }
        var interfaces = WindowsDecoding.ParseWlanInterfaceList(list);
        if (interfaces.Count == 0) { return Unavailable; }

        var ssids = new List<string>();
        var denied = false;
        foreach (var (guid, _) in interfaces.Where(i => i.Connected))
        {
            var (result, attributes) = native.QueryCurrentConnection(client, guid);
            var ssid = result == 0 && attributes is not null ? WindowsDecoding.ParseConnectionSsid(attributes) : null;
            // Access denied, or a redacted (empty) SSID: Windows hides the name from this app.
            denied |= result == ErrorAccessDenied || (result == 0 && ssid is null);
            if (ssid is not null) { ssids.Add(ssid); }
        }
        return WiFiReading.From(ssids, denied);
    }
}

/// <summary><see cref="IWlanNative"/> over wlanapi.dll.</summary>
internal sealed class WlanNative : IWlanNative
{
    public bool TryOpen(out IntPtr client) => WlanOpenHandle(2, IntPtr.Zero, out _, out client) == 0;

    public void Close(IntPtr client) => WlanCloseHandle(client, IntPtr.Zero);

    public byte[]? EnumerateInterfaces(IntPtr client)
    {
        if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0) { return null; }
        try
        {
            var count = Marshal.ReadInt32(list);
            return Copy(list, WindowsDecoding.WlanInterfaceListHeaderSize + count * WindowsDecoding.WlanInterfaceInfoSize);
        }
        finally { WlanFreeMemory(list); }
    }

    public (int Result, byte[]? Attributes) QueryCurrentConnection(IntPtr client, Guid interfaceGuid)
    {
        var result = WlanQueryInterface(client, ref interfaceGuid, WlanIntfOpcodeCurrentConnection, IntPtr.Zero, out var size, out var data, out _);
        if (result != 0) { return (result, null); }
        try { return (0, Copy(data, (int)size)); }
        finally { WlanFreeMemory(data); }
    }

    private static byte[] Copy(IntPtr source, int length)
    {
        var bytes = new byte[Math.Max(0, length)];
        Marshal.Copy(source, bytes, 0, bytes.Length);
        return bytes;
    }
}

/// <summary>The default playback endpoint's identifying properties.</summary>
internal sealed record AudioEndpointInfo(string? EnumeratorName, uint? FormFactor);

internal interface IAudioEndpointReader
{
    /// <summary>The default playback endpoint; null when there is none. Throws <see cref="COMException"/> on failure.</summary>
    AudioEndpointInfo? ReadDefaultPlayback();
}

/// <summary>Classifies the default playback device.</summary>
internal sealed class CoreAudioProbe(IAudioEndpointReader? reader = null) : IAudioOutputProbe
{
    private readonly IAudioEndpointReader reader = reader ?? new CoreAudioEndpointReader();

    public bool? IsDefaultOutputExternal()
    {
        try
        {
            return reader.ReadDefaultPlayback() is { } endpoint
                && WindowsDecoding.IsExternalAudioEndpoint(endpoint.EnumeratorName, endpoint.FormFactor);
        }
        catch (COMException)
        {
            return null; // the state can't be read
        }
    }
}

/// <summary><see cref="IAudioEndpointReader"/> over Core Audio (MMDevice API).</summary>
internal sealed class CoreAudioEndpointReader : IAudioEndpointReader
{
    private const int ElementNotFound = unchecked((int)0x80070490); // E_NOTFOUND: no endpoint of that kind

    public AudioEndpointInfo? ReadDefaultPlayback()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var result = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device);
            if (result == ElementNotFound || (result == 0 && device is null)) { return null; }
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(device!.OpenPropertyStore(0, out store));
            return new AudioEndpointInfo(
                ReadString(store!, PropertyKeys.DeviceEnumeratorName),
                ReadUInt(store!, PropertyKeys.AudioEndpointFormFactor));
        }
        finally
        {
            Release(store);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null) { Marshal.ReleaseComObject(comObject); }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) { return null; }
        try
        {
            return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally { PropVariantClear(ref value); }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) { return null; }
        try
        {
            return value.Type == 19 /* VT_UI4 */ ? value.UInt : null;
        }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum DataFlow { Render = 0 }
    private enum Role { Multimedia = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    private static class PropertyKeys
    {
        public static readonly PropertyKey AudioEndpointFormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
        public static readonly PropertyKey DeviceEnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] public IntPtr Padding;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
}
