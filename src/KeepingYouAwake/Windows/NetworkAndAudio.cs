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

internal readonly record struct WiFiReading(WiFiReadStatus Status, IReadOnlyList<string> Ssids);

/// <summary>Reads the joined Wi-Fi networks through the Native Wifi API.</summary>
internal sealed class WlanWiFiProbe : IWiFiProbe
{
    public IReadOnlyList<string> CurrentSsids() => Read().Ssids;

    public static WiFiReading Read()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) { return Unavailable; }
            try { return ReadInterfaces(client); }
            finally { WlanCloseHandle(client, IntPtr.Zero); }
        }
        catch (DllNotFoundException)
        {
            return Unavailable; // Windows Server without the WLAN feature
        }
    }

    private static readonly WiFiReading Unavailable = new(WiFiReadStatus.Unavailable, []);

    private static WiFiReading ReadInterfaces(IntPtr client)
    {
        if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0) { return Unavailable; }
        try
        {
            var count = Marshal.ReadInt32(list);
            if (count == 0) { return Unavailable; }
            var ssids = new List<string>();
            var denied = false;
            for (var i = 0; i < count; i++)
            {
                var info = list + 8 + i * WlanInterfaceInfoSize;
                if (Marshal.ReadInt32(info + 16 + 512) != WlanInterfaceStateConnected) { continue; }
                var guid = Marshal.PtrToStructure<Guid>(info);
                var ssid = QuerySsid(client, guid, out var accessDenied);
                denied |= accessDenied;
                if (ssid is not null) { ssids.Add(ssid); }
            }
            if (ssids.Count > 0) { return new(WiFiReadStatus.Connected, ssids); }
            return new(denied ? WiFiReadStatus.AccessDenied : WiFiReadStatus.NotConnected, []);
        }
        finally { WlanFreeMemory(list); }
    }

    /// <summary>The SSID of a connected interface; null with <paramref name="accessDenied"/> when Windows hides it.</summary>
    private static string? QuerySsid(IntPtr client, Guid interfaceGuid, out bool accessDenied)
    {
        var result = WlanQueryInterface(client, ref interfaceGuid, WlanIntfOpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _);
        accessDenied = result == ErrorAccessDenied;
        if (result != 0) { return null; }
        try
        {
            var length = Math.Clamp(Marshal.ReadInt32(data + ConnectionSsidOffset), 0, 32);
            var bytes = new byte[length];
            Marshal.Copy(data + ConnectionSsidOffset + 4, bytes, 0, length);
            var ssid = WindowsDecoding.DecodeSsid(bytes);
            accessDenied |= ssid is null; // redacted
            return ssid;
        }
        finally { WlanFreeMemory(data); }
    }
}

/// <summary>Classifies the default playback device through Core Audio (MMDevice API).</summary>
internal sealed class CoreAudioProbe : IAudioOutputProbe
{
    public bool? IsDefaultOutputExternal()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device) != 0 || device is null)
            {
                return false; // no playback device at all
            }
            if (device.OpenPropertyStore(0, out store) != 0 || store is null) { return null; }
            return WindowsDecoding.IsExternalAudioEndpoint(
                ReadString(store, PropertyKeys.DeviceEnumeratorName),
                ReadUInt(store, PropertyKeys.AudioEndpointFormFactor));
        }
        catch (COMException)
        {
            return null;
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
