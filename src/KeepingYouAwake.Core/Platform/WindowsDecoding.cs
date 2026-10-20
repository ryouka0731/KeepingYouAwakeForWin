using System.Text;

namespace KeepingYouAwake.Core.Platform;

/// <summary>
/// Pure decoding of Win32 results, kept out of the P/Invoke layer so it is unit-tested
/// on every OS. The Windows project only fetches the raw values.
/// </summary>
public static class WindowsDecoding
{
    // GetSystemPowerStatus (SYSTEM_POWER_STATUS)
    private const byte ACLineOffline = 0;
    private const byte ACLineUnknown = 255;
    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagNoBattery = 128;
    private const byte BatteryFlagUnknown = 255;
    private const byte PercentUnknown = 255;
    private const byte StatusBatterySaverOn = 1;

    /// <summary>Maps the fields of <c>SYSTEM_POWER_STATUS</c> to a <see cref="PowerStatus"/>.</summary>
    public static PowerStatus DecodePowerStatus(byte acLineStatus, byte batteryFlag, byte batteryLifePercent, byte systemStatusFlag)
    {
        var saver = systemStatusFlag == StatusBatterySaverOn;
        var noBattery = batteryFlag is BatteryFlagNoBattery or BatteryFlagUnknown || batteryLifePercent == PercentUnknown;
        if (noBattery || acLineStatus == ACLineUnknown) { return new PowerStatus(BatteryState.Unknown, 100, saver); }

        double percent = Math.Min(batteryLifePercent, (byte)100);
        var charging = (batteryFlag & BatteryFlagCharging) != 0;
        var state = acLineStatus switch
        {
            ACLineOffline => BatteryState.Unplugged,
            _ when charging && percent < 100 => BatteryState.Charging,
            // On AC and not charging: full, or held at a charge limit.
            _ => BatteryState.Full,
        };
        return new PowerStatus(state, percent, saver);
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that are not external monitors.
    private const uint OutputLvds = 6;
    private const uint OutputDisplayPortEmbedded = 11;
    private const uint OutputUdiEmbedded = 13;
    private const uint OutputIndirectVirtual = 17;
    private const uint OutputInternal = 0x80000000;
    private const uint OutputOther = 0xFFFFFFFF;

    /// <summary>
    /// Counts active display paths that drive an external monitor: everything except
    /// built-in panels (internal / LVDS / eDP / UDI embedded) and virtual displays
    /// (Remote Desktop, VMs, virtual display drivers).
    /// </summary>
    public static int CountExternalDisplays(IEnumerable<uint> outputTechnologies) =>
        outputTechnologies.Count(t => t is not (OutputInternal or OutputLvds or OutputDisplayPortEmbedded
            or OutputUdiEmbedded or OutputIndirectVirtual or OutputOther));

    private static readonly string[] ExternalAudioEnumerators = ["USB", "BTHENUM", "BTHHFENUM", "BTHLEDEVICE", "BTHLE"];
    private const uint FormFactorRemoteNetworkDevice = 0;
    private const uint FormFactorDigitalAudioDisplayDevice = 9;

    /// <summary>
    /// Whether an audio endpoint is external, from its device enumerator (USB, Bluetooth)
    /// or its form factor (network device, HDMI / DisplayPort monitor).
    /// </summary>
    public static bool IsExternalAudioEndpoint(string? enumeratorName, uint? formFactor)
    {
        if (enumeratorName is not null
            && ExternalAudioEnumerators.Any(e => enumeratorName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return formFactor is FormFactorRemoteNetworkDevice or FormFactorDigitalAudioDisplayDevice;
    }

    // Native Wifi layouts (wlanapi.h), identical on x64 and ARM64.
    public const int WlanInterfaceListHeaderSize = 8;     // dwNumberOfItems, dwIndex
    public const int WlanInterfaceInfoSize = 532;         // GUID + WCHAR[256] + WLAN_INTERFACE_STATE
    private const int WlanInterfaceStateOffset = 16 + 512;
    private const int WlanInterfaceStateConnected = 1;
    /// <summary>Offset of wlanAssociationAttributes.dot11Ssid in WLAN_CONNECTION_ATTRIBUTES.</summary>
    public const int WlanConnectionSsidOffset = 520;

    /// <summary>The interfaces in a <c>WLAN_INTERFACE_INFO_LIST</c> and whether each is connected.</summary>
    public static IReadOnlyList<(Guid Interface, bool Connected)> ParseWlanInterfaceList(ReadOnlySpan<byte> list)
    {
        if (list.Length < WlanInterfaceListHeaderSize) { return []; }
        var count = BitConverter.ToInt32(list);
        var available = (list.Length - WlanInterfaceListHeaderSize) / WlanInterfaceInfoSize;
        var result = new List<(Guid, bool)>();
        for (var i = 0; i < Math.Min(count, available); i++)
        {
            var info = list.Slice(WlanInterfaceListHeaderSize + i * WlanInterfaceInfoSize, WlanInterfaceInfoSize);
            var connected = BitConverter.ToInt32(info[WlanInterfaceStateOffset..]) == WlanInterfaceStateConnected;
            result.Add((new Guid(info[..16]), connected));
        }
        return result;
    }

    /// <summary>The SSID in a <c>WLAN_CONNECTION_ATTRIBUTES</c>; null when missing, empty or redacted.</summary>
    public static string? ParseConnectionSsid(ReadOnlySpan<byte> attributes)
    {
        if (attributes.Length < WlanConnectionSsidOffset + 4) { return null; }
        var length = Math.Clamp(BitConverter.ToInt32(attributes[WlanConnectionSsidOffset..]), 0, 32);
        var bytes = attributes[(WlanConnectionSsidOffset + 4)..];
        return DecodeSsid(bytes[..Math.Min(length, bytes.Length)]);
    }

    /// <summary>Decodes a <c>DOT11_SSID</c>; null when empty (hidden or redacted).</summary>
    public static string? DecodeSsid(ReadOnlySpan<byte> ssid)
    {
        var length = Math.Min(ssid.Length, 32);
        return length == 0 ? null : Encoding.UTF8.GetString(ssid[..length]);
    }
}

/// <summary>The message a second instance sends to the running one: its arguments, one per line.</summary>
public static class InstanceMessage
{
    public const int MaximumBytes = 8 * 1024;

    /// <summary>Encodes arguments; no arguments means "show Settings".</summary>
    public static byte[] Encode(IReadOnlyList<string> args)
    {
        var lines = args.Count == 0 ? ["--settings"] : args.Select(a => a.Replace('\n', ' ').Replace('\r', ' '));
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return bytes.Length <= MaximumBytes ? bytes : bytes[..MaximumBytes];
    }

    public static string[] Decode(ReadOnlySpan<byte> payload) =>
        Encoding.UTF8.GetString(payload[..Math.Min(payload.Length, MaximumBytes)])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Registry values that integrate the app with the Windows shell.</summary>
public static class ShellCommands
{
    /// <summary>The <c>Run</c> value for Start at Login.</summary>
    public static string StartAtLogin(string executablePath) => $"\"{executablePath}\"";

    /// <summary>The <c>shell\open\command</c> of the <c>keepingyouawake:</c> URL scheme.</summary>
    public static string OpenUrl(string executablePath) => $"\"{executablePath}\" \"%1\"";

    /// <summary>The URL scheme's <c>DefaultIcon</c>.</summary>
    public static string Icon(string executablePath) => $"\"{executablePath}\",0";
}
