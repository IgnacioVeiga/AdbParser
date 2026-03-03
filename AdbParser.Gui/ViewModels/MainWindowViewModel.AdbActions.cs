using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbParser.Core.Execution;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdbParser.Gui.ViewModels;

public partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _adbActionGate = new(1, 1);

    [ObservableProperty]
    private bool isAdbActionBusy;

    [ObservableProperty]
    private string? selectedDeviceSerial;

    [ObservableProperty]
    private string selectedDeviceLabel = "Auto";

    [ObservableProperty]
    private string shellCommandText = "dumpsys display";

    [ObservableProperty]
    private string inputText = "hello from adb gui";

    [ObservableProperty]
    private string tapX = "100";

    [ObservableProperty]
    private string tapY = "100";

    [ObservableProperty]
    private string adbPathOverride = string.Empty;

    [ObservableProperty]
    private string ffmpegPathOverride = string.Empty;

    [ObservableProperty]
    private int currentFrameWidth;

    [ObservableProperty]
    private int currentFrameHeight;

    public Func<Task<string>>? RefreshDevicesOutputAction { get; set; }
    public Func<Task<string>>? ScreenshotAction { get; set; }
    public Func<Task<string>>? RecordAction { get; set; }
    public Func<string, Task>? OnAdbMissingAsync { get; set; }
    public Action? PersistSettingsAction { get; set; }

    public void SetSelectedDevice(string? serial, string label)
    {
        SelectedDeviceSerial = serial;
        SelectedDeviceLabel = label;
    }

    public void SetCurrentFrameSize(int width, int height)
    {
        CurrentFrameWidth = width;
        CurrentFrameHeight = height;
    }

    public async Task<string> GetDevicesOutputAsync()
    {
        var result = await _adbService.RunRawAsync(AdbCommand.Devices());
        return FormatParsedResult(result);
    }

    [RelayCommand]
    private async Task RunAdbActionAsync(string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            SetActionStatus("Unknown action");
            return;
        }

        var definition = ResolveAdbAction(actionKey);
        if (definition is null)
        {
            SetActionStatus($"Unknown action: {actionKey}");
            return;
        }

        if (!await _adbActionGate.WaitAsync(0))
        {
            SetActionStatus("Another ADB action is already running...");
            return;
        }

        var (label, action) = definition.Value;
        IsAdbActionBusy = true;
        SetActionStatus($"{label} running...");

        try
        {
            var content = await action();
            AppendOutput(label, content);
            SetActionStatus($"{label} completed");
        }
        catch (FileNotFoundException fnf)
        {
            AppendOutput($"{label} ERROR", fnf.ToString());
            SetActionStatus($"{label} failed: adb not found");
            AppendOutput("ADB missing", fnf.Message + "\nInstall Android platform-tools or add adb to PATH.");
            if (OnAdbMissingAsync is not null)
                await OnAdbMissingAsync(fnf.Message);
        }
        catch (Exception ex)
        {
            AppendOutput($"{label} ERROR", ex.ToString());
            SetActionStatus($"{label} failed: {GetFirstLine(ex.Message)}");
        }
        finally
        {
            IsAdbActionBusy = false;
            _adbActionGate.Release();
        }
    }

    [RelayCommand]
    private void SetShellPreset(string? shellCommand)
    {
        if (string.IsNullOrWhiteSpace(shellCommand))
            return;

        ShellCommandText = shellCommand;
        PersistSettingsAction?.Invoke();
    }

    [RelayCommand]
    private void ClearOutput()
    {
        ClearOutputText();
        SetActionStatus("Output cleared");
    }

    private (string Label, Func<Task<string>> Action)? ResolveAdbAction(string actionKey)
        => actionKey switch
        {
            "refresh-devices" => ("Refresh devices", RunDevicesActionAsync),
            "devices" => ("Devices", RunDevicesActionAsync),
            "get-prop" => ("GetProp", RunGetPropAsync),
            "packages" => ("Packages", RunPackagesAsync),
            "battery" => ("Battery", RunBatteryAsync),
            "screenshot" => ("Screenshot", () => RunHostActionAsync(ScreenshotAction, "Screenshot action is unavailable.")),
            "record-5s" => ("Record 5s", () => RunHostActionAsync(RecordAction, "Record action is unavailable.")),
            "top-activity" => ("Top Activity", () => RunNamedShellCommandAsync("Top Activity", "dumpsys activity top")),
            "display-info" => ("Display Info", () => RunNamedShellCommandAsync("Display Info", "dumpsys display")),
            "wm-size" => ("WM Size", () => RunNamedShellCommandAsync("WM Size", "wm size")),
            "wm-density" => ("WM Density", () => RunNamedShellCommandAsync("WM Density", "wm density")),
            "window-info" => ("Window Info", () => RunNamedShellCommandAsync("Window Info", "dumpsys window")),
            "rotation-settings" => ("Rotation Settings", RunRotationSettingsAsync),
            "refresh-rates" => ("Refresh Rates", RunRefreshRateSettingsAsync),
            "display-modes" => ("Display Modes", RunDisplayModesAsync),
            "user-packages" => ("User Packages", () => RunNamedShellCommandAsync("User Packages", "pm list packages -3")),
            "system-packages" => ("System Packages", () => RunNamedShellCommandAsync("System Packages", "pm list packages -s")),
            "mem-info" => ("MemInfo", () => RunNamedShellCommandAsync("MemInfo", "dumpsys meminfo")),
            "activity-stack" => ("Activities", () => RunNamedShellCommandAsync("Activities", "dumpsys activity activities")),
            "processes" => ("Processes", () => RunNamedShellCommandAsync("Processes", "ps -A")),
            "storage" => ("Storage", () => RunNamedShellCommandAsync("Storage", "df -h")),
            "send-input-text" => ("Input Text", RunInputTextAsync),
            "tap-coords" => ("Input Tap", RunTapCoordinatesAsync),
            "tap-center" => ("Input Tap Center", RunTapCenterAsync),
            "run-shell" => ("Shell", RunCustomShellAsync),
            "home" => ("Input Home", () => RunNamedShellCommandAsync("Input Home", "input keyevent KEYCODE_HOME")),
            "back" => ("Input Back", () => RunNamedShellCommandAsync("Input Back", "input keyevent KEYCODE_BACK")),
            "recents" => ("Input Recents", () => RunNamedShellCommandAsync("Input Recents", "input keyevent KEYCODE_APP_SWITCH")),
            "power" => ("Input Power", () => RunNamedShellCommandAsync("Input Power", "input keyevent KEYCODE_POWER")),
            "vol-up" => ("Volume Up", () => RunNamedShellCommandAsync("Volume Up", "input keyevent KEYCODE_VOLUME_UP")),
            "vol-down" => ("Volume Down", () => RunNamedShellCommandAsync("Volume Down", "input keyevent KEYCODE_VOLUME_DOWN")),
            "notifications" => ("Notifications", () => RunNamedShellCommandAsync("Notifications", "cmd statusbar expand-notifications")),
            "quick-settings" => ("Quick Settings", () => RunNamedShellCommandAsync("Quick Settings", "cmd statusbar expand-settings")),
            _ => null
        };

    private async Task<string> RunDevicesActionAsync()
    {
        if (RefreshDevicesOutputAction is not null)
            return await RefreshDevicesOutputAction();

        return await GetDevicesOutputAsync();
    }

    private static Task<string> RunHostActionAsync(Func<Task<string>>? action, string unavailableMessage)
    {
        if (action is null)
            throw new InvalidOperationException(unavailableMessage);

        return action();
    }

    private Task<AdbResult<object>> RunSelectedDeviceCommandAsync(AdbCommand command)
        => _adbService.RunRawAsync(command, SelectedDeviceSerial);

    private async Task<string> RunNamedShellCommandAsync(string title, string shellCommand)
    {
        _ = title;
        var result = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(shellCommand));
        return $"$ adb {BuildDeviceSelectorPreview()}shell {shellCommand}\n\n" + FormatParsedResult(result);
    }

    private async Task<string> RunShellBatchAsync(params (string Label, string Command)[] commands)
    {
        var sb = new StringBuilder();

        foreach (var (label, command) in commands)
        {
            sb.AppendLine($"## {label}");
            try
            {
                sb.AppendLine(await RunNamedShellCommandAsync(label, command));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERROR: {GetFirstLine(ex.Message)}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildDeviceSelectorPreview()
        => string.IsNullOrWhiteSpace(SelectedDeviceSerial) ? "" : $"-s {SelectedDeviceSerial} ";

    private async Task<string> RunGetPropAsync()
    {
        var result = await RunSelectedDeviceCommandAsync(AdbCommand.GetProp());
        return FormatParsedResult(result);
    }

    private async Task<string> RunPackagesAsync()
    {
        var result = await RunSelectedDeviceCommandAsync(AdbCommand.ListPackages());
        return FormatParsedResult(result);
    }

    private async Task<string> RunBatteryAsync()
    {
        var result = await RunSelectedDeviceCommandAsync(AdbCommand.Shell("dumpsys battery"));
        return FormatParsedResult(result);
    }

    private async Task<string> RunCustomShellAsync()
    {
        var shellCommand = (ShellCommandText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(shellCommand))
            throw new InvalidOperationException("Shell command is empty.");

        var result = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(shellCommand));
        return $"$ adb {BuildDeviceSelectorPreview()}shell {shellCommand}\n\n" + FormatParsedResult(result);
    }

    private async Task<string> RunInputTextAsync()
    {
        var text = (InputText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Input text is empty.");

        var encodedText = EncodeAndroidInputText(text);
        var command = $"input text \"{encodedText}\"";
        _ = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(command));

        return
            $"Text sent to device.\n" +
            $"Device: {SelectedDeviceLabel}\n" +
            $"Command: adb {BuildDeviceSelectorPreview()}shell {command}\n" +
            $"Text: {text}";
    }

    private async Task<string> RunTapCoordinatesAsync()
    {
        if (!TryParseNonNegativeInt(TapX, out var x))
            throw new InvalidOperationException("Tap X must be a non-negative integer.");
        if (!TryParseNonNegativeInt(TapY, out var y))
            throw new InvalidOperationException("Tap Y must be a non-negative integer.");

        return await RunTapAsync(x, y, "Tap coordinates");
    }

    private async Task<string> RunTapCenterAsync()
    {
        if (CurrentFrameWidth <= 0 || CurrentFrameHeight <= 0)
            throw new InvalidOperationException("No frame size available yet. Start mirror first.");

        var x = CurrentFrameWidth / 2;
        var y = CurrentFrameHeight / 2;
        return await RunTapAsync(x, y, "Tap center");
    }

    private async Task<string> RunTapAsync(int x, int y, string label)
    {
        var command = $"input tap {x} {y}";
        _ = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(command));

        return
            $"{label} sent.\n" +
            $"Device: {SelectedDeviceLabel}\n" +
            $"Command: adb {BuildDeviceSelectorPreview()}shell {command}";
    }

    private Task<string> RunRefreshRateSettingsAsync()
        => RunShellBatchAsync(
            ("peak_refresh_rate", "settings get system peak_refresh_rate"),
            ("min_refresh_rate", "settings get system min_refresh_rate"),
            ("user_refresh_rate", "settings get system user_refresh_rate"),
            ("fps_dev_override", "settings get system min_refresh_rate_for_video"));

    private Task<string> RunRotationSettingsAsync()
        => RunShellBatchAsync(
            ("accelerometer_rotation", "settings get system accelerometer_rotation"),
            ("user_rotation", "settings get system user_rotation"),
            ("wm size", "wm size"));

    private Task<string> RunDisplayModesAsync()
        => RunShellBatchAsync(
            ("dumpsys display", "dumpsys display"),
            ("cmd display get-active-display-mode", "cmd display get-active-display-mode"),
            ("cmd display get-displays", "cmd display get-displays"));

    private string FormatParsedResult(AdbResult<object> result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Parser: {result.ParserKey}");
        sb.AppendLine();
        sb.AppendLine("Data:");
        sb.Append(FormatObject(result.Data));

        if (!string.IsNullOrWhiteSpace(result.RawOutput))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Raw output:");
            sb.Append(result.RawOutput.TrimEnd());
        }

        return sb.ToString();
    }

    private static string FormatObject(object? data)
    {
        if (data is null)
            return "(null)";

        if (data is IEnumerable<string> strings)
            return string.Join(Environment.NewLine, strings);

        if (data is IReadOnlyDictionary<string, string> dict)
        {
            var sb = new StringBuilder();
            foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"{kv.Key} = {kv.Value}");
            }
            return sb.ToString().TrimEnd();
        }

        if (data is System.Collections.IEnumerable enumerable and not string)
        {
            var sb = new StringBuilder();
            foreach (var item in enumerable)
            {
                sb.AppendLine(item?.ToString());
            }
            return sb.ToString().TrimEnd();
        }

        return data.ToString() ?? "(null)";
    }

    private static bool TryParseNonNegativeInt(string? text, out int value)
    {
        if (int.TryParse(text, out value) && value >= 0)
            return true;

        value = 0;
        return false;
    }

    private static string EncodeAndroidInputText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case ' ':
                    sb.Append("%s");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '$':
                    sb.Append("\\$");
                    break;
                case '`':
                    sb.Append("\\`");
                    break;
                case '%':
                    sb.Append("\\%");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string GetFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var newlineIndex = text.IndexOfAny(['\r', '\n']);
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}
