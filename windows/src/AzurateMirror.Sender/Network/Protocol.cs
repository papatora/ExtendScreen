using System.IO;

namespace AzurateMirror.Sender.Network;

/// <summary>
/// Hand-written implementation of docs/PROTOCOL.md. If you change anything here,
/// update project/app/src/main/java/com/azuratemirror/receiver/net/Protocol.java too.
/// </summary>
public static class Protocol
{
    public const byte Hello = 0x01;
    public const byte HelloAck = 0x02;
    public const byte VideoConfig = 0x03;
    public const byte VideoFrame = 0x04;
    public const byte Control = 0x05;
    public const byte StatsClient = 0x06;
    public const byte Ping = 0x07;
    public const byte Pong = 0x08;

    public const int HeaderSize = 13;

    public static byte[] BuildHeader(byte type, int payloadLength, ulong timestampMs)
    {
        var header = new byte[HeaderSize];
        header[0] = type;
        WriteUInt32BE(header, 1, (uint)payloadLength);
        WriteUInt64BE(header, 5, timestampMs);
        return header;
    }

    public static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    public static void WriteUInt64BE(byte[] buf, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
            buf[offset + i] = (byte)(value >> (56 - i * 8));
    }

    public static uint ReadUInt32BE(byte[] buf, int offset)
    {
        return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
               ((uint)buf[offset + 2] << 8) | buf[offset + 3];
    }

    public static ulong ReadUInt64BE(byte[] buf, int offset)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
            v = (v << 8) | buf[offset + i];
        return v;
    }

    public static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    /// <summary>Writes a full frame (header + payload) to the stream in one call.</summary>
    public static void WriteFrame(Stream s, byte type, byte[] payload, ulong timestampMs)
    {
        var header = BuildHeader(type, payload.Length, timestampMs);
        s.Write(header, 0, header.Length);
        if (payload.Length > 0)
            s.Write(payload, 0, payload.Length);
    }

    public readonly record struct FrameHeader(byte Type, int Length, ulong TimestampMs);

    public static FrameHeader ReadHeader(Stream s)
    {
        var buf = ReadExact(s, HeaderSize);
        return new FrameHeader(buf[0], (int)ReadUInt32BE(buf, 1), ReadUInt64BE(buf, 5));
    }

    public static byte[] ReadExact(Stream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException("Connection closed while reading frame.");
            read += n;
        }
        return buf;
    }
}
