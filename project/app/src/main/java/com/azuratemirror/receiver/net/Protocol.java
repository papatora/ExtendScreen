package com.azuratemirror.receiver.net;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Hand-written implementation of docs/PROTOCOL.md. If you change anything here,
 * update windows/src/AzurateMirror.Sender/Network/Protocol.cs too.
 */
public final class Protocol {
    private Protocol() {}

    public static final byte HELLO = 0x01;
    public static final byte HELLO_ACK = 0x02;
    public static final byte VIDEO_CONFIG = 0x03;
    public static final byte VIDEO_FRAME = 0x04;
    public static final byte CONTROL = 0x05;
    public static final byte STATS_CLIENT = 0x06;
    public static final byte PING = 0x07;
    public static final byte PONG = 0x08;

    public static final int HEADER_SIZE = 13;

    public static final class FrameHeader {
        public final byte type;
        public final int length;
        public final long timestampMs;

        public FrameHeader(byte type, int length, long timestampMs) {
            this.type = type;
            this.length = length;
            this.timestampMs = timestampMs;
        }
    }

    public static void writeUInt32BE(byte[] buf, int offset, int value) {
        buf[offset] = (byte) (value >>> 24);
        buf[offset + 1] = (byte) (value >>> 16);
        buf[offset + 2] = (byte) (value >>> 8);
        buf[offset + 3] = (byte) value;
    }

    public static void writeUInt64BE(byte[] buf, int offset, long value) {
        for (int i = 0; i < 8; i++)
            buf[offset + i] = (byte) (value >>> (56 - i * 8));
    }

    public static void writeUInt16BE(byte[] buf, int offset, int value) {
        buf[offset] = (byte) (value >>> 8);
        buf[offset + 1] = (byte) value;
    }

    public static long readUInt32BE(byte[] buf, int offset) {
        return ((long) (buf[offset] & 0xFF) << 24)
                | ((buf[offset + 1] & 0xFF) << 16)
                | ((buf[offset + 2] & 0xFF) << 8)
                | (buf[offset + 3] & 0xFF);
    }

    public static long readUInt64BE(byte[] buf, int offset) {
        long v = 0;
        for (int i = 0; i < 8; i++)
            v = (v << 8) | (buf[offset + i] & 0xFF);
        return v;
    }

    public static int readUInt16BE(byte[] buf, int offset) {
        return ((buf[offset] & 0xFF) << 8) | (buf[offset + 1] & 0xFF);
    }

    public static void writeFrame(OutputStream out, byte type, byte[] payload, long timestampMs) throws IOException {
        byte[] header = new byte[HEADER_SIZE];
        header[0] = type;
        writeUInt32BE(header, 1, payload == null ? 0 : payload.length);
        writeUInt64BE(header, 5, timestampMs);
        out.write(header);
        if (payload != null && payload.length > 0)
            out.write(payload);
    }

    public static FrameHeader readHeader(InputStream in) throws IOException {
        byte[] buf = readExact(in, HEADER_SIZE);
        int length = (int) readUInt32BE(buf, 1);
        long ts = readUInt64BE(buf, 5);
        return new FrameHeader(buf[0], length, ts);
    }

    public static byte[] readExact(InputStream in, int count) throws IOException {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count) {
            int n = in.read(buf, read, count - read);
            if (n <= 0) throw new EOFException("Connection closed while reading frame.");
            read += n;
        }
        return buf;
    }
}
