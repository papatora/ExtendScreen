package com.azuratemirror.receiver;

import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewTreeObserver;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import com.azuratemirror.receiver.net.MirrorClient;
import com.azuratemirror.receiver.video.H264Decoder;

public class MirrorActivity extends AppCompatActivity implements SurfaceHolder.Callback, MirrorClient.Listener {

    private static final String TAG = "AzurateMirror/MirrorActivity";
    public static final String EXTRA_HOST = "host";
    public static final String EXTRA_PORT = "port";
    public static final String EXTRA_MODE = "mode";
    public static final String EXTRA_TOUCHPAD_ENABLED = "touchpadEnabled";
    public static final int MODE_MIRROR = 0;
    public static final int MODE_EXTEND = 1;

    private String host;
    private int port;
    private int mode;
    private boolean touchpadEnabled;
    private H264Decoder decoder;
    private MirrorClient client;
    private SurfaceView surfaceView;

    // Cached last VIDEO_CONFIG so a decoder created after a surface recreation (window-manager
    // churn on some OEM skins destroys/recreates the SurfaceView's Surface without the Activity
    // itself dying) can reconfigure immediately without needing the network connection to be
    // torn down and re-established. Also drives the letterbox sizing below.
    private int cfgWidth, cfgHeight;
    private byte[] cfgSps, cfgPps;
    private boolean hasCachedConfig;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_mirror);
        applyImmersiveFullscreen();

        host = getIntent().getStringExtra(EXTRA_HOST);
        port = getIntent().getIntExtra(EXTRA_PORT, DashboardActivity.DEFAULT_PORT);
        mode = getIntent().getIntExtra(EXTRA_MODE, MODE_EXTEND);
        touchpadEnabled = getIntent().getBooleanExtra(EXTRA_TOUCHPAD_ENABLED, false);

        surfaceView = findViewById(R.id.surfaceMirror);
        surfaceView.getHolder().addCallback(this);

        // Self-healing against a race that only reproduced over WiFi (not USB): applyLetterboxSizing
        // reads the parent's getWidth()/getHeight() at whatever moment VIDEO_CONFIG happens to
        // arrive, but the immersive-fullscreen system-bar-hide triggered in applyImmersiveFullscreen()
        // above is an ASYNC animation - if the parent's final post-immersive size hasn't settled
        // yet at that exact moment (more likely to be caught mid-transition under WiFi's network
        // round-trip timing than USB's near-instant loopback), the box gets sized against a
        // transient/wrong parent height and never recalculated, leaving a visible letterbox strip
        // that should have gone away once the bars finished hiding. This listener re-applies the
        // same sizing every time the parent's actual layout changes for any reason (immersive mode
        // settling, later system-bar swipe reveal/hide, rotation, etc.), so it self-corrects
        // regardless of exactly when VIDEO_CONFIG happened to race the animation.
        View mirrorParent = (View) surfaceView.getParent();
        if (mirrorParent != null) {
            mirrorParent.getViewTreeObserver().addOnGlobalLayoutListener(() -> {
                if (hasCachedConfig) applyLetterboxSizing(cfgWidth, cfgHeight);
            });
        }

        // Network connection lifecycle is tied to the Activity, NOT the Surface - a surface
        // can be destroyed/recreated by the window manager without the connection needing to drop.
        client = new MirrorClient(host, port, mode, this);
        client.start();

        if (touchpadEnabled) setupTouchpad();
    }

    private static final int LONG_PRESS_MS = 500;
    private static final float MOVE_SLOP_PX = 24f;
    private final Handler touchHandler = new Handler(Looper.getMainLooper());
    private Runnable longPressRunnable;
    private boolean longPressFired;
    private boolean leftDownSent;
    private float touchDownX, touchDownY;

    /** Task #14 - opt-in touch-to-mouse relay. Coordinates are normalized against the
     * SurfaceView's OWN current size, which applyLetterboxSizing() already shrinks to exactly
     * the video's content rect (centered, black bars excluded) - so a touch landing outside the
     * actual video content never reaches this listener at all, no extra letterbox math needed
     * here.
     *
     * Left-click "down" is deliberately NOT sent on ACTION_DOWN anymore - it's deferred until
     * either the finger moves past MOVE_SLOP_PX (treated as the start of a drag) or lifts
     * quickly (treated as a plain tap, down+up sent together on ACTION_UP). This is what makes
     * distinguishing a long-press possible: if neither happens before LONG_PRESS_MS, a
     * "right_click" event fires instead and no left-click ever reaches the PC for that touch. */
    private void setupTouchpad() {
        Log.i(TAG, "setupTouchpad: listener attached, surfaceView=" + surfaceView);
        surfaceView.setOnTouchListener((v, event) -> {
            if (client == null) return true;
            float xNorm = Math.max(0f, Math.min(1f, event.getX() / v.getWidth()));
            float yNorm = Math.max(0f, Math.min(1f, event.getY() / v.getHeight()));

            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    touchDownX = event.getX();
                    touchDownY = event.getY();
                    longPressFired = false;
                    leftDownSent = false;
                    if (longPressRunnable != null) touchHandler.removeCallbacks(longPressRunnable);
                    longPressRunnable = () -> {
                        longPressFired = true;
                        client.sendTouch("right_click", xNorm, yNorm);
                    };
                    touchHandler.postDelayed(longPressRunnable, LONG_PRESS_MS);
                    break;
                case MotionEvent.ACTION_MOVE:
                    if (longPressFired) break; // already resolved as a right-click, ignore the rest of this gesture
                    float dx = event.getX() - touchDownX, dy = event.getY() - touchDownY;
                    if (Math.hypot(dx, dy) > MOVE_SLOP_PX) {
                        if (longPressRunnable != null) touchHandler.removeCallbacks(longPressRunnable);
                        if (!leftDownSent) { client.sendTouch("down", xNorm, yNorm); leftDownSent = true; }
                        client.sendTouch("move", xNorm, yNorm);
                    }
                    break;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    if (longPressRunnable != null) touchHandler.removeCallbacks(longPressRunnable);
                    if (!longPressFired) {
                        if (!leftDownSent) client.sendTouch("down", xNorm, yNorm); // quick tap: down+up together
                        client.sendTouch("up", xNorm, yNorm);
                    }
                    break;
            }
            return true;
        });
    }

    /** Hides status bar + navigation bar. Re-applied on focus regain since a system swipe can bring them back. */
    private void applyImmersiveFullscreen() {
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null) {
                controller.hide(WindowInsets.Type.systemBars());
                controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        }
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) applyImmersiveFullscreen();
    }

    /** Resizes the SurfaceView to fit the screen while preserving the source's aspect ratio
     * (letterboxed on the FrameLayout's black background), instead of the default stretch-to-fill
     * that squished mismatched-aspect-ratio video in v1. */
    private void applyLetterboxSizing(int videoWidth, int videoHeight) {
        if (surfaceView == null || videoWidth <= 0 || videoHeight <= 0) return;
        runOnUiThread(() -> {
            View parent = (View) surfaceView.getParent();
            if (parent == null) return;
            int parentW = parent.getWidth();
            int parentH = parent.getHeight();
            if (parentW <= 0 || parentH <= 0) return;

            float videoAspect = (float) videoWidth / videoHeight;
            float parentAspect = (float) parentW / parentH;

            int targetW, targetH;
            if (videoAspect > parentAspect) {
                targetW = parentW;
                targetH = Math.round(parentW / videoAspect);
            } else {
                targetH = parentH;
                targetW = Math.round(parentH * videoAspect);
            }

            // Bail out if this is already the surface's current size. setLayoutParams() itself
            // triggers a new layout pass, which re-fires the OnGlobalLayoutListener added in
            // onCreate() (that's what lets this self-heal after the async immersive-fullscreen
            // animation settles) - without this guard, applying the SAME size back every time
            // creates a tight feedback loop (observed live: 30+ layout passes within 265ms), each
            // one tearing down and recreating the SurfaceView's Surface out from under the decoder
            // that's actively rendering into it. That's what was actually producing the black
            // screen + ghosted-cursor corruption on the tablet - not the sender pipeline at all.
            ViewGroup.LayoutParams currentLp = surfaceView.getLayoutParams();
            if (currentLp != null && currentLp.width == targetW && currentLp.height == targetH) return;

            FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(targetW, targetH);
            lp.gravity = android.view.Gravity.CENTER;
            surfaceView.setLayoutParams(lp);
        });
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        decoder = new H264Decoder(holder.getSurface());
        if (hasCachedConfig) {
            decoder.configure(cfgWidth, cfgHeight, cfgSps, cfgPps);
            applyLetterboxSizing(cfgWidth, cfgHeight);
            // This is a surface RE-creation (app resumed from background, window-manager churn,
            // etc.), not the first connect - the new decoder has no frames queued and the source
            // desktop might be static, so ask the server for an immediate keyframe instead of
            // waiting for its next naturally-scheduled one (was previously seen taking 8+ seconds).
            if (client != null) client.requestKeyframe();
        }
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        // Only the decoder is surface-bound; the network connection survives a surface swap.
        if (decoder != null) {
            decoder.release();
            decoder = null;
        }
    }

    @Override
    public void onConnected() {
        Log.i(TAG, "Connected to " + host + ":" + port);
    }

    @Override
    public void onVideoConfig(int width, int height, int fps, byte[] sps, byte[] pps) {
        Log.i(TAG, "VIDEO_CONFIG " + width + "x" + height + "@" + fps);
        cfgWidth = width;
        cfgHeight = height;
        cfgSps = sps;
        cfgPps = pps;
        hasCachedConfig = true;
        if (decoder != null) decoder.configure(width, height, sps, pps);
        applyLetterboxSizing(width, height);
    }

    @Override
    public void onVideoFrame(byte[] annexB, boolean keyFrame, long timestampMs) {
        if (decoder != null) decoder.submitAccessUnit(annexB, timestampMs);
    }

    @Override
    public void onDisconnected(String reason) {
        Log.w(TAG, "Disconnected: " + reason);
        Toast.makeText(this, "Disconnected: " + reason, Toast.LENGTH_LONG).show();
        // Return to the dashboard instead of leaving the user stuck on a frozen/black mirror
        // view - DashboardActivity is still on the back stack (started via plain startActivity,
        // never finish()'d itself), so finish() here naturally pops back to it.
        finish();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (client != null) client.stop();
        if (decoder != null) decoder.release();
        if (longPressRunnable != null) touchHandler.removeCallbacks(longPressRunnable);
    }
}
