using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AzurateMirror.Sender;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    // Random-ish suffix so this doesn't collide with an unrelated app's mutex/message name on the
    // same machine.
    private const string MutexName = "AzurateMirrorV2_SingleInstance_9F3E7A21";

    public static readonly int WM_ShowAzurateMirror = RegisterWindowMessage("AzurateMirrorV2_ShowRequest_9F3E7A21");

    private static Mutex? _singleInstanceMutex;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>Only one AzurateMirror.Sender process is allowed to run at a time - previously
    /// nothing stopped launching a second instance on top of one already minimized to tray, which
    /// was easy to do by accident (forgetting the first one was still running/mirroring) and left
    /// two capture pipelines fighting over the same virtual display. A second launch attempt asks
    /// the existing instance to show itself instead, then exits immediately without ever creating
    /// a window.</summary>
    // Where crash reports land - a subfolder next to the rolling session log (MainWindow.LogFilePath
    // lives directly in %TEMP%) so both are easy to find together.
    private static readonly string CrashLogDir = Path.Combine(Path.GetTempPath(), "AzurateMirrorV2_Crashes");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Root cause found live via the app's own log: without an explicit DPI-awareness
        // declaration, this process's raw GDI calls (EnumDisplaySettings et al, used by
        // VirtualDisplayManager.FindVirtualOutput) get their reported resolution VIRTUALIZED by
        // Windows - the virtual display's true native mode (2560x1600) was read back as
        // 2048x1280 (exactly 1/1.25, the monitor's 125% scaling), every single time. That made
        // ResolveTargetOutput() think the display was at the wrong resolution and try to
        // "fix" it via VirtualDisplayManager.SetDisplayMode() on EVERY client (re)connect - a
        // mode-switch attempt against a display DXGI Desktop Duplication already had an active
        // handle open on, which is exactly the kind of disruption that produces
        // DXGI_ERROR_ACCESS_LOST ("keyed mutex abandoned"). That in turn forced a full
        // encoder/VIDEO_CONFIG/Android-decoder-reconfigure cycle on nearly every connect - the
        // actual, deterministic (not flaky-GPU) cause of the black-screen/ghosting bug recurring
        // on every attempt. Declaring Per-Monitor-V2 DPI awareness here, before any GDI call in
        // the app ever runs, makes those calls report true physical pixels - the display now
        // reads as already being at its native resolution, so the disruptive "fix" never fires.
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // Checked BEFORE base.OnStartup(e): that base call is what actually creates and shows the
        // StartupUri window, so a duplicate instance has to bail out ahead of it - otherwise a
        // second MainWindow briefly exists before Shutdown() tears it back down.
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            PostMessage(HWND_BROADCAST, WM_ShowAzurateMirror, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // Previously an unhandled exception anywhere outside the pipeline thread's own try/catch
        // (which already logs "Pipeline error: ...") just silently killed the process - no crash
        // dump most of the time (WER doesn't always fire for this in practice), nothing in the
        // rolling session log either since the crash itself is what happened right after the last
        // line written. These three cover the realistic places an unhandled exception can
        // originate: any background thread, the WPF UI/Dispatcher thread, and an unobserved async
        // Task fault. Each writes a timestamped report to CrashLogDir with the full exception
        // (message/stack/inner exceptions) AND the tail of the current session's log, so what led
        // up to the crash is visible without having to reproduce it live.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
            WriteCrashLog("DispatcherUnhandledException (UI thread)", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);

        base.OnStartup(e);
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDir);
            string path = Path.Combine(CrashLogDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            string sessionLogTail = "(session log not found or unreadable)";
            try
            {
                if (File.Exists(AzurateMirror.Sender.MainWindow.LogFilePath))
                {
                    var lines = File.ReadAllLines(AzurateMirror.Sender.MainWindow.LogFilePath);
                    sessionLogTail = string.Join("\n", lines[Math.Max(0, lines.Length - 200)..]);
                }
            }
            catch { /* best-effort - the exception details below are the important part */ }

            File.WriteAllText(path,
                $"AzurateMirror.Sender crash report\n" +
                $"Time: {DateTime.Now:O}\n" +
                $"Source: {source}\n\n" +
                $"--- Exception ---\n{(ex?.ToString() ?? "(no exception object available)")}\n\n" +
                $"--- Last ~200 lines of session log before crash ---\n{sessionLogTail}\n");
        }
        catch { /* if even crash logging fails, there's nothing more we can safely do here */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
