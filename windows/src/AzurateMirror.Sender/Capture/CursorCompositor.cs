using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace AzurateMirror.Sender.Capture;

/// <summary>
/// DXGI Desktop Duplication does not bake the mouse cursor into captured frames - the cursor is
/// rendered by DWM as a separate hardware overlay, so it's invisible to any Desktop-Duplication
/// based capture unless the app composites it back in manually (why the cursor showed as
/// "transparent"/invisible on the tablet even though clicks still worked - clicks go through the
/// normal Windows input pipeline, independent of what a capture tool can see).
///
/// Rather than decoding DXGI's raw pointer-shape formats (monochrome/color/masked-color) by hand,
/// this uses the classic GDI trick: get a device context onto a GDI-compatible D3D11 surface via
/// IDXGISurface1.GetDC, then let Windows draw its OWN current cursor onto it with DrawIconEx -
/// this automatically handles every cursor shape/animation Windows supports.
/// </summary>
public static class CursorCompositor
{
    public static void DrawCursorOnto(ID3D11Texture2D gdiCompatibleTexture, int captureOriginX, int captureOriginY)
    {
        using var surface = gdiCompatibleTexture.QueryInterface<Vortice.DXGI.IDXGISurface1>();

        CURSORINFO ci = new() { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero)
            return;

        if (!GetIconInfo(ci.hCursor, out ICONINFO iconInfo))
            return;

        try
        {
            int x = ci.ptScreenPos.X - captureOriginX - iconInfo.xHotspot;
            int y = ci.ptScreenPos.Y - captureOriginY - iconInfo.yHotspot;

            IntPtr hdc = surface.GetDC(false);
            try
            {
                DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                surface.ReleaseDC(null);
            }
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
    }

    // --- Win32 interop ---

    private const uint CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
