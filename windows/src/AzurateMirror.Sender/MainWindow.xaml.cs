using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using AzurateMirror.Sender.Capture;
using AzurateMirror.Sender.Network;
using AppSettingsModel = AzurateMirror.Sender.Settings.AppSettings;

namespace AzurateMirror.Sender;

public partial class MainWindow : Window
{
    private const int Port = 47632;

    // POCO Pad tablet panel is 2560x1600 - matching the virtual display's active mode to this
    // natively avoids Android upscaling a smaller source to fill the screen (a likely major
    // contributor to the blur the user reported testing against Chrome/Discord content).
    private const int TargetWidth = 2560;
    private const int TargetHeight = 1600;
    private const int TargetRefreshHz = 60;

    private Thread? _pipelineThread;
    private volatile bool _running;
    private volatile bool _configSent;
    private volatile CaptureMode _mode = CaptureMode.Extend; // default: real virtual monitor
    private volatile bool _modeChanged;
    private volatile bool _keyframeRequested;
    private bool _loggedFirstFrameBytes;
    private MirrorServer? _server;
    // Not volatile - double isn't a valid volatile type in C#. Written from the MirrorServer's
    // read-loop thread, read from the pipeline thread; a briefly stale read is harmless for a
    // once-a-second UI display, so no synchronization is needed here.
    private double _latestLatencyMs = -1;
    private bool _loggedGpuConversionStatus;

    private readonly AppSettingsModel _appSettings = AppSettingsModel.Load();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _useUsbTransport;
    private volatile bool _touchpadGateEnabled;

    public MainWindow()
    {
        InitializeComponent();
        ChkCloseToTray.IsChecked = _appSettings.CloseToTray;
        ChkEnableTouchpad.IsChecked = _appSettings.EnableTouchpad;
        _touchpadGateEnabled = _appSettings.EnableTouchpad;
        RbUsb.IsChecked = _appSettings.UseUsbTransport;
        RbWifi.IsChecked = !_appSettings.UseUsbTransport;
        UpdatePortInfoText();
        if (Environment.GetEnvironmentVariable("AZURATEMIRROR_AUTOSTART") == "1")
            Loaded += (_, _) => BtnStart_Click(this, new RoutedEventArgs());
    }

    /// <summary>Listens for App's single-instance "please show yourself" broadcast (see
    /// App.xaml.cs) so a second launch attempt restores this window - out of the tray if it's
    /// hidden there, or just brings it to front otherwise - instead of the new process spawning
    /// its own competing capture pipeline.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (System.Windows.Interop.HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == App.WM_ShowAzurateMirror)
        {
            if (_trayIcon != null) RestoreFromTray();
            else { Show(); WindowState = WindowState.Normal; Activate(); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Shows this PC's LAN IP(s) next to the port so WiFi-mode users know exactly what
    /// to type into the tablet's Connect screen - previously this info existed nowhere in the UI,
    /// forcing users to run `ipconfig` themselves with no guidance on which adapter to read.</summary>
    private void UpdatePortInfoText()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    ips.Add(ip.ToString());
            }
        }
        catch { /* best-effort - WiFi mode still works, user just has to find their IP another way */ }

        TxtPortInfo.Text = ips.Count > 0
            ? $"Port: {Port}   |   This PC's LAN IP (for WiFi mode): {string.Join(" or ", ips)}"
            : $"Port: {Port}   |   Could not detect a LAN IP - check your network adapter.";
    }

    private void ChkEnableTouchpad_Changed(object sender, RoutedEventArgs e)
    {
        _touchpadGateEnabled = ChkEnableTouchpad.IsChecked == true;
        _appSettings.EnableTouchpad = _touchpadGateEnabled;
        _appSettings.Save();
    }

    private void ChkCloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        _appSettings.CloseToTray = ChkCloseToTray.IsChecked == true;
        _appSettings.Save();
    }

    /// <summary>
    /// X button behavior is user-configurable (Task #13): if "Minimize to tray" is checked AND a
    /// mirror session is actually running, closing the window hides it and keeps the capture
    /// pipeline alive in the background via a tray icon instead of tearing the whole app down -
    /// lets the mirror session survive an accidental/habitual click on X. But the tray hide is only
    /// worth it while something is actually running in the background - if the session is already
    /// Stopped (or never started), there's nothing left to keep alive, so X always fully closes
    /// regardless of the checkbox. This also stops the checkbox from silently trapping a
    /// stopped/idle window in the tray, which made it easy to forget an old instance was still
    /// there and launch a second one on top of it.
    /// </summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool minimizeToTray = ChkCloseToTray.IsChecked == true && _running;
        AppendLog($"Window_Closing fired: _exitRequested={_exitRequested} ChkCloseToTray.IsChecked={ChkCloseToTray.IsChecked} _running={_running}");
        if (_exitRequested || !minimizeToTray)
        {
            _trayIcon?.Dispose();
            _running = false;
            return;
        }

        e.Cancel = true;
        Hide();
        ShowTrayIcon();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && ChkCloseToTray.IsChecked == true && _running)
        {
            Hide();
            ShowTrayIcon();
        }
    }

    private void ShowTrayIcon()
    {
        if (_trayIcon != null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _exitRequested = true;
            System.Windows.Application.Current.Dispatcher.Invoke(Close);
        });

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, // placeholder until final branded icon (Task #7)
            Visible = true,
            Text = "AzurateMirror V2 - mirroring in background",
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        TxtStatus.Text = "Starting...";

        // Read UI state here, on the UI thread, before handing off to the background pipeline
        // thread - WPF controls can't be touched from a non-UI thread. Transport is a one-shot
        // read (changing it mid-session would need a reconnect anyway); the touchpad gate is a
        // separate volatile field kept live-updated by ChkEnableTouchpad_Changed instead, so
        // toggling it mid-session takes effect immediately.
        _useUsbTransport = RbUsb.IsChecked == true;
        _appSettings.UseUsbTransport = _useUsbTransport;
        _appSettings.Save();

        _pipelineThread = new Thread(PipelineLoop) { IsBackground = true, Name = "CapturePipeline" };
        _pipelineThread.SetApartmentState(ApartmentState.MTA);
        _pipelineThread.Start();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _running = false;
        BtnStop.IsEnabled = false;
    }

    /// <summary>Applies a mode requested by a newly-connected client - the pipeline loop notices
    /// via _modeChanged and re-targets DesktopDuplicator at the right DXGI output.</summary>
    private void ApplyClientMode(CaptureMode mode)
    {
        _mode = mode;
        _modeChanged = true;
        _configSent = false;
        _loggedFirstFrameBytes = false;
        AppendLog($"Active mode: {mode}");
    }

    private DxgiOutputRef ResolveTargetOutput(CaptureMode mode)
    {
        var found = mode == CaptureMode.Extend ? VirtualDisplayManager.FindVirtualOutput() : VirtualDisplayManager.FindPrimaryOutput();
        if (found is not { } f)
        {
            if (mode == CaptureMode.Extend)
                throw new InvalidOperationException("Virtual display (VDD by MTT) not found - is the driver installed and enabled? See docs/PROTOCOL.md / memory/00_LIVE_STATE.md.");
            throw new InvalidOperationException("Could not resolve the primary display via GDI.");
        }

        if (mode == CaptureMode.Extend && (f.Width != TargetWidth || f.Height != TargetHeight))
        {
            AppendLog($"Virtual display is {f.Width}x{f.Height}, switching to native {TargetWidth}x{TargetHeight}@{TargetRefreshHz}...");
            bool ok = VirtualDisplayManager.SetDisplayMode(f.GdiDeviceName, TargetWidth, TargetHeight, TargetRefreshHz);
            AppendLog(ok ? "Mode switch requested successfully, re-resolving output..." : "Mode switch call failed (mode may not be in vdd_settings.xml's <resolutions> list).");

            var refreshed = mode == CaptureMode.Extend ? VirtualDisplayManager.FindVirtualOutput() : VirtualDisplayManager.FindPrimaryOutput();
            if (refreshed is { } rf)
            {
                AppendLog($"Re-resolved: {rf.Width}x{rf.Height}");
                return rf;
            }
        }

        return f;
    }

    private void PipelineLoop()
    {
        DesktopDuplicator? duplicator = null;
        FrameEncoder? encoder = null;
        MirrorServer? server = null;
        Vortice.Direct3D11.ID3D11Texture2D? lastGoodFrame = null;
        DxgiOutputRef currentOutput;

        try
        {
            // Re-attach the virtual display to the desktop (BtnStop's cleanup detaches it - see
            // VirtualDisplayManager.DetachFromDesktop's doc comment). This is a display-topology
            // change only (same as Windows' own "Disconnect this display"), NOT a PnP device
            // toggle - no UAC, and no way to corrupt the driver's device node the way the old
            // pnputil enable/disable dance repeatedly did in practice.
            string? gdiName = VirtualDisplayManager.FindVirtualDisplayGdiName();
            if (gdiName != null)
            {
                AppendLog("Attaching virtual display to desktop...");
                if (VirtualDisplayManager.AttachToDesktop(gdiName, TargetWidth, TargetHeight, TargetRefreshHz))
                    AppendLog("Virtual display attached.");
                else
                    AppendLog("Could not attach virtual display - Extend mode will fail to find its target.");
            }
            else
            {
                AppendLog("Virtual display driver not found at all (not just detached) - is it installed? See docs/PROTOCOL.md / memory/00_LIVE_STATE.md.");
            }

            currentOutput = ResolveTargetOutput(_mode);
            duplicator = new DesktopDuplicator(currentOutput.AdapterIndex, currentOutput.OutputIndex);
            AppendLog($"Capture target: {currentOutput.AdapterDescription} [{currentOutput.GdiDeviceName}] ({duplicator.Width}x{duplicator.Height}) mode={_mode}");

            encoder = new FrameEncoder(duplicator.Width, duplicator.Height);
            AppendLog($"Hardware encoder: {encoder.EncoderName}");

            server = new MirrorServer(Port);
            server.Log += msg => AppendLog(msg);
            server.ClientHandshakeCompleted += mode => ApplyClientMode(mode);
            server.KeyframeRequested += () => { _keyframeRequested = true; AppendLog("Client requested a fresh keyframe"); };
            server.LatencyMeasured += ms => { _latestLatencyMs = ms; AppendLog($"Latency (RTT via PING/PONG): {ms:F0}ms"); };
            server.TouchReceived += (action, xNorm, yNorm) =>
            {
                // Both this PC's checkbox AND the tablet's own checkbox must be on - either side
                // can veto touch control, so a stray tap can't move this PC's mouse just because
                // one end forgot to uncheck its box.
                if (!_touchpadGateEnabled)
                {
                    AppendLog($"Touch {action} ignored - PC-side touchpad gate is off.");
                    return;
                }
                int screenX = currentOutput.OriginX + (int)Math.Round(xNorm * currentOutput.Width);
                int screenY = currentOutput.OriginY + (int)Math.Round(yNorm * currentOutput.Height);
                try
                {
                    switch (action)
                    {
                        case "down": AzurateMirror.Sender.Input.TouchInjector.TouchDown(screenX, screenY); break;
                        case "move": AzurateMirror.Sender.Input.TouchInjector.TouchMove(screenX, screenY); break;
                        case "up": AzurateMirror.Sender.Input.TouchInjector.TouchUp(screenX, screenY); break;
                        case "right_click":
                            AzurateMirror.Sender.Input.TouchInjector.RightClick(screenX, screenY);
                            AppendLog($"Touch long-press -> right-click at ({screenX},{screenY})");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Touch injection failed: {ex.Message}");
                }
            };

            if (_useUsbTransport)
            {
                AppendLog("USB mode selected - setting up adb reverse...");
                if (!Network.AdbBridge.HasConnectedDevice())
                {
                    AppendLog("No adb device found - plug in the tablet with a data cable and enable USB debugging, or switch to WiFi mode.");
                }
                else
                {
                    var reverseResult = Network.AdbBridge.SetupReverse(Port);
                    AppendLog(reverseResult.Success
                        ? $"adb reverse tcp:{Port} tcp:{Port} OK - tablet can now use 127.0.0.1."
                        : $"adb reverse failed: {reverseResult.Output}");
                }
            }

            server.Start();
            _server = server;

            SetStatus("Waiting for client...");

            _configSent = false;
            ulong sessionStartMs = (ulong)Environment.TickCount64;
            int framesThisSecond = 0;
            var fpsTimer = Stopwatch.StartNew();

            // DXGI Desktop Duplication only yields a frame when the desktop actually changes -
            // on a briefly-idle screen (nothing moving right when a client connects, or right
            // after the encoder gets recreated for any reason) that means AcquireNextFrame can
            // time out indefinitely, so no keyframe is ever produced, VIDEO_CONFIG never goes
            // out, and the receiver sits on a silent black screen with no error anywhere. Cache
            // the last successfully-captured frame and, if we still owe the client a config,
            // periodically re-encode that cached frame so a keyframe always shows up within
            // ~1s of connecting regardless of desktop activity. (Field declared at method scope
            // so the outer finally can dispose it too.)
            var forceFrameTimer = Stopwatch.StartNew();

            void EncodeAndSend(Vortice.Direct3D11.ID3D11Texture2D texture, ulong ts)
            {
                var units = encoder!.EncodeFrame(duplicator!.Device, duplicator.Context, texture, ts);

                if (server!.HasClient)
                {
                    if (!_configSent && encoder.Sps != null && encoder.Pps != null)
                    {
                        server.SendVideoConfig(duplicator.Width, duplicator.Height, 30, encoder.Sps, encoder.Pps, ts);
                        _configSent = true;
                        AppendLog("Sent VIDEO_CONFIG");
                    }

                    foreach (var unit in units)
                    {
                        if (_configSent)
                        {
                            if (!_loggedFirstFrameBytes)
                            {
                                _loggedFirstFrameBytes = true;
                                int n = Math.Min(24, unit.AnnexB.Length);
                                AppendLog($"First VIDEO_FRAME bytes ({unit.AnnexB.Length} total, key={unit.IsKeyFrame}): {BitConverter.ToString(unit.AnnexB, 0, n)}");
                            }
                            server.SendVideoFrame(unit);
                        }
                    }
                }

                framesThisSecond += units.Count;
            }

            while (_running)
            {
                if (_modeChanged)
                {
                    _modeChanged = false;
                    try
                    {
                        var newOutput = ResolveTargetOutput(_mode);
                        if (newOutput.GdiDeviceName != currentOutput.GdiDeviceName)
                        {
                            AppendLog($"Switching capture target -> {newOutput.AdapterDescription} [{newOutput.GdiDeviceName}]");
                            duplicator.Dispose();
                            encoder.Dispose();
                            currentOutput = newOutput;
                            duplicator = new DesktopDuplicator(currentOutput.AdapterIndex, currentOutput.OutputIndex);
                            encoder = new FrameEncoder(duplicator.Width, duplicator.Height);
                            lastGoodFrame?.Dispose();
                            lastGoodFrame = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Mode switch failed, staying on previous target: {ex.Message}");
                    }
                }

                // NOTE: a periodic (every few seconds) forced keyframe used to live here, based on
                // a diagnosis that turned out to be wrong - the actual ghosting/black-screen bug
                // was an infinite Android-side layout loop (fixed in MirrorActivity.java), not
                // encoder GOP corruption. Removed because it was actively harmful: setting
                // _configSent = false resends VIDEO_CONFIG, and Android's onVideoConfig() does a
                // FULL MediaCodec decoder reconfigure on every VIDEO_CONFIG - tearing down and
                // recreating the decoder's Surface connection every few seconds, which is exactly
                // what was producing the BufferQueueProducer "cancelBuffer: slot not owned by
                // producer" errors and visible corruption. _keyframeRequested is still used for a
                // genuine client resume (see TouchReceived/mode-change wiring), where a full
                // VIDEO_CONFIG + decoder reconfigure is actually correct and necessary.
                if (_keyframeRequested)
                {
                    _keyframeRequested = false;
                    // A fresh FrameEncoder's very first output is always an IDR - simplest
                    // guaranteed way to produce one on demand (the encoder has no explicit
                    // "force IDR now" call exposed through Vortice.MediaFoundation). Re-send
                    // through the cached frame immediately rather than waiting for the next
                    // natural AcquireNextFrame success, so a resumed client isn't stuck for
                    // however long the desktop happens to stay static.
                    try
                    {
                        encoder.Dispose();
                        encoder = new FrameEncoder(duplicator.Width, duplicator.Height);
                        _configSent = false;
                        _loggedFirstFrameBytes = false;
                        if (lastGoodFrame != null)
                        {
                            EncodeAndSend(lastGoodFrame, (ulong)Environment.TickCount64 - sessionStartMs);
                            forceFrameTimer.Restart();
                        }
                        AppendLog("Forced fresh keyframe for resumed client");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Forced keyframe failed: {ex.Message}");
                    }
                }

                Vortice.Direct3D11.ID3D11Texture2D? frame;
                bool timedOut;
                try
                {
                    frame = duplicator.AcquireNextFrame(500, out timedOut);
                }
                catch (Exception ex)
                {
                    // Desktop Duplication can transiently fault (e.g. DXGI_ERROR_INVALID_CALL /
                    // ACCESS_LOST after a mode change or a stalled frame). Recreate the duplicator
                    // + encoder against the same target and keep going rather than killing the session.
                    AppendLog($"Capture fault, recovering: {ex.Message}");
                    duplicator.Dispose();
                    encoder.Dispose();

                    // Recreating DesktopDuplicator (IDXGIOutput1.DuplicateOutput) can ITSELF fail -
                    // observed live as E_ACCESSDENIED, most likely the desktop being in a secure
                    // state (lock screen / UAC prompt / screensaver) at that exact moment, which
                    // DXGI refuses to duplicate by design. A single failed attempt used to propagate
                    // straight out of this catch block (it's not re-caught by its own catch),
                    // killing the whole pipeline - and the cleanup that runs afterward could ALSO
                    // fail to detach the now-orphaned virtual display, leaving it visible in Windows
                    // with nothing rendering into it: a black screen where only the DWM-composited
                    // mouse cursor (a separate hardware overlay, unaffected by the dead pipeline)
                    // still moves and ghosts. Retrying here instead of giving up after one attempt
                    // means a transient state (lock screen etc.) self-resolves once the desktop
                    // becomes capturable again, without ever tearing the session down.
                    DesktopDuplicator? recreated = null;
                    int attempt = 0;
                    while (_running && recreated == null)
                    {
                        attempt++;
                        Thread.Sleep(attempt == 1 ? 200 : 2000);
                        try
                        {
                            recreated = new DesktopDuplicator(currentOutput.AdapterIndex, currentOutput.OutputIndex);
                        }
                        catch (Exception retryEx)
                        {
                            AppendLog($"Recovery attempt {attempt} failed, retrying: {retryEx.Message}");
                        }
                    }
                    if (recreated == null) return; // _running went false while retrying - normal Stop, not a crash.

                    duplicator = recreated;
                    encoder = new FrameEncoder(duplicator.Width, duplicator.Height);
                    lastGoodFrame?.Dispose();
                    lastGoodFrame = null;
                    _configSent = false;
                    AppendLog($"Recovered after {attempt} attempt(s). Capture target: {currentOutput.AdapterDescription} [{currentOutput.GdiDeviceName}]");
                    continue;
                }

                ulong nowMs = (ulong)Environment.TickCount64 - sessionStartMs;

                if (timedOut || frame is null)
                {
                    // Static desktop: no new frame. If the client still has no VIDEO_CONFIG, force
                    // one through using the last frame we did capture, throttled to ~1/sec so an
                    // idle screen doesn't spin the encoder pointlessly once config has gone out.
                    if (!_configSent && lastGoodFrame != null && forceFrameTimer.ElapsedMilliseconds >= 1000)
                    {
                        forceFrameTimer.Restart();
                        EncodeAndSend(lastGoodFrame, nowMs);
                    }
                    continue;
                }

                try
                {
                    if (lastGoodFrame is null || lastGoodFrame.Description.Width != (uint)duplicator.Width || lastGoodFrame.Description.Height != (uint)duplicator.Height)
                    {
                        lastGoodFrame?.Dispose();
                        var cacheDesc = new Vortice.Direct3D11.Texture2DDescription
                        {
                            Width = (uint)duplicator.Width,
                            Height = (uint)duplicator.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                            Usage = Vortice.Direct3D11.ResourceUsage.Default,
                            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget,
                            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                            // GdiCompatible lets CursorCompositor draw onto this texture via a
                            // classic GetDC/DrawIconEx trick below - DXGI Desktop Duplication
                            // never bakes the mouse cursor into captured frames (it's a separate
                            // DWM hardware overlay), so without this every frame would be
                            // cursor-less regardless of mode.
                            MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.GdiCompatible
                        };
                        lastGoodFrame = duplicator.Device.CreateTexture2D(cacheDesc);
                    }
                    duplicator.Context.CopyResource(lastGoodFrame, frame);

                    try
                    {
                        CursorCompositor.DrawCursorOnto(lastGoodFrame, currentOutput.OriginX, currentOutput.OriginY);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Cursor composite failed (non-fatal): {ex.Message}");
                    }

                    EncodeAndSend(lastGoodFrame, nowMs);
                }
                finally
                {
                    frame.Dispose();
                    duplicator.ReleaseFrame();
                }

                if (!_loggedGpuConversionStatus && encoder.UsingGpuConversion is bool usingGpu)
                {
                    _loggedGpuConversionStatus = true;
                    AppendLog(usingGpu
                        ? "Color conversion: GPU (VideoProcessorBlt) - active"
                        : "Color conversion: CPU (GPU path permanently disabled - see FrameEncoder.cs)");
                }

                if (fpsTimer.ElapsedMilliseconds >= 1000)
                {
                    bool hasClient = server.HasClient;
                    string latencyPart = _latestLatencyMs >= 0 ? $", {_latestLatencyMs:F0} ms" : "";
                    AppendLog($"encode fps: {framesThisSecond}  mode: {_mode}  target: {currentOutput.GdiDeviceName} ({duplicator.Width}x{duplicator.Height})  client: {(hasClient ? "connected" : "waiting")}{(hasClient ? $"  latency: {(_latestLatencyMs >= 0 ? $"{_latestLatencyMs:F0}ms" : "measuring...")}" : "")}");
                    SetStatus(hasClient ? $"Connected ({framesThisSecond} fps{latencyPart})" : "Waiting for client...");
                    UpdateTransportIndicator(hasClient ? server.ClientRemoteAddress : null);
                    if (hasClient) server.SendPing();
                    framesThisSecond = 0;
                    fpsTimer.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Pipeline error: {ex}");
        }
        finally
        {
            server?.Dispose();
            encoder?.Dispose();
            duplicator?.Dispose();
            lastGoodFrame?.Dispose();
            _server = null;

            if (_useUsbTransport)
            {
                var removeResult = Network.AdbBridge.RemoveReverse(Port);
                AppendLog(removeResult.Success ? "adb reverse tunnel removed." : $"adb reverse --remove failed (non-fatal): {removeResult.Output}");
            }

            // Detach the virtual display from the desktop now that capture has fully torn down -
            // otherwise it sits in Windows as "display 4" indefinitely with nothing rendering
            // into it (the mouse-wanders-into-a-dead-display complaint this exists to fix).
            // Topology-only change (like Windows' own "Disconnect this display"), NOT a PnP
            // device toggle - no UAC, and can't corrupt the driver's device node.
            string? gdiNameToDetach = VirtualDisplayManager.FindVirtualDisplayGdiName();
            if (gdiNameToDetach != null)
            {
                AppendLog("Detaching virtual display from desktop...");
                // Can transiently fail for the same reason capture recovery above can (desktop in
                // a secure state right when a crash unwound into this cleanup) - a single failed
                // attempt used to just give up and leave the display orphaned/attached with
                // nothing rendering into it. A few retries covers the transient case without
                // blocking Stop/exit indefinitely if something is genuinely wrong.
                bool detached = false;
                for (int i = 0; i < 3 && !detached; i++)
                {
                    if (i > 0) Thread.Sleep(500);
                    detached = VirtualDisplayManager.DetachFromDesktop(gdiNameToDetach);
                }
                AppendLog(detached
                    ? "Virtual display detached."
                    : "Could not detach virtual display after 3 attempts - display 4 will stay visible until next Start.");
            }

            SetStatus("Stopped");
            Dispatcher.Invoke(() =>
            {
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled = false;
            });
        }
    }

    private void SetStatus(string text) => Dispatcher.Invoke(() => TxtStatus.Text = text);

    private string? _lastTransportShown;

    /// <summary>
    /// Shows which transport is actually active, auto-detected from the connected client's IP
    /// rather than a user-facing toggle (the PC side can't choose transport - that's decided by
    /// which host address the Android app dials). Wording is based on real measurements taken
    /// this session via the PING/PONG round-trip (docs/PROTOCOL.md), NOT assumed: USB (adb
    /// reverse, 127.0.0.1) measured ~11-49ms and consistent; WiFi measured ~30-540ms and highly
    /// variable (PING/PONG shares the same TCP stream as VIDEO_FRAME data, so it reflects queuing
    /// behind video traffic, not a clean network-only ping). Explicitly avoids ever saying "zero
    /// latency" for USB - that was never measured and would be a fabricated claim.
    /// </summary>
    private void UpdateTransportIndicator(string? clientIp)
    {
        string text = clientIp switch
        {
            null => "",
            "127.0.0.1" => "USB mode - cable, measured ~11-49ms, consistent",
            _ => "WiFi mode - wireless, measured ~30-540ms, varies with network load",
        };
        if (text == _lastTransportShown) return;
        _lastTransportShown = text;
        Dispatcher.Invoke(() => TxtTransport.Text = text);
    }

    // Internal (not private) so App.xaml.cs's crash handlers can read the session log's tail into
    // the crash report - the whole point is being able to see what was happening right before an
    // unhandled exception took the process down.
    internal static readonly string LogFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "azuratemirror_v2_test.log");

    // (regex, brush) pairs checked in priority order - fps=green, latency=orange, connected=green,
    // waiting=amber, error/failed/fault=red (claims the rest of the line), timestamp=dim gray.
    // Earlier rules claim their matched span first so a later broad rule (like the error one)
    // can't paint over text an earlier, more specific rule already colored.
    private static readonly (System.Text.RegularExpressions.Regex Rx, System.Windows.Media.Brush Brush)[] LogHighlightRules =
    {
        (new System.Text.RegularExpressions.Regex(@"^\[[\d:.]+\]"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x77, 0x77, 0x77))),
        (new System.Text.RegularExpressions.Regex(@"(?<=fps: )\d+"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0xC3, 0x4A))),
        (new System.Text.RegularExpressions.Regex(@"(?<=latency: )(\d+ms|measuring\.\.\.)"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA7, 0x26))),
        (new System.Text.RegularExpressions.Regex(@"(?<=, )\d+ ms"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA7, 0x26))),
        (new System.Text.RegularExpressions.Regex(@"client: connected"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x6A))),
        (new System.Text.RegularExpressions.Regex(@"client: waiting"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xCA, 0x28))),
        (new System.Text.RegularExpressions.Regex(@"(?i)\b(error|failed|fault)\b.*$"), new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50))),
    };

    private static readonly System.Windows.Media.Brush DefaultLogBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD));

    private void AppendLog(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        try { System.IO.File.AppendAllText(LogFilePath, stamped + "\n"); } catch { }
        Dispatcher.Invoke(() =>
        {
            var para = new System.Windows.Documents.Paragraph();
            foreach (var (text, brush) in SplitIntoColoredSpans(stamped))
                para.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = brush });
            TxtLog.Document.Blocks.Add(para);
            TxtLog.ScrollToEnd();
        });
    }

    /// <summary>Walks a log line left-to-right, carving out spans matched by LogHighlightRules
    /// (first rule to claim a span wins) and returning (text, brush) pieces in original order -
    /// plain-text runs in between get the default log color.</summary>
    private static IEnumerable<(string Text, System.Windows.Media.Brush Brush)> SplitIntoColoredSpans(string line)
    {
        var claimed = new bool[line.Length];
        var spans = new List<(int Start, int Length, System.Windows.Media.Brush Brush)>();

        foreach (var (rx, brush) in LogHighlightRules)
        {
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(line))
            {
                if (m.Length == 0) continue;
                bool overlaps = false;
                for (int i = m.Index; i < m.Index + m.Length; i++)
                    if (claimed[i]) { overlaps = true; break; }
                if (overlaps) continue;

                for (int i = m.Index; i < m.Index + m.Length; i++) claimed[i] = true;
                spans.Add((m.Index, m.Length, brush));
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        int pos = 0;
        foreach (var (start, length, brush) in spans)
        {
            if (start > pos) yield return (line[pos..start], DefaultLogBrush);
            yield return (line.Substring(start, length), brush);
            pos = start + length;
        }
        if (pos < line.Length) yield return (line[pos..], DefaultLogBrush);
    }
}
