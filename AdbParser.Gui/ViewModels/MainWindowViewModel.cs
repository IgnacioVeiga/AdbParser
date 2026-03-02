using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AdbParser.Gui.Services;
using System.Threading;
using AdbParser.Core.Execution;
using System.Threading.Tasks;
using System.IO;

namespace AdbParser.Gui.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private string _status = "Ready";
    private string _output = string.Empty;
    private string _stats = string.Empty;
    private string _footer = string.Empty;
    private string _deviceStatus = string.Empty;
    private string _frameInfo = string.Empty;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Output
    {
        get => _output;
        private set => SetProperty(ref _output, value);
    }

    public string Stats
    {
        get => _stats;
        private set => SetProperty(ref _stats, value);
    }

    public string Footer
    {
        get => _footer;
        private set => SetProperty(ref _footer, value);
    }

    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => SetProperty(ref _deviceStatus, value);
    }

    public string FrameInfo
    {
        get => _frameInfo;
        private set => SetProperty(ref _frameInfo, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private string _actionStatus = "Ready";
    public string ActionStatus
    {
        get => _actionStatus;
        private set => SetProperty(ref _actionStatus, value);
    }

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

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private static string GetFirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var newlineIndex = text.IndexOfAny(new[] { '\r', '\n' });
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}
