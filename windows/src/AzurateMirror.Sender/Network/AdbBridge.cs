using System;
using System.Diagnostics;
using System.IO;

namespace AzurateMirror.Sender.Network;

/// <summary>
/// Wraps the vendored adb.exe so USB mode is genuinely "just plug in a cable" for the end user -
/// previously the app claimed USB needed no setup beyond 127.0.0.1, but nothing in the app
/// actually ran `adb reverse`, so a real user had no way to make that true without installing
/// Android platform-tools themselves and running the command manually.
/// </summary>
public static class AdbBridge
{
    private static string AdbPath => Path.Combine(AppContext.BaseDirectory, "tools", "adb", "adb.exe");

    public readonly record struct Result(bool Success, string Output);

    /// <summary>Runs `adb reverse tcp:port tcp:port` so the tablet's 127.0.0.1:port reaches this PC's listener over USB.</summary>
    public static Result SetupReverse(int port)
    {
        var (success, output) = Run($"reverse tcp:{port} tcp:{port}");
        return new Result(success, output);
    }

    /// <summary>Removes the reverse tunnel - called on Stop so a stale tunnel doesn't linger pointing at a dead listener.</summary>
    public static Result RemoveReverse(int port)
    {
        var (success, output) = Run($"reverse --remove tcp:{port}");
        return new Result(success, output);
    }

    /// <summary>True if `adb devices` lists at least one attached device - lets the UI give a clear
    /// "no device found" message instead of a generic reverse-tunnel failure.</summary>
    public static bool HasConnectedDevice()
    {
        var (success, output) = Run("devices");
        if (!success) return false;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices")) continue;
            if (trimmed.EndsWith("\tdevice") || trimmed.Contains("\tdevice")) return true;
        }
        return false;
    }

    private static (bool Success, string Output) Run(string arguments)
    {
        if (!File.Exists(AdbPath))
            return (false, $"Bundled adb.exe not found at {AdbPath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Failed to start adb.exe");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            bool success = proc.ExitCode == 0;
            string combined = (stdout + stderr).Trim();
            return (success, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
