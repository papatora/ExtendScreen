using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

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

    /// <summary>Only one AzurateMirror.Sender process is allowed to run at a time - previously
    /// nothing stopped launching a second instance on top of one already minimized to tray, which
    /// was easy to do by accident (forgetting the first one was still running/mirroring) and left
    /// two capture pipelines fighting over the same virtual display. A second launch attempt asks
    /// the existing instance to show itself instead, then exits immediately without ever creating
    /// a window.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
