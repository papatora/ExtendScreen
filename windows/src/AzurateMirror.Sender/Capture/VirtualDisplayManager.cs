using System;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.DXGI;

namespace AzurateMirror.Sender.Capture;

public readonly record struct DxgiOutputRef(uint AdapterIndex, uint OutputIndex, string GdiDeviceName, string AdapterDescription, int Width, int Height, int OriginX, int OriginY);

/// <summary>
/// Finds and manages the VirtualDrivers/Virtual-Display-Driver ("VDD by MTT") output, and the
/// real primary display, by GDI device name rather than a hardcoded DXGI adapter/output index -
/// empirically on this machine the virtual display shares Adapter 0 (the NVIDIA GPU) with the
/// real primary display as a second Output, so index alone isn't a stable way to tell them apart,
/// and it can shift as monitors are (dis)connected. Matching by the monitor's PNP hardware ID
/// ("MTT" prefix - see Device Manager entry "Generic Monitor (VDD by MTT)") is stable instead.
///
/// This class is the ONLY thing in AzurateMirrorV2 that talks to the driver - per the plan, the
/// user explicitly does not want to depend on the third-party "VDD Control" app for day-to-day
/// use, so reload/mode-set must be self-contained here.
/// </summary>
public static class VirtualDisplayManager
{
    private const string VirtualMonitorIdMarker = "MTT";
    public const string VddSettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    public const string VddDeviceInstanceId = @"ROOT\DISPLAY\0000";

    /// <summary>Finds the virtual display's DXGI adapter/output by matching its monitor's PNP hardware ID.</summary>
    public static DxgiOutputRef? FindVirtualOutput() => FindOutputByGdiNamePredicate(gdiName => MonitorDeviceIdContains(gdiName, VirtualMonitorIdMarker));

    /// <summary>Finds the real primary display's DXGI adapter/output (the one Windows marks primary via GDI).</summary>
    public static DxgiOutputRef? FindPrimaryOutput() => FindOutputByGdiNamePredicate(IsPrimaryGdiDevice);

    private static DxgiOutputRef? FindOutputByGdiNamePredicate(Func<string, bool> matches)
    {
        string? targetGdiName = null;
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(dd);
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (matches(dd.DeviceName))
            {
                targetGdiName = dd.DeviceName;
                break;
            }
            dd.cb = Marshal.SizeOf(dd);
        }

        if (targetGdiName is null) return null;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ai++)
        {
            for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; oi++)
            {
                var od = output.Description;
                if (string.Equals(od.DeviceName, targetGdiName, StringComparison.OrdinalIgnoreCase))
                {
                    var b = od.DesktopCoordinates;
                    var result = new DxgiOutputRef(ai, oi, od.DeviceName, adapter.Description1.Description, b.Right - b.Left, b.Bottom - b.Top, b.Left, b.Top);
                    output.Dispose();
                    adapter.Dispose();
                    return result;
                }
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }

    private static bool IsPrimaryGdiDevice(string gdiDeviceName)
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(dd);
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (string.Equals(dd.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                return (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            dd.cb = Marshal.SizeOf(dd);
        }
        return false;
    }

    /// <summary>Checks the GDI adapter's attached monitor PNP device ID for a marker substring (e.g. "MTT").</summary>
    private static bool MonitorDeviceIdContains(string adapterGdiName, string marker)
    {
        var monitor = new DISPLAY_DEVICE();
        monitor.cb = Marshal.SizeOf(monitor);
        if (EnumDisplayDevices(adapterGdiName, 0, ref monitor, 0))
        {
            return monitor.DeviceID.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
                || monitor.DeviceString.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return false;
    }

    /// <summary>
    /// Sets the active display mode (resolution + refresh rate) for a GDI device. The mode must
    /// already be listed as available (see vdd_settings.xml's &lt;resolutions&gt; - v2's setup
    /// step adds a 2560x1600@60 entry matching the tablet's panel) before this can select it.
    /// </summary>
    public static bool SetDisplayMode(string gdiDeviceName, int width, int height, int refreshHz)
    {
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf(mode);
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmDisplayFrequency = refreshHz;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        int result = ChangeDisplaySettingsEx(gdiDeviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return result == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>
    /// Reloads the virtual display driver (disable+enable its PnP device) so it re-reads
    /// vdd_settings.xml - requires admin, so this launches an elevated helper process via the
    /// UAC "runas" verb rather than requiring the whole app to run elevated. The user sees one
    /// UAC prompt for this specific action only.
    /// </summary>
    public static bool ReloadDriver()
    {
        string psCommand =
            $"pnputil /disable-device \"{VddDeviceInstanceId}\"; " +
            "Start-Sleep -Milliseconds 800; " +
            $"pnputil /enable-device \"{VddDeviceInstanceId}\"";
        return RunElevatedPnputil(psCommand);
    }

    /// <summary>
    /// Enables the virtual display so it appears as display "4" in Windows again - called from
    /// BtnStart before the capture pipeline resolves its target output. One UAC prompt per app
    /// Start, not per tablet reconnect (see DisableDriver's doc comment for why that granularity
    /// was chosen over per-connect/disconnect).
    /// </summary>
    public static bool EnableDriver() => RunElevatedPnputil($"pnputil /enable-device \"{VddDeviceInstanceId}\"");

    /// <summary>
    /// Disables the virtual display so it disappears from Windows entirely (Display Settings,
    /// and - the actual motivating complaint - the mouse can no longer wander into a "display 4"
    /// region that isn't rendering anything when no session is active). Called from BtnStop's
    /// pipeline cleanup, once capture has already torn down. Deliberately scoped to Start/Stop
    /// rather than per tablet connect/disconnect: pnputil disable/enable each need their own UAC
    /// elevation, and firing that on every reconnect (WiFi drop, app backgrounded, etc.) would be
    /// far more disruptive than the mouse-wandering problem it's meant to fix.
    /// </summary>
    public static bool DisableDriver() => RunElevatedPnputil($"pnputil /disable-device \"{VddDeviceInstanceId}\"");

    /// <summary>
    /// Runs a PowerShell command elevated via UAC "runas". Uses -EncodedCommand (base64 UTF-16LE)
    /// rather than -Command "...", because the naive $"-Command \"{psCommand}\"" pattern breaks
    /// as soon as psCommand itself contains double quotes (e.g. around the device instance ID) -
    /// the outer ProcessStartInfo.Arguments quoting and the inner PowerShell string quoting
    /// collide and truncate the command mid-argument. Confirmed broken in practice: the UAC
    /// prompt appeared and was approved, but pnputil still failed because it never received its
    /// device-ID argument intact. -EncodedCommand sidesteps quoting entirely.
    /// </summary>
    private static bool RunElevatedPnputil(string psCommand)
    {
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psCommand));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(15000);
            return proc?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user declined the UAC prompt
        }
    }

    // --- Win32 interop ---

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
