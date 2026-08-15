package com.azuratemirror.receiver.video;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;
import android.view.Surface;

import java.nio.ByteBuffer;
import java.util.concurrent.LinkedBlockingQueue;

/**
 * Hardware H.264 decode straight to a Surface, using MediaCodec's async callback API
 * (same approach scrcpy uses) - no PTS pacing, decode-and-show-ASAP for a live mirror
 * with no audio to sync against.
 *
 * MediaCodec's async callback hands us free input-buffer *indices*, not data - so when
 * onInputBufferAvailable fires and there's no access unit queued yet, the index is parked
 * in pendingInputIndices until submitAccessUnit() has something to feed it.
 */
public final class H264Decoder {
    private static final String TAG = "AzurateMirror/H264Decoder";
    private static final String MIME = "video/avc";

    private MediaCodec codec;
    private final Surface surface;
    private volatile boolean configured;

    private final LinkedBlockingQueue<Integer> pendingInputIndices = new LinkedBlockingQueue<>();
    private final LinkedBlockingQueue<QueuedAccessUnit> queue = new LinkedBlockingQueue<>(64);
    private final java.util.concurrent.atomic.AtomicLong renderedFrameCount = new java.util.concurrent.atomic.AtomicLong();

    // VIDEO_CONFIG is delivered via a Handler.post (needs a Looper thread for
    // MediaCodec.setCallback), while VIDEO_FRAME is delivered synchronously on the raw network
    // thread - so a frame can reach submitAccessUnit() before the posted configure() has actually
    // run. Buffer a few such frames here instead of dropping them; configure() drains this once
    // the codec is ready. Small bound since only the frames from the last few tens of ms matter.
    private final java.util.ArrayDeque<QueuedAccessUnit> preConfigBuffer = new java.util.ArrayDeque<>();
    private static final int PRE_CONFIG_BUFFER_MAX = 16;

    public H264Decoder(Surface surface) {
        this.surface = surface;
    }

    private static final class QueuedAccessUnit {
        final byte[] data;
        final long timestampUs;
        QueuedAccessUnit(byte[] data, long timestampUs) {
            this.data = data;
            this.timestampUs = timestampUs;
        }
    }

    /** sps/pps are the raw Annex-B NAL bytes (start code + header + RBSP) from VIDEO_CONFIG. */
    public synchronized void configure(int width, int height, byte[] sps, byte[] pps) {
        try {
            if (codec != null) {
                release();
            }
            MediaFormat format = MediaFormat.createVideoFormat(MIME, width, height);
            format.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
            format.setByteBuffer("csd-1", ByteBuffer.wrap(pps));

            codec = MediaCodec.createDecoderByType(MIME);
            codec.setCallback(new MediaCodec.Callback() {
                @Override
                public void onInputBufferAvailable(MediaCodec mc, int index) {
                    pendingInputIndices.offer(index);
                    drainQueueToCodec();
                }

                @Override
                public void onOutputBufferAvailable(MediaCodec mc, int index, MediaCodec.BufferInfo info) {
                    try {
                        mc.releaseOutputBuffer(index, true);
                        long n = renderedFrameCount.incrementAndGet();
                        if (n % 30 == 0) Log.i(TAG, "rendered frames: " + n);
                    } catch (IllegalStateException e) {
                        Log.w(TAG, "releaseOutputBuffer failed (codec probably reconfiguring)", e);
                    }
                }

                @Override
                public void onError(MediaCodec mc, MediaCodec.CodecException e) {
                    Log.e(TAG, "MediaCodec error", e);
                }

                @Override
                public void onOutputFormatChanged(MediaCodec mc, MediaFormat format) {
                    Log.i(TAG, "Output format changed: " + format);
                }
            });
            codec.configure(format, surface, null, 0);
            codec.start();
            configured = true;
            Log.i(TAG, "Decoder configured " + width + "x" + height);

            QueuedAccessUnit buffered;
            int replayed = 0;
            while ((buffered = preConfigBuffer.poll()) != null) {
                try {
                    queue.put(buffered);
                    replayed++;
                } catch (InterruptedException ignored) {
                }
            }
            if (replayed > 0) {
                Log.i(TAG, "Replayed " + replayed + " frame(s) buffered before configure() completed");
                drainQueueToCodec();
            }
        } catch (Exception e) {
            Log.e(TAG, "configure failed", e);
            configured = false;
        }
    }

    /** Call from the network read thread for every VIDEO_FRAME payload received. */
    public synchronized void submitAccessUnit(byte[] annexB, long timestampMs) {
        var unit = new QueuedAccessUnit(annexB, timestampMs * 1000);

        if (!configured || codec == null) {
            // configure() is still in flight (posted to the UI thread) - buffer instead of
            // silently dropping so the very first frame(s) after VIDEO_CONFIG aren't lost.
            if (preConfigBuffer.size() >= PRE_CONFIG_BUFFER_MAX) preConfigBuffer.poll();
            preConfigBuffer.offer(unit);
            return;
        }

        try {
            queue.put(unit);
        } catch (InterruptedException ignored) {
        }
        drainQueueToCodec();
    }

    private synchronized void drainQueueToCodec() {
        if (codec == null) return;
        Integer index;
        while ((index = pendingInputIndices.poll()) != null) {
            QueuedAccessUnit unit = queue.poll();
            if (unit == null) {
                pendingInputIndices.offer(index); // nothing to feed yet, put the index back
                break;
            }
            try {
                ByteBuffer input = codec.getInputBuffer(index);
                if (input == null) continue;
                input.clear();
                input.put(unit.data);
                codec.queueInputBuffer(index, 0, unit.data.length, unit.timestampUs, 0);
            } catch (IllegalStateException e) {
                Log.w(TAG, "queueInputBuffer failed", e);
            }
        }
    }

    public synchronized void release() {
        configured = false;
        if (codec != null) {
            try {
                codec.stop();
            } catch (Exception ignored) {
            }
            try {
                codec.release();
            } catch (Exception ignored) {
            }
            codec = null;
        }
        queue.clear();
        pendingInputIndices.clear();
    }

    public boolean isConfigured() {
        return configured;
    }
}
