package com.azuratemirror.receiver.net;

import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import java.io.IOException;
import java.io.OutputStream;
import java.net.Socket;

/**
 * Phase-2 bare client: connects, sends HELLO, then loop-reads frames and dispatches them.
 * Reconnect/backoff lands in the control-channel phase; for now a failed/closed connection
 * just calls onDisconnected and stops.
 */
public final class MirrorClient {
    private static final String TAG = "AzurateMirror/MirrorClient";

    public interface Listener {
        void onConnected();
        void onVideoConfig(int width, int height, int fps, byte[] sps, byte[] pps);
        void onVideoFrame(byte[] annexB, boolean keyFrame, long timestampMs);
        void onDisconnected(String reason);
    }

    private final String host;
    private final int port;
    private final int mode; // matches MirrorActivity.MODE_MIRROR / MODE_EXTEND and docs/PROTOCOL.md's HELLO mode byte
    private final Listener listener;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    private Thread thread;
    private volatile boolean running;
    private Socket socket;
    private OutputStream out;
    private final Object writeLock = new Object();

    public MirrorClient(String host, int port, int mode, Listener listener) {
        this.host = host;
        this.port = port;
        this.mode = mode;
        this.listener = listener;
    }

    public void start() {
        if (thread != null) return;
        running = true;
        thread = new Thread(this::run, "MirrorClient");
        thread.start();
    }

    public void stop() {
        running = false;
        try {
            if (socket != null) socket.close();
        } catch (IOException ignored) {
        }
    }

    private void run() {
        try {
            socket = new Socket();
            socket.connect(new java.net.InetSocketAddress(host, port), 5000);
            socket.setTcpNoDelay(true);

            out = socket.getOutputStream();
            java.io.InputStream in = socket.getInputStream();

            byte[] name = android.os.Build.MODEL.getBytes(java.nio.charset.StandardCharsets.UTF_8);
            byte[] hello = new byte[2 + name.length];
            hello[0] = 1; // protocolVersion
            hello[1] = (byte) mode; // 0=mirror, 1=extend - see docs/PROTOCOL.md
            System.arraycopy(name, 0, hello, 2, name.length);
            Protocol.writeFrame(out, Protocol.HELLO, hello, 0);

            Protocol.FrameHeader ackHeader = Protocol.readHeader(in);
            byte[] ackPayload = ackHeader.length > 0 ? Protocol.readExact(in, ackHeader.length) : new byte[0];
            boolean accepted = ackHeader.type == Protocol.HELLO_ACK && ackPayload.length > 0 && ackPayload[0] != 0;
            if (!accepted) {
                notifyDisconnected("Server rejected connection");
                return;
            }

            mainHandler.post(listener::onConnected);
            Log.i(TAG, "Connected to " + host + ":" + port);

            while (running) {
                Protocol.FrameHeader header = Protocol.readHeader(in);
                byte[] payload = header.length > 0 ? Protocol.readExact(in, header.length) : new byte[0];
                dispatch(header.type, payload, header.timestampMs);
            }
        } catch (Exception e) {
            if (running) notifyDisconnected(e.getMessage() != null ? e.getMessage() : e.toString());
        } finally {
            try {
                if (socket != null) socket.close();
            } catch (IOException ignored) {
            }
        }
    }

    private void dispatch(byte type, byte[] payload, long timestampMs) {
        if (type == Protocol.VIDEO_CONFIG) {
            int o = 0;
            int width = (int) Protocol.readUInt32BE(payload, o); o += 4;
            int height = (int) Protocol.readUInt32BE(payload, o); o += 4;
            int fps = payload[o++] & 0xFF;
            o++; // codecId, only H264(1) supported for now
            int spsLen = Protocol.readUInt16BE(payload, o); o += 2;
            byte[] sps = new byte[spsLen];
            System.arraycopy(payload, o, sps, 0, spsLen); o += spsLen;
            int ppsLen = Protocol.readUInt16BE(payload, o); o += 2;
            byte[] pps = new byte[ppsLen];
            System.arraycopy(payload, o, pps, 0, ppsLen);

            // Posted to mainHandler (not called directly) because H264Decoder.configure() calls
            // MediaCodec.setCallback() without an explicit Handler, which needs a thread with a
            // Looper (this raw network Thread has none). VIDEO_FRAME below stays synchronous on
            // this thread, so there's a window where a frame can arrive before the posted config
            // runnable has actually run on the UI thread - H264Decoder buffers pre-configuration
            // frames instead of dropping them to cover that gap (see submitAccessUnit).
            mainHandler.post(() -> listener.onVideoConfig(width, height, fps, sps, pps));
        } else if (type == Protocol.VIDEO_FRAME) {
            boolean keyFrame = payload.length > 0 && payload[0] != 0;
            byte[] annexB = new byte[payload.length - 1];
            System.arraycopy(payload, 1, annexB, 0, annexB.length);
            listener.onVideoFrame(annexB, keyFrame, timestampMs);
        } else if (type == Protocol.PING) {
            // Echo straight back on this same network thread - any delay here would pollute the
            // server's round-trip latency measurement, so no Handler hop, no extra Thread.
            synchronized (writeLock) {
                try {
                    Protocol.writeFrame(out, Protocol.PONG, new byte[0], 0);
                } catch (Throwable e) {
                    Log.w(TAG, "PONG echo failed", e);
                }
            }
        }
        // CONTROL / PONG (as a reply to our own PING) not used by the client yet.
    }

    private void notifyDisconnected(String reason) {
        mainHandler.post(() -> listener.onDisconnected(reason));
    }

    /** Asks the server for an immediate fresh keyframe - call when a new Surface is ready but
     * the connection stayed alive (e.g. app resumed from background), so the decoder isn't stuck
     * waiting for the encoder's next naturally-scheduled IDR. Safe to call from any thread. */
    public void requestKeyframe() {
        if (out == null) {
            Log.w(TAG, "requestKeyframe: out stream is null, cannot send");
            return;
        }
        // Callers include surfaceCreated(), which runs on the UI thread - Android forbids
        // blocking socket I/O there (NetworkOnMainThreadException), so hop to a throwaway
        // background thread for the actual write.
        new Thread(() -> {
            byte[] cmd = "{\"cmd\":\"request_keyframe\"}".getBytes(java.nio.charset.StandardCharsets.UTF_8);
            synchronized (writeLock) {
                try {
                    Protocol.writeFrame(out, Protocol.CONTROL, cmd, 0);
                    Log.i(TAG, "requestKeyframe: sent successfully");
                } catch (Throwable e) {
                    Log.w(TAG, "requestKeyframe failed", e);
                }
            }
        }, "MirrorClient-ctrl").start();
    }

    /** Sends a touchpad event (Task #14) - coordinates are normalized [0,1] relative to the
     * SurfaceView's own bounds (already letterbox-corrected, so no extra offset math is needed
     * here - see MirrorActivity's touch listener). The server maps them onto the virtual
     * display's real pixel bounds and injects a mouse move/click. Called directly from the
     * SurfaceView's onTouch on the UI thread, so - same as requestKeyframe - the actual socket
     * write is hopped to a background thread to avoid NetworkOnMainThreadException. Touch events
     * fire far more often than a keyframe request, so this intentionally reuses one small
     * background thread pattern per call rather than a persistent queue - simplest thing that
     * works for now; revisit if touch ever needs sub-frame-interval precision. */
    public void sendTouch(String action, float xNorm, float yNorm) {
        if (out == null) { Log.w(TAG, "sendTouch: out is null, dropping"); return; }
        String json = "{\"cmd\":\"touch\",\"action\":\"" + action + "\",\"xNorm\":" + xNorm + ",\"yNorm\":" + yNorm + "}";
        new Thread(() -> {
            byte[] cmd = json.getBytes(java.nio.charset.StandardCharsets.UTF_8);
            synchronized (writeLock) {
                try {
                    Protocol.writeFrame(out, Protocol.CONTROL, cmd, 0);
                    Log.i(TAG, "sendTouch: sent " + json);
                } catch (Throwable e) {
                    Log.w(TAG, "sendTouch failed", e);
                }
            }
        }, "MirrorClient-touch").start();
    }
}
