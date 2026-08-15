using System;
using System.Runtime.InteropServices;

namespace AzurateMirror.Sender.Input;

/// <summary>
/// Injects PC mouse input from touchpad events relayed by the Android client (Task #14, opt-in,
/// off by default - see docs/PROTOCOL.md's CONTROL{"cmd":"touch"} shape).
///
/// Uses classic SetCursorPos + mouse_event, NOT the newer InitializeTouchInjection/InjectTouchInput
/// touch-pointer API - that was tried first (it's architecturally the "correct" choice, since real
/// touchscreen digitizers report as POINTER_INPUT_TYPE_TOUCH rather than stealing the system mouse
/// cursor) but empirically fails with ERROR_INVALID_PARAMETER (Win32 87) specifically when the
/// target point falls on this app's virtual display. Confirmed via isolated testing: the exact
/// same injection call succeeds on the real primary monitor AND a real physical second monitor,
/// but fails only on the IddCx-backed virtual display - it doesn't expose whatever the touch
/// stack needs (no registered digitizer/HID touch device backing it), so InjectTouchInput rejects
/// it outright. This is a hard driver-level limitation, not a bug in the injection code here.
///
/// Because of that, this DOES move the single system mouse cursor onto the virtual display while
/// a touch is active - there's no way around that with mouse-class input. To limit how disruptive
/// that is to a physical mouse in active use elsewhere, the cursor's pre-touch position is saved
/// on the first TouchDown of a gesture and restored on TouchUp, so between touches the mouse is
/// back exactly where the user left it - the tablet only "borrows" the cursor for the duration of
/// an actual touch-and-hold, not permanently.
/// </summary>
public static class TouchInjector
{
    private static bool _contactDown;
    private static POINT _savedPosition;

    public static void TouchDown(int x, int y)
    {
        if (!_contactDown)
        {
            GetCursorPos(out _savedPosition);
            _contactDown = true;
        }
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    }

    public static void TouchMove(int x, int y)
    {
        if (!_contactDown) return; // no active contact to move - avoids a stray move outside a down/up pair
        SetCursorPos(x, y);
    }

    public static void TouchUp(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        if (_contactDown)
        {
            SetCursorPos(_savedPosition.X, _savedPosition.Y);
            _contactDown = false;
        }
    }

    /// <summary>Long-press on the tablet maps to this (see MirrorActivity.setupTouchpad's Java
    /// side) - immediate right-click, then restore the cursor same as TouchUp does.</summary>
    public static void RightClick(int x, int y)
    {
        bool hadSaved = _contactDown;
        POINT saved = _savedPosition;
        if (!hadSaved) GetCursorPos(out saved);

        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        SetCursorPos(saved.X, saved.Y);
        _contactDown = false;
    }

    /// <summary>Reads the cursor position back from THIS process - used for same-process
    /// diagnostic verification, sidestepping the DPI-virtualization mismatches seen when a
    /// separate (possibly DPI-unaware) process reads back a position this process set.</summary>
    public static (int X, int Y) GetPosition()
    {
        GetCursorPos(out POINT p);
        return (p.X, p.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
}
