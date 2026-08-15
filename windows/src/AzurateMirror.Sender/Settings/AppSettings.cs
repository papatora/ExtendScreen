using System;
using System.IO;
using System.Text.Json;

namespace AzurateMirror.Sender.Settings;

public class AppSettings
{
    public bool CloseToTray { get; set; } = false;
    public bool EnableTouchpad { get; set; } = false;
    public bool UseUsbTransport { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AzurateMirror", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { /* fall through to defaults - a corrupt settings file shouldn't block startup */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* non-fatal - worst case the toggle doesn't persist across restarts */ }
    }
}
