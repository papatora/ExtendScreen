namespace AzurateMirror.Sender.Capture;

/// <summary>Matches docs/PROTOCOL.md's HELLO mode byte: 0=mirror, 1=extend.
/// Extend now targets the real "VDD by MTT" virtual display (VirtualDisplayManager), not a
/// cropped region of the primary screen like v1's CanvasWindow approach.</summary>
public enum CaptureMode : byte
{
    Mirror = 0,
    Extend = 1,
}
