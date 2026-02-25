using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AdbParser.Core.Execution;
using AdbParser.Core.Parsers;
using AdbParser.Core.Registry;
using AdbParser.Core.Screen;
using AdbParser.Core.Video;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdbParser.Gui;

public partial class MainWindow : Window
{
    private sealed class DeviceChoice
    {
        public string Label { get; init; } = "";
        public string? Serial { get; init; }
        public string Status { get; init; } = "";
        public override string ToString() => Label;
    }

    private static readonly (string Label, int? Width, int? Height)[] ResolutionPresets =
    [
        ("Native", null, null),
        ("1280x720", 1280, 720),
        ("960x540", 960, 540),
        ("854x480", 854, 480),
        ("640x360", 640, 360)
    ];

    private static readonly (string Label, int BitRate)[] BitratePresets =
    [
        ("12 Mbps", 12_000_000),
        ("8 Mbps", 8_000_000),
        ("5 Mbps", 5_000_000),
        ("3 Mbps", 3_000_000),
        ("2 Mbps", 2_000_000)
    ];

    private static readonly (string Label, int? FpsCap)[] RenderFpsPresets =
    [
        ("Unlimited", null),
        ("240", 240),
        ("165", 165),
        ("144", 144),
        ("120", 120),
        ("90", 90),
        ("75", 75),
        ("60", 60),
        ("30", 30),
        ("20", 20),
        ("15", 15)
    ];

    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _cts;
    private Task? _producerTask;
    private Task? _consumerTask;
    private Task? _sessionMonitorTask;

    private bool _isStopping;
    private bool _isClosing;
    private bool _adbActionBusy;

    private readonly SemaphoreSlim _streamOpsGate = new(1, 1);
    private readonly SemaphoreSlim _adbActionGate = new(1, 1);

    private long _decodedFrameCount;
    private long _renderedFrameCount;
    private long _lastDecodedFrameCount;
    private long _lastRenderedFrameCount;
    private double _decodedFps;
    private double _renderedFps;
    private DateTime _lastStatsSampleUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _statsTimer;
    private readonly List<DeviceChoice> _deviceChoices = [];

    private int _currentFrameWidth;
    private int _currentFrameHeight;
    private long _lastRenderTicksUtc;
    private int? _renderFpsCap;
    private bool _parsersRegistered;

    public MainWindow()
    {
        InitializeComponent();

        EnsureParsersRegistered();
        ConfigureControls();
        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statsTimer.Tick += (_, _) => RefreshStats();

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _statsTimer.Start();
        UpdateControlState();
        UpdateStatsText();
        UpdateFooter();
        SetStatus("Ready. Choose an action or start mirror.");
        await RefreshDevicesListAsync(selectFirstOnline: true);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _statsTimer.Stop();
        await RunStreamOpAsync(() => StopStreamingAsync(windowClosing: true));
    }

    private void ConfigureControls()
    {
        DeviceCombo.ItemsSource = _deviceChoices;
        DeviceCombo.SelectionChanged += (_, _) =>
        {
            UpdateDeviceStatus();
            UpdateFooter();
        };
        RefreshDevicesButton.Click += async (_, _) =>
            await RunAdbActionAsync("Refresh devices", RefreshDevicesOutputAsync);

        ResolutionCombo.ItemsSource = Array.ConvertAll(ResolutionPresets, p => p.Label);
        BitrateCombo.ItemsSource = Array.ConvertAll(BitratePresets, p => p.Label);
        RenderFpsCombo.ItemsSource = Array.ConvertAll(RenderFpsPresets, p => p.Label);

        ResolutionCombo.SelectedIndex = 0;
        BitrateCombo.SelectedIndex = 1; // 8 Mbps default
        RenderFpsCombo.SelectedIndex = 0; // Unlimited (lower latency)

        StartButton.Click += async (_, _) => await RunStreamOpAsync(StartStreamingAsync);
        StopButton.Click += async (_, _) => await RunStreamOpAsync(() => StopStreamingAsync());
        ReconnectButton.Click += async (_, _) => await RunStreamOpAsync(ReconnectAsync);

        ResolutionCombo.SelectionChanged += (_, _) => UpdateFooter();
        BitrateCombo.SelectionChanged += (_, _) => UpdateFooter();
        RenderFpsCombo.SelectionChanged += (_, _) => UpdateFooter();

        DevicesButton.Click += async (_, _) => await RunAdbActionAsync("Devices", RefreshDevicesOutputAsync);
        GetPropButton.Click += async (_, _) => await RunAdbActionAsync("GetProp", RunGetPropAsync);
        PackagesButton.Click += async (_, _) => await RunAdbActionAsync("Packages", RunPackagesAsync);
        BatteryButton.Click += async (_, _) => await RunAdbActionAsync("Battery", RunBatteryAsync);
        ScreenshotButton.Click += async (_, _) => await RunAdbActionAsync("Screenshot", RunScreenshotAsync);
        RecordButton.Click += async (_, _) => await RunAdbActionAsync("Record 5s", RunRecord5sAsync);
        TopActivityButton.Click += async (_, _) => await RunAdbActionAsync("Top Activity", () => RunNamedShellCommandAsync("Top Activity", "dumpsys activity top"));
        DisplayInfoButton.Click += async (_, _) => await RunAdbActionAsync("Display Info", () => RunNamedShellCommandAsync("Display Info", "dumpsys display"));

        WmSizeButton.Click += async (_, _) => await RunAdbActionAsync("WM Size", () => RunNamedShellCommandAsync("WM Size", "wm size"));
        WmDensityButton.Click += async (_, _) => await RunAdbActionAsync("WM Density", () => RunNamedShellCommandAsync("WM Density", "wm density"));
        WindowInfoButton.Click += async (_, _) => await RunAdbActionAsync("Window Info", () => RunNamedShellCommandAsync("Window Info", "dumpsys window"));
        RotationSettingsButton.Click += async (_, _) => await RunAdbActionAsync("Rotation Settings", RunRotationSettingsAsync);
        RefreshRatesButton.Click += async (_, _) => await RunAdbActionAsync("Refresh Rates", RunRefreshRateSettingsAsync);
        DisplayModesButton.Click += async (_, _) => await RunAdbActionAsync("Display Modes", RunDisplayModesAsync);

        UserPackagesButton.Click += async (_, _) => await RunAdbActionAsync("User Packages", () => RunNamedShellCommandAsync("User Packages", "pm list packages -3"));
        SystemPackagesButton.Click += async (_, _) => await RunAdbActionAsync("System Packages", () => RunNamedShellCommandAsync("System Packages", "pm list packages -s"));
        MemInfoButton.Click += async (_, _) => await RunAdbActionAsync("MemInfo", () => RunNamedShellCommandAsync("MemInfo", "dumpsys meminfo"));
        ActivityStackButton.Click += async (_, _) => await RunAdbActionAsync("Activities", () => RunNamedShellCommandAsync("Activities", "dumpsys activity activities"));
        ProcessesButton.Click += async (_, _) => await RunAdbActionAsync("Processes", () => RunNamedShellCommandAsync("Processes", "ps -A"));
        StorageButton.Click += async (_, _) => await RunAdbActionAsync("Storage", () => RunNamedShellCommandAsync("Storage", "df -h"));

        HomeButton.Click += async (_, _) => await RunAdbActionAsync("Input Home", () => RunNamedShellCommandAsync("Input Home", "input keyevent KEYCODE_HOME"));
        BackButton.Click += async (_, _) => await RunAdbActionAsync("Input Back", () => RunNamedShellCommandAsync("Input Back", "input keyevent KEYCODE_BACK"));
        RecentsButton.Click += async (_, _) => await RunAdbActionAsync("Input Recents", () => RunNamedShellCommandAsync("Input Recents", "input keyevent KEYCODE_APP_SWITCH"));
        PowerButton.Click += async (_, _) => await RunAdbActionAsync("Input Power", () => RunNamedShellCommandAsync("Input Power", "input keyevent KEYCODE_POWER"));
        VolUpButton.Click += async (_, _) => await RunAdbActionAsync("Volume Up", () => RunNamedShellCommandAsync("Volume Up", "input keyevent KEYCODE_VOLUME_UP"));
        VolDownButton.Click += async (_, _) => await RunAdbActionAsync("Volume Down", () => RunNamedShellCommandAsync("Volume Down", "input keyevent KEYCODE_VOLUME_DOWN"));
        NotificationsButton.Click += async (_, _) => await RunAdbActionAsync("Notifications", () => RunNamedShellCommandAsync("Notifications", "cmd statusbar expand-notifications"));
        QuickSettingsButton.Click += async (_, _) => await RunAdbActionAsync("Quick Settings", () => RunNamedShellCommandAsync("Quick Settings", "cmd statusbar expand-settings"));

        RunShellButton.Click += async (_, _) => await RunAdbActionAsync("Shell", RunCustomShellAsync);
        ShellDisplayQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys display";
        ShellWindowQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys window";
        ShellBatteryQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys battery";
        ShellSurfaceQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys SurfaceFlinger";
        ClearOutputButton.Click += (_, _) => ClearOutput();

        FrameInfoText.Text = "No frame yet";
        ActionStatusText.Text = "Ready";
        DeviceStatusText.Text = "Devices: not loaded";
        SetStatus("Disconnected");
    }

    private void EnsureParsersRegistered()
    {
        if (_parsersRegistered)
            return;

        AdbParserSetup.RegisterParsers();
        _parsersRegistered = true;
    }

    private string? GetSelectedDeviceSerial()
        => (DeviceCombo.SelectedItem as DeviceChoice)?.Serial;

    private string GetSelectedDeviceLabel()
        => (DeviceCombo.SelectedItem as DeviceChoice)?.Label ?? "Auto";

    private async Task RefreshDevicesListAsync(bool selectFirstOnline = false)
    {
        var previousSelectedSerial = GetSelectedDeviceSerial();

        var result = await AdbExecutor.RunAsync(AdbCommand.Devices());
        var devices = result.Data as List<DeviceInfo> ?? [];

        _deviceChoices.Clear();
        _deviceChoices.Add(new DeviceChoice
        {
            Label = "Auto (adb default)",
            Serial = null,
            Status = "auto"
        });

        foreach (var device in devices.OrderBy(d => d.Serial, StringComparer.Ordinal))
        {
            var status = string.IsNullOrWhiteSpace(device.Status) ? "unknown" : device.Status;
            _deviceChoices.Add(new DeviceChoice
            {
                Label = $"{device.Serial} ({status})",
                Serial = device.Serial,
                Status = status
            });
        }

        DeviceCombo.ItemsSource = null;
        DeviceCombo.ItemsSource = _deviceChoices;

        DeviceChoice? selection = null;
        if (!string.IsNullOrWhiteSpace(previousSelectedSerial))
        {
            selection = _deviceChoices.FirstOrDefault(d =>
                string.Equals(d.Serial, previousSelectedSerial, StringComparison.Ordinal));
        }

        if (selection is null && selectFirstOnline)
        {
            selection = _deviceChoices.FirstOrDefault(d =>
                string.Equals(d.Status, "device", StringComparison.OrdinalIgnoreCase));
        }

        DeviceCombo.SelectedItem = selection ?? _deviceChoices[0];
        UpdateDeviceStatus();
    }

    private async Task<string> RefreshDevicesOutputAsync()
    {
        await RefreshDevicesListAsync(selectFirstOnline: false);
        return await RunDevicesAsync();
    }

    private void UpdateDeviceStatus()
    {
        var online = _deviceChoices.Count(d =>
            !string.IsNullOrWhiteSpace(d.Serial) &&
            string.Equals(d.Status, "device", StringComparison.OrdinalIgnoreCase));
        var total = _deviceChoices.Count - 1; // exclude auto
        var selected = GetSelectedDeviceLabel();

        DeviceStatusText.Text = $"Devices: {online}/{total} online | Selected: {selected}";
    }

    private async Task<AdbResult<object>> RunSelectedDeviceCommandAsync(AdbCommand command)
        => await AdbExecutor.RunAsync(command, GetSelectedDeviceSerial());

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
                sb.AppendLine($"ERROR: {FirstLine(ex.Message)}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildDeviceSelectorPreview()
    {
        var serial = GetSelectedDeviceSerial();
        return string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
    }

    private async Task RunStreamOpAsync(Func<Task> operation)
    {
        await _streamOpsGate.WaitAsync();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                SetStatus($"UI error: {FirstLine(ex.Message)}");
            }
        }
        finally
        {
            _streamOpsGate.Release();
        }
    }

    private async Task RunAdbActionAsync(string label, Func<Task<string>> action)
    {
        if (_isClosing)
            return;

        if (!await _adbActionGate.WaitAsync(0))
        {
            SetActionStatus("Another ADB action is already running...");
            return;
        }

        _adbActionBusy = true;
        UpdateControlState();
        SetActionStatus($"{label} running...");

        try
        {
            var content = await action();
            AppendOutput(label, content);
            SetActionStatus($"{label} completed");
        }
        catch (Exception ex)
        {
            var message = FirstLine(ex.Message);
            AppendOutput($"{label} ERROR", ex.ToString());
            SetActionStatus($"{label} failed: {message}");
        }
        finally
        {
            _adbActionBusy = false;
            UpdateControlState();
            _adbActionGate.Release();
        }
    }

    private async Task<string> RunDevicesAsync()
    {
        var result = await AdbExecutor.RunAsync(AdbCommand.Devices());
        return FormatParsedResult(result);
    }

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
        var shellCommand = (ShellCommandTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(shellCommand))
            throw new InvalidOperationException("Shell command is empty.");

        var result = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(shellCommand));
        return $"$ adb {BuildDeviceSelectorPreview()}shell {shellCommand}\n\n" + FormatParsedResult(result);
    }

    private async Task<string> RunScreenshotAsync()
    {
        var result = await AdbExecutor.RunBinaryAsync(
            "exec-out",
            "screencap -p",
            GetSelectedDeviceSerial()
        );

        var fileName = $"screen_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        await using var file = File.Create(fileName);
        await result.DataStream.CopyToAsync(file);

        return $"Screenshot saved to {fileName}\nDevice: {GetSelectedDeviceLabel()}";
    }

    private async Task<string> RunRecord5sAsync()
    {
        var fileName = $"record_{DateTime.Now:yyyyMMdd_HHmmss}.h264";
        using var adb = AdbExecutor.RunBinaryStream(
            "exec-out",
            "screenrecord --output-format=h264 -",
            GetSelectedDeviceSerial()
        );

        await using var file = File.Create(fileName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await adb.Output.CopyToAsync(file, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected after 5 seconds.
        }

        return $"Recorded ~5s to {fileName}\nDevice: {GetSelectedDeviceLabel()}";
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

    private void AppendOutput(string title, string body)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var block = $"[{timestamp}] {title}\n{body.TrimEnd()}\n\n";
        OutputTextBox.Text = (OutputTextBox.Text ?? string.Empty) + block;
        OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
    }

    private void ClearOutput()
    {
        OutputTextBox.Text = string.Empty;
        SetActionStatus("Output cleared");
    }

    private void SetActionStatus(string text)
    {
        ActionStatusText.Text = text;
    }

    private async Task StartStreamingAsync()
    {
        if (_isClosing)
            return;

        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            SetStatus("Already streaming");
            return;
        }

        // Clean up any previous canceled/stopped session state.
        await StopStreamingAsync();

        var options = BuildScreenStreamOptions();
        _renderFpsCap = GetSelectedRenderFpsCap();

        ResetSessionStats();
        UpdateFooter();
        SetStatus("Initializing FFmpeg...");
        UpdateControlState();

        FfmpegH264Decoder decoder;
        AdbScreenStreamService service;
        try
        {
            decoder = new FfmpegH264Decoder();
            service = new AdbScreenStreamService(decoder);
        }
        catch (Exception ex)
        {
            SetStatus($"FFmpeg error: {FirstLine(ex.Message)}");
            Title = "ADB Screen Test - Error";
            UpdateControlState();
            return;
        }

        var cts = new CancellationTokenSource();
        var buffer = new VideoFrameBuffer();

        _cts = cts;
        _isStopping = false;
        SetStatus($"Connecting to {GetSelectedDeviceLabel()}...");
        UpdateControlState();

        _producerTask = RunProducerAsync(service, decoder, buffer, options, cts);
        _consumerTask = RunConsumerAsync(buffer, cts.Token);
        _sessionMonitorTask = MonitorSessionAsync(cts, _producerTask, _consumerTask);
    }

    private async Task StopStreamingAsync(bool windowClosing = false)
    {
        var cts = _cts;
        if (cts is null)
        {
            if (!windowClosing)
            {
                _isStopping = false;
                UpdateControlState();
            }
            return;
        }

        _isStopping = true;
        if (!windowClosing)
        {
            SetStatus("Stopping...");
        }
        UpdateControlState();

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Ignore if cancellation token source is already disposed/canceled.
        }

        try
        {
            await Task.WhenAll(
                _producerTask ?? Task.CompletedTask,
                _consumerTask ?? Task.CompletedTask
            );
        }
        catch (OperationCanceledException)
        {
            // Expected during stop.
        }
        catch (Exception ex)
        {
            if (!windowClosing)
            {
                SetStatus($"Stop error: {FirstLine(ex.Message)}");
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                ReleaseSessionState(cts);
                if (!windowClosing && !StatusHasError())
                {
                    SetStatus("Stopped");
                }
            }

            if (!windowClosing)
            {
                UpdateControlState();
            }
        }
    }

    private async Task ReconnectAsync()
    {
        await StopStreamingAsync();
        await StartStreamingAsync();
    }

    private async Task RunProducerAsync(
        AdbScreenStreamService service,
        FfmpegH264Decoder decoder,
        VideoFrameBuffer buffer,
        ScreenStreamOptions options,
        CancellationTokenSource cts)
    {
        try
        {
            await foreach (var frame in service.StartStream(options, cts.Token))
            {
                Interlocked.Increment(ref _decodedFrameCount);
                await buffer.WriteAsync(frame, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping the stream.
        }
        catch (Exception ex)
        {
            ReportWorkerError(cts, $"Stream error: {FirstLine(ex.Message)}");
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private async Task RunConsumerAsync(VideoFrameBuffer buffer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await buffer.ReadAsync(cancellationToken);

                await ApplyRenderThrottleAsync(cancellationToken);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    EnsureBitmap(frame);
                    RenderFrame(frame);

                    _currentFrameWidth = frame.Width;
                    _currentFrameHeight = frame.Height;
                    FrameInfoText.Text = $"{frame.Width}x{frame.Height} | {FormatBytes(frame.Data.Length)} BGRA";

                    Interlocked.Increment(ref _renderedFrameCount);
                    Interlocked.Exchange(ref _lastRenderTicksUtc, DateTime.UtcNow.Ticks);

                    if (!StatusHasError())
                    {
                        SetStatus("Streaming...");
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping.
        }
        catch (Exception ex)
        {
            var cts = _cts;
            if (cts is not null)
            {
                ReportWorkerError(cts, $"Render error: {FirstLine(ex.Message)}");
            }
        }
    }

    private async Task MonitorSessionAsync(
        CancellationTokenSource sessionCts,
        Task producerTask,
        Task consumerTask)
    {
        try
        {
            await Task.WhenAll(producerTask, consumerTask);
        }
        catch
        {
            // Errors are reported by worker tasks and translated into status text.
        }
        finally
        {
            if (!_isClosing)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(_cts, sessionCts))
                            return;

                        ReleaseSessionState(sessionCts);

                        if (!StatusHasError())
                        {
                            SetStatus("Stopped");
                        }

                        UpdateControlState();
                    });
                }
                catch
                {
                    // Window may be closing/disposed.
                }
            }
        }
    }

    private void ReportWorkerError(CancellationTokenSource cts, string message)
    {
        try
        {
            cts.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }

        if (_isClosing)
            return;

        try
        {
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus(message);
                AppendOutput("Mirror error", message);
            });
        }
        catch
        {
            // Window may be closing/disposed.
        }
    }

    private async Task ApplyRenderThrottleAsync(CancellationToken cancellationToken)
    {
        if (_renderFpsCap is not > 0)
            return;

        var minIntervalTicks = TimeSpan.FromSeconds(1d / _renderFpsCap.Value).Ticks;
        var lastRenderTicks = Interlocked.Read(ref _lastRenderTicksUtc);
        if (lastRenderTicks <= 0)
            return;

        var nowTicks = DateTime.UtcNow.Ticks;
        var targetTicks = lastRenderTicks + minIntervalTicks;
        if (targetTicks <= nowTicks)
            return;

        var wait = TimeSpan.FromTicks(targetTicks - nowTicks);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    private ScreenStreamOptions BuildScreenStreamOptions()
    {
        var resolution = ResolutionPresets[ClampIndex(ResolutionCombo.SelectedIndex, ResolutionPresets.Length)];
        var bitrate = BitratePresets[ClampIndex(BitrateCombo.SelectedIndex, BitratePresets.Length)];
        var renderCap = GetSelectedRenderFpsCap();

        return new ScreenStreamOptions
        {
            DeviceSerial = GetSelectedDeviceSerial(),
            Width = resolution.Width,
            Height = resolution.Height,
            BitRate = bitrate.BitRate,
            MaxFps = renderCap ?? 30
        };
    }

    private int? GetSelectedRenderFpsCap()
    {
        var preset = RenderFpsPresets[ClampIndex(RenderFpsCombo.SelectedIndex, RenderFpsPresets.Length)];
        return preset.FpsCap;
    }

    private void ResetSessionStats()
    {
        Interlocked.Exchange(ref _decodedFrameCount, 0);
        Interlocked.Exchange(ref _renderedFrameCount, 0);
        Interlocked.Exchange(ref _lastRenderTicksUtc, 0);

        _lastDecodedFrameCount = 0;
        _lastRenderedFrameCount = 0;
        _decodedFps = 0;
        _renderedFps = 0;
        _lastStatsSampleUtc = DateTime.UtcNow;

        _currentFrameWidth = 0;
        _currentFrameHeight = 0;
        FrameInfoText.Text = "Waiting for first frame...";
        UpdateStatsText();
    }

    private void RefreshStats()
    {
        var now = DateTime.UtcNow;
        var elapsedSeconds = (now - _lastStatsSampleUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        var decoded = Interlocked.Read(ref _decodedFrameCount);
        var rendered = Interlocked.Read(ref _renderedFrameCount);

        _decodedFps = (decoded - _lastDecodedFrameCount) / elapsedSeconds;
        _renderedFps = (rendered - _lastRenderedFrameCount) / elapsedSeconds;

        _lastDecodedFrameCount = decoded;
        _lastRenderedFrameCount = rendered;
        _lastStatsSampleUtc = now;

        UpdateStatsText();

        var state = _cts is null
            ? "Idle"
            : (_cts.IsCancellationRequested || _isStopping ? "Stopping" : "Streaming");
        Title = $"ADB Screen Test | {state} | Render {_renderedFps:0.0} fps";
    }

    private void UpdateStatsText()
    {
        var decoded = Interlocked.Read(ref _decodedFrameCount);
        var rendered = Interlocked.Read(ref _renderedFrameCount);
        var frameSize = _currentFrameWidth > 0
            ? $"{_currentFrameWidth}x{_currentFrameHeight}"
            : "n/a";
        var renderCapLabel = _renderFpsCap is > 0 ? _renderFpsCap.Value.ToString() : "Unlimited";

        StatsText.Text =
            $"Decoded: {decoded} ({_decodedFps:0.0} fps) | " +
            $"Rendered: {rendered} ({_renderedFps:0.0} fps) | " +
            $"Frame: {frameSize} | " +
            $"Render cap: {renderCapLabel}";
    }

    private void UpdateFooter()
    {
        var res = ResolutionPresets[ClampIndex(ResolutionCombo.SelectedIndex, ResolutionPresets.Length)].Label;
        var bitrate = BitratePresets[ClampIndex(BitrateCombo.SelectedIndex, BitratePresets.Length)].Label;
        var renderCap = RenderFpsPresets[ClampIndex(RenderFpsCombo.SelectedIndex, RenderFpsPresets.Length)].Label;
        var device = GetSelectedDeviceLabel();

        FooterText.Text =
            $"Device: {device} | Preset: {res}, {bitrate}, render cap {renderCap}. " +
            "Settings apply on Start/Reconnect. Lower bitrate/resolution if latency is high.";
    }

    private void UpdateControlState()
    {
        var hasSession = _cts is not null;
        var running = hasSession && !_isStopping && !(_cts?.IsCancellationRequested ?? true);
        var streamControlsLocked = _isStopping;

        StartButton.IsEnabled = !hasSession && !streamControlsLocked;
        StopButton.IsEnabled = hasSession && !streamControlsLocked;
        ReconnectButton.IsEnabled = !streamControlsLocked;

        ResolutionCombo.IsEnabled = !running;
        BitrateCombo.IsEnabled = !running;
        RenderFpsCombo.IsEnabled = !running;

        var actionControlsEnabled = !_adbActionBusy;
        DeviceCombo.IsEnabled = actionControlsEnabled;
        RefreshDevicesButton.IsEnabled = actionControlsEnabled;
        DevicesButton.IsEnabled = actionControlsEnabled;
        GetPropButton.IsEnabled = actionControlsEnabled;
        PackagesButton.IsEnabled = actionControlsEnabled;
        BatteryButton.IsEnabled = actionControlsEnabled;
        ScreenshotButton.IsEnabled = actionControlsEnabled;
        RecordButton.IsEnabled = actionControlsEnabled;
        TopActivityButton.IsEnabled = actionControlsEnabled;
        DisplayInfoButton.IsEnabled = actionControlsEnabled;
        WmSizeButton.IsEnabled = actionControlsEnabled;
        WmDensityButton.IsEnabled = actionControlsEnabled;
        WindowInfoButton.IsEnabled = actionControlsEnabled;
        RotationSettingsButton.IsEnabled = actionControlsEnabled;
        RefreshRatesButton.IsEnabled = actionControlsEnabled;
        DisplayModesButton.IsEnabled = actionControlsEnabled;
        UserPackagesButton.IsEnabled = actionControlsEnabled;
        SystemPackagesButton.IsEnabled = actionControlsEnabled;
        MemInfoButton.IsEnabled = actionControlsEnabled;
        ActivityStackButton.IsEnabled = actionControlsEnabled;
        ProcessesButton.IsEnabled = actionControlsEnabled;
        StorageButton.IsEnabled = actionControlsEnabled;
        HomeButton.IsEnabled = actionControlsEnabled;
        BackButton.IsEnabled = actionControlsEnabled;
        RecentsButton.IsEnabled = actionControlsEnabled;
        PowerButton.IsEnabled = actionControlsEnabled;
        VolUpButton.IsEnabled = actionControlsEnabled;
        VolDownButton.IsEnabled = actionControlsEnabled;
        NotificationsButton.IsEnabled = actionControlsEnabled;
        QuickSettingsButton.IsEnabled = actionControlsEnabled;
        RunShellButton.IsEnabled = actionControlsEnabled;
        ShellCommandTextBox.IsEnabled = actionControlsEnabled;
        ShellDisplayQuickButton.IsEnabled = actionControlsEnabled;
        ShellWindowQuickButton.IsEnabled = actionControlsEnabled;
        ShellBatteryQuickButton.IsEnabled = actionControlsEnabled;
        ShellSurfaceQuickButton.IsEnabled = actionControlsEnabled;
        ClearOutputButton.IsEnabled = actionControlsEnabled;
    }

    private void ReleaseSessionState(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cts, cts))
        {
            _cts = null;
            _producerTask = null;
            _consumerTask = null;
            _sessionMonitorTask = null;
        }

        _isStopping = false;

        try
        {
            cts.Dispose();
        }
        catch
        {
            // Ignore disposal races.
        }
    }

    private bool StatusHasError()
        => StatusText.Text?.Contains("error", StringComparison.OrdinalIgnoreCase) == true;

    private void EnsureBitmap(VideoFrame frame)
    {
        if (_bitmap != null &&
            _bitmap.PixelSize.Width == frame.Width &&
            _bitmap.PixelSize.Height == frame.Height)
            return;

        _bitmap = new WriteableBitmap(
            new Avalonia.PixelSize(frame.Width, frame.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul
        );

        ScreenImage.Source = _bitmap;
    }

    private void RenderFrame(VideoFrame frame)
    {
        if (_bitmap == null)
            return;

        using var fb = _bitmap.Lock();
        int srcStride = frame.Width * 4;
        int dstStride = fb.RowBytes;

        for (int y = 0; y < frame.Height; y++)
        {
            var srcOffset = y * srcStride;
            var dstPtr = fb.Address + y * dstStride;

            Marshal.Copy(
                frame.Data,
                srcOffset,
                dstPtr,
                srcStride
            );
        }

        ScreenImage.InvalidateVisual();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = $"Status: {text}";
    }

    private static int ClampIndex(int index, int length)
    {
        if (length <= 0)
            return 0;

        if (index < 0)
            return 0;

        if (index >= length)
            return length - 1;

        return index;
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024d / 1024d:0.0} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var newlineIndex = text.IndexOfAny(['\r', '\n']);
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}
