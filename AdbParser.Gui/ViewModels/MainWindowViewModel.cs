using System;
// using declarations
using AdbParser.Gui.Services;
using System.Threading;
using AdbParser.Core.Execution;
using System.Threading.Tasks;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdbParser.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string status = "Ready";

    [ObservableProperty]
    private string output = string.Empty;

    [ObservableProperty]
    private string stats = string.Empty;

    [ObservableProperty]
    private string footer = string.Empty;

    [ObservableProperty]
    private string deviceStatus = string.Empty;

    [ObservableProperty]
    private string frameInfo = string.Empty;

    private readonly IAdbService _adbService;
    private readonly SemaphoreSlim _adbActionGate = new(1,1);

    public MainWindowViewModel() : this(new AdbParser.Gui.Services.AdbService()) { }

    public MainWindowViewModel(IAdbService adbService)
    {
        _adbService = adbService;
    }

    public void SetStatus(string text)
    {
        // Keep the same label format as previous code-behind: "Status: <text>"
        Status = $"Status: {text}";
    }

    public void SetStats(string text) => Stats = text;
    public void SetFooter(string text) => Footer = text;
    public void SetDeviceStatus(string text) => DeviceStatus = text;
    public void SetFrameInfo(string text) => FrameInfo = text;

    public Task<AdbParser.Core.Execution.AdbResult<object>> RunRawAsync(AdbParser.Core.Execution.AdbCommand command, string? deviceSerial = null)
        => _adbService.RunRawAsync(command, deviceSerial);

    public Task<AdbParser.Core.Execution.AdbBinaryResult> RunBinaryAsync(string command, string arguments = "", string? deviceSerial = null)
        => _adbService.RunBinaryAsync(command, arguments, deviceSerial);

    public AdbParser.Core.Execution.AdbBinaryProcess RunBinaryStream(string command, string arguments = "", string? deviceSerial = null)
        => _adbService.RunBinaryStream(command, arguments, deviceSerial);

    public void AppendOutput(string title, string body)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var block = $"[{timestamp}] {title}\n{body.TrimEnd()}\n\n";
        Output = (Output ?? string.Empty) + block;
    }

    public async Task<string> RunAdbFormattedAsync(AdbCommand command, string? deviceSerial = null)
    {
        var result = await _adbService.RunRawAsync(command, deviceSerial);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Parser: {result.ParserKey}");
        sb.AppendLine();
        sb.AppendLine("Data:");
        if (result.Data is null)
            sb.AppendLine("(null)");
        else if (result.Data is System.Collections.IEnumerable enumerable && result.Data is not string)
        {
            foreach (var item in enumerable)
                sb.AppendLine(item?.ToString());
        }
        else
        {
            sb.AppendLine(result.Data.ToString() ?? "(null)");
        }

        if (!string.IsNullOrWhiteSpace(result.RawOutput))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Raw output:");
            sb.AppendLine(result.RawOutput.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    public void ClearOutput()
    {
        Output = string.Empty;
    }

    [ObservableProperty]
    private string actionStatus = "Ready";

    public void SetActionStatus(string text)
    {
        ActionStatus = text;
    }

    public async Task RunAdbActionAsync(string label, Func<Task<string>> action)
    {
        if (!await _adbActionGate.WaitAsync(0))
        {
            SetActionStatus("Another ADB action is already running...");
            return;
        }

        try
        {
            _ = Task.Run(() => { });
            SetActionStatus($"{label} running...");
            var content = await action();
            AppendOutput(label, content);
            SetActionStatus($"{label} completed");
        }
        catch (FileNotFoundException fnf)
        {
            AppendOutput($"{label} ERROR", fnf.ToString());
            SetActionStatus($"{label} failed: adb not found");
            // propagate or handle; here we append guidance
            AppendOutput("ADB missing", fnf.Message + "\nInstall Android platform-tools or add adb to PATH.");
        }
        catch (Exception ex)
        {
            AppendOutput($"{label} ERROR", ex.ToString());
            SetActionStatus($"{label} failed: {GetFirstLine(ex.Message)}");
        }
        finally
        {
            _adbActionGate.Release();
        }
    }

    // Property change notifications are handled by CommunityToolkit.Mvvm's ObservableObject

    private static string GetFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var newlineIndex = text.IndexOfAny(new[] { '\r', '\n' });
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}
