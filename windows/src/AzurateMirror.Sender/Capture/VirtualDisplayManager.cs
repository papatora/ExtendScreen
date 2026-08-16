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

    /// <summary>
    /// Finds the virtual display's GDI device name via EnumDisplayDevices alone, without the DXGI
    /// cross-reference FindVirtualOutput does. EnumDisplayDevices enumerates hardware the driver
    /// knows about regardless of desktop-attach state, so this works even when the display has
    /// been DetachFromDesktop'd (DXGI's own output enumeration may not list detached displays) -
    /// needed to know what name to pass to AttachToDesktop before DXGI can see it again.
    /// </summary>
    public static string? FindVirtualDisplayGdiName()
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(dd);
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (MonitorDeviceIdContains(dd.DeviceName, VirtualMonitorIdMarker))
                return dd.DeviceName;
            dd.cb = Marshal.SizeOf(dd);
        }
        return null;
    }

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
    /// DEPRECATED - do not call. Toggling the PnP device itself (pnputil enable/disable) via UAC
    /// on every single app Start/Stop repeatedly corrupted the virtual display's device node in
    /// practice (ended up "The device is not connected", needing a full driver reinstall to
    /// recover - happened 3 times in one day). Kept only so any stray caller fails loudly instead
    /// of silently doing something destructive. Use DetachFromDesktop/AttachToDesktop instead -
    /// those change display TOPOLOGY only (like Windows' own "Disconnect this display"), never
    /// touch the PnP device, and don't need admin/UAC at all.
    /// </summary>
    [Obsolete("Corrupts the VDD device node under real-world UAC-failure conditions - use DetachFromDesktop/AttachToDesktop instead.", error: true)]
    public static bool EnableDriver() => throw new InvalidOperationException("EnableDriver is disabled - see DetachFromDesktop/AttachToDesktop.");

    [Obsolete("Corrupts the VDD device node under real-world UAC-failure conditions - use DetachFromDesktop/AttachToDesktop instead.", error: true)]
    public static bool DisableDriver() => throw new InvalidOperationException("DisableDriver is disabled - see DetachFromDesktop/AttachToDesktop.");

    /// <summary>
    /// Removes the virtual display from the desktop (Display Settings, mouse can't wander onto
    /// it) WITHOUT touching the underlying PnP device at all - this is the same mechanism as
    /// Windows' own Settings > Display > "Disconnect this display", just driven programmatically.
    /// No admin/UAC needed (a user can always reconfigure their own session's display topology).
    /// Safe to call as often as needed (every Stop, every disconnect) - unlike the old pnputil
    /// enable/disable dance, this has no way to corrupt the driver's device node since the device
    /// itself never changes state, only whether it's part of the desktop.
    /// </summary>
    public static bool DetachFromDesktop(string gdiDeviceName)
    {
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf(mode);
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        mode.dmPelsWidth = 0;
        mode.dmPelsHeight = 0;

        int result = ChangeDisplaySettingsEx(gdiDeviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ApplyPendingTopology();
        return result == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Re-adds the virtual display to the desktop at the given resolution - the inverse
    /// of DetachFromDesktop, same non-destructive topology-only mechanism.</summary>
    public static bool AttachToDesktop(string gdiDeviceName, int width, int height, int refreshHz)
    {
        var mode = new DEVMODE();
        mode.dmSize = (short)Marshal.SizeOf(mode);
        mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmDisplayFrequency = refreshHz;

        int result = ChangeDisplaySettingsEx(gdiDeviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ApplyPendingTopology();
        return result == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>
    /// CDS_NORESET above stages the change without applying it immediately (lets several display
    /// changes batch into one flicker) - this commits whatever's staged, matching the documented
    /// ChangeDisplaySettingsEx(NULL, NULL, NULL, 0, NULL) "apply now" pattern.
    ///
    /// MUST go through the IntPtr-typed overload below, not the DEVMODE-by-ref one used above -
    /// a `ref DEVMODE` parameter can never marshal to a true null pointer, so calling the by-ref
    /// overload with a zeroed-but-non-null DEVMODE (dmFields=0) makes Win32 see "apply zero field
    /// changes", a harmless no-op that returns DISP_CHANGE_SUCCESSFUL without ever committing the
    /// staged CDS_NORESET registry change. Confirmed via research this was exactly why an earlier
    /// version of this method silently failed to detach anything despite reporting success.
    /// </summary>
    private static void ApplyPendingTopology()
    {
        ChangeDisplaySettingsExNull(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

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
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    /// <summary>Separate overload purely so the "apply pending changes" call can pass a genuine
    /// NULL for both device name and DEVMODE - see ApplyPendingTopology's doc comment for why
    /// the `ref DEVMODE` overload above can never do that.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsExNull(IntPtr lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DM_POSITION = 0x00000020;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_NORESET = 0x10000000;
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
