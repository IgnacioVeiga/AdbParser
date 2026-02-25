using System;
using System.IO;
using System.Text.Json;

namespace AdbParser.Gui;

internal static class GuiUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(GetAppConfigDirectory(), "gui-settings.json");

    public static GuiUserSettings LoadOrDefault()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return new GuiUserSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GuiUserSettings>(json, JsonOptions) ?? new GuiUserSettings();
        }
        catch
        {
            return new GuiUserSettings();
        }
    }

    public static void Save(GuiUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var path = SettingsPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid settings path.");

        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string GetAppConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = string.IsNullOrWhiteSpace(home)
                ? AppContext.BaseDirectory
                : Path.Combine(home, ".config");
        }

        return Path.Combine(appData, "AdbParser");
    }
}
