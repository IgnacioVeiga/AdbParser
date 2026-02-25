using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    private enum MirrorViewMode
    {
        Normal,
        Focus,
        Fullscreen
    }

    private sealed class DeviceChoice
    {
        public string Label { get; init; } = "";
        public string? Serial { get; init; }
        public string Status { get; init; } = "";
        public override string ToString() => Label;
    }

    private static readonly string[] ToolCategoryOptions =
    [
        "Overview",
        "Display",
        "Apps",
        "Input",
        "Shell"
    ];

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

    private static readonly (string Label, MirrorViewMode Mode)[] ViewModePresets =
    [
        ("Normal", MirrorViewMode.Normal),
        ("Focus (mirror)", MirrorViewMode.Focus),
        ("Fullscreen", MirrorViewMode.Fullscreen)
    ];

    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _cts;
    private Task? _producerTask;
    private Task? _consumerTask;
    private Task? _sessionMonitorTask;

    private bool _isStopping;
    private bool _isClosing;
    private bool _adbActionBusy;
    private bool _suppressViewModeSelectionChange;
    private bool _suppressSettingsPersistence;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private MirrorViewMode _lastNonFullscreenViewMode = MirrorViewMode.Normal;
    private GridLength _savedToolboxWidth = new(410);
    private GridLength _savedOutputHeight = new(240);
    private IReadOnlyList<Control> _adbActionControls = [];
    private IReadOnlyList<Control> _toolsCategoryPanels = [];
    private string? _preferredDeviceSerialFromSettings;
    private double _lastNormalWindowWidth;
    private double _lastNormalWindowHeight;
    private Avalonia.PixelPoint? _lastNormalWindowPosition;

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
    private readonly GuiUserSettings _userSettings;

    private int _currentFrameWidth;
    private int _currentFrameHeight;
    private long _lastRenderTicksUtc;
    private int? _renderFpsCap;
    private bool _parsersRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _userSettings = GuiUserSettingsStore.LoadOrDefault();
        ApplyLoadedWindowPlacement();

        EnsureParsersRegistered();
        ConfigureControls();
        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statsTimer.Tick += (_, _) => RefreshStats();

        Opened += OnOpened;
        Closed += OnClosed;
        PositionChanged += (_, _) => CaptureNormalWindowPlacement();
        SizeChanged += (_, _) => CaptureNormalWindowPlacement();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _statsTimer.Start();
        ApplyLoadedWindowStateIfNeeded();
        CaptureNormalWindowPlacement();
        UpdateControlState();
        UpdateStatsText();
        UpdateFooter();
        SetStatus("Ready. Choose an action or start mirror.");
        await RefreshDevicesListAsync(selectFirstOnline: true);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        SaveSettingsSafe();
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
            PersistSettingsIfReady();
        };
        RefreshDevicesButton.Click += async (_, _) =>
            await RunAdbActionAsync("Refresh devices", RefreshDevicesOutputAsync);

        ResolutionCombo.ItemsSource = Array.ConvertAll(ResolutionPresets, p => p.Label);
        BitrateCombo.ItemsSource = Array.ConvertAll(BitratePresets, p => p.Label);
        RenderFpsCombo.ItemsSource = Array.ConvertAll(RenderFpsPresets, p => p.Label);
        ViewModeCombo.ItemsSource = Array.ConvertAll(ViewModePresets, p => p.Label);
        ToolsCategoryCombo.ItemsSource = ToolCategoryOptions;

        ResolutionCombo.SelectedIndex = 0;
        BitrateCombo.SelectedIndex = 1; // 8 Mbps default
        RenderFpsCombo.SelectedIndex = 0; // Unlimited (lower latency)
        ViewModeCombo.SelectedIndex = 0;
        ToolsCategoryCombo.SelectedIndex = 0;

        StartButton.Click += async (_, _) => await RunStreamOpAsync(StartStreamingAsync);
        StopButton.Click += async (_, _) => await RunStreamOpAsync(() => StopStreamingAsync());
        ReconnectButton.Click += async (_, _) => await RunStreamOpAsync(ReconnectAsync);

        ResolutionCombo.SelectionChanged += (_, _) =>
        {
            UpdateFooter();
            PersistSettingsIfReady();
        };
        BitrateCombo.SelectionChanged += (_, _) =>
        {
            UpdateFooter();
            PersistSettingsIfReady();
        };
        RenderFpsCombo.SelectionChanged += (_, _) =>
        {
            UpdateFooter();
            PersistSettingsIfReady();
        };
        ViewModeCombo.SelectionChanged += (_, _) =>
        {
            if (!_suppressViewModeSelectionChange)
                ApplyMirrorViewMode();
            PersistSettingsIfReady();
        };
        ToolsCategoryCombo.SelectionChanged += (_, _) =>
        {
            ApplyToolsCategorySelection();
            PersistSettingsIfReady();
        };

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
        ExitFullscreenButton.Click += (_, _) => ExitFullscreenOverlayView();

        SendInputTextButton.Click += async (_, _) => await RunAdbActionAsync("Input Text", RunInputTextAsync);
        TapCoordsButton.Click += async (_, _) => await RunAdbActionAsync("Input Tap", RunTapCoordinatesAsync);
        TapCenterButton.Click += async (_, _) => await RunAdbActionAsync("Input Tap Center", RunTapCenterAsync);

        RunShellButton.Click += async (_, _) => await RunAdbActionAsync("Shell", RunCustomShellAsync);
        ShellDisplayQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys display";
        ShellWindowQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys window";
        ShellBatteryQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys battery";
        ShellSurfaceQuickButton.Click += (_, _) =>
            ShellCommandTextBox.Text = "dumpsys SurfaceFlinger";
        ShellCommandTextBox.LostFocus += (_, _) => PersistSettingsIfReady();
        CopyOutputButton.Click += async (_, _) => await CopyOutputToClipboardAsync();
        SaveOutputButton.Click += async (_, _) => await SaveOutputToFileAsync();
        ClearOutputButton.Click += (_, _) => ClearOutput();
        InputTextTextBox.LostFocus += (_, _) => PersistSettingsIfReady();
        TapXTextBox.LostFocus += (_, _) => PersistSettingsIfReady();
        TapYTextBox.LostFocus += (_, _) => PersistSettingsIfReady();

        InitializeControlGroups();
        ApplyLoadedSettings();
        ApplyToolsCategorySelection();
        ApplyMirrorViewMode();
        FrameInfoText.Text = "No frame yet";
        ActionStatusText.Text = "Ready";
        DeviceStatusText.Text = "Devices: not loaded";
        PreviewHintOverlay.IsVisible = true;
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

        if (selection is null && !string.IsNullOrWhiteSpace(_preferredDeviceSerialFromSettings))
        {
            selection = _deviceChoices.FirstOrDefault(d =>
                string.Equals(d.Serial, _preferredDeviceSerialFromSettings, StringComparison.Ordinal));
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

    private void InitializeControlGroups()
    {
        _toolsCategoryPanels =
        [
            OverviewToolsPanel,
            DisplayToolsPanel,
            AppsToolsPanel,
            InputToolsPanel,
            ShellToolsPanel
        ];

        _adbActionControls =
        [
            DeviceCombo,
            RefreshDevicesButton,
            DevicesButton,
            GetPropButton,
            PackagesButton,
            BatteryButton,
            ScreenshotButton,
            RecordButton,
            TopActivityButton,
            DisplayInfoButton,
            WmSizeButton,
            WmDensityButton,
            WindowInfoButton,
            RotationSettingsButton,
            RefreshRatesButton,
            DisplayModesButton,
            UserPackagesButton,
            SystemPackagesButton,
            MemInfoButton,
            ActivityStackButton,
            ProcessesButton,
            StorageButton,
            HomeButton,
            BackButton,
            RecentsButton,
            PowerButton,
            VolUpButton,
            VolDownButton,
            NotificationsButton,
            QuickSettingsButton,
            RunShellButton,
            ShellCommandTextBox,
            ShellDisplayQuickButton,
            ShellWindowQuickButton,
            ShellBatteryQuickButton,
            ShellSurfaceQuickButton,
            SendInputTextButton,
            InputTextTextBox,
            TapXTextBox,
            TapYTextBox,
            TapCoordsButton,
            TapCenterButton
        ];
    }

    private void ApplyLoadedSettings()
    {
        _suppressSettingsPersistence = true;
        _suppressViewModeSelectionChange = true;
        try
        {
            if (_userSettings.ToolboxWidth is > 120)
                _savedToolboxWidth = new GridLength(_userSettings.ToolboxWidth.Value);
            if (_userSettings.OutputHeight is > 80)
                _savedOutputHeight = new GridLength(_userSettings.OutputHeight.Value);

            _preferredDeviceSerialFromSettings = string.IsNullOrWhiteSpace(_userSettings.PreferredDeviceSerial)
                ? null
                : _userSettings.PreferredDeviceSerial;

            ResolutionCombo.SelectedIndex = ClampIndex(_userSettings.ResolutionIndex, ResolutionPresets.Length);
            BitrateCombo.SelectedIndex = ClampIndex(_userSettings.BitrateIndex, BitratePresets.Length);
            RenderFpsCombo.SelectedIndex = ClampIndex(_userSettings.RenderFpsIndex, RenderFpsPresets.Length);
            ViewModeCombo.SelectedIndex = ClampIndex(_userSettings.ViewModeIndex, ViewModePresets.Length);
            ToolsCategoryCombo.SelectedIndex = ClampIndex(_userSettings.ToolsCategoryIndex, ToolCategoryOptions.Length);

            if (!string.IsNullOrWhiteSpace(_userSettings.ShellCommandText))
                ShellCommandTextBox.Text = _userSettings.ShellCommandText;
            if (!string.IsNullOrWhiteSpace(_userSettings.InputText))
                InputTextTextBox.Text = _userSettings.InputText;
            if (!string.IsNullOrWhiteSpace(_userSettings.TapX))
                TapXTextBox.Text = _userSettings.TapX;
            if (!string.IsNullOrWhiteSpace(_userSettings.TapY))
                TapYTextBox.Text = _userSettings.TapY;
        }
        finally
        {
            _suppressViewModeSelectionChange = false;
            _suppressSettingsPersistence = false;
        }
    }

    private void ApplyLoadedWindowPlacement()
    {
        try
        {
            if (_userSettings.WindowWidth is > 320 &&
                _userSettings.WindowHeight is > 240)
            {
                Width = _userSettings.WindowWidth.Value;
                Height = _userSettings.WindowHeight.Value;

                _lastNormalWindowWidth = Width;
                _lastNormalWindowHeight = Height;
            }

            if (_userSettings.WindowPosX is int x && _userSettings.WindowPosY is int y)
            {
                var position = new Avalonia.PixelPoint(x, y);
                Position = position;
                _lastNormalWindowPosition = position;
            }
        }
        catch
        {
            // Ignore invalid placement values and keep framework defaults.
        }
    }

    private void ApplyLoadedWindowStateIfNeeded()
    {
        // Fullscreen is controlled by the mirror view mode. Persisting it here would
        // conflict with the app-specific fullscreen behavior and custom exit overlay.
        if (GetSelectedViewMode() == MirrorViewMode.Fullscreen)
            return;

        if (!TryParseWindowState(_userSettings.WindowStateName, out var state))
            return;

        if (state is WindowState.Minimized or WindowState.FullScreen)
            return;

        try
        {
            WindowState = state;
        }
        catch
        {
            // Some window managers reject state changes during startup.
        }
    }

    private void CaptureNormalWindowPlacement()
    {
        try
        {
            if (WindowState != WindowState.Normal)
                return;

            if (!double.IsNaN(Width) && Width > 320)
                _lastNormalWindowWidth = Width;
            if (!double.IsNaN(Height) && Height > 240)
                _lastNormalWindowHeight = Height;

            _lastNormalWindowPosition = Position;
        }
        catch
        {
            // Window may be in a transient state while opening/closing.
        }
    }

    private static bool TryParseWindowState(string? value, out WindowState state)
    {
        if (Enum.TryParse<WindowState>(value, ignoreCase: true, out state))
            return true;

        state = WindowState.Normal;
        return false;
    }

    private void PersistSettingsIfReady()
    {
        if (_suppressSettingsPersistence)
            return;

        SaveSettingsSafe();
    }

    private void SaveSettingsSafe()
    {
        try
        {
            GuiUserSettingsStore.Save(CaptureCurrentSettings());
        }
        catch
        {
            // Keep GUI usable even if the config path is unavailable or read-only.
        }
    }

    private GuiUserSettings CaptureCurrentSettings()
    {
        var toolboxWidth = ToolboxPanel.IsVisible && MainContentGrid.ColumnDefinitions.Count >= 1
            ? MainContentGrid.ColumnDefinitions[0].Width.Value
            : _savedToolboxWidth.Value;
        var outputHeight = OutputPanel.IsVisible && RootLayoutGrid.RowDefinitions.Count >= 4
            ? RootLayoutGrid.RowDefinitions[3].Height.Value
            : _savedOutputHeight.Value;

        return new GuiUserSettings
        {
            Version = 1,
            ResolutionIndex = ClampIndex(ResolutionCombo.SelectedIndex, ResolutionPresets.Length),
            BitrateIndex = ClampIndex(BitrateCombo.SelectedIndex, BitratePresets.Length),
            RenderFpsIndex = ClampIndex(RenderFpsCombo.SelectedIndex, RenderFpsPresets.Length),
            ViewModeIndex = ClampIndex(ViewModeCombo.SelectedIndex, ViewModePresets.Length),
            ToolsCategoryIndex = ClampIndex(ToolsCategoryCombo.SelectedIndex, ToolCategoryOptions.Length),
            PreferredDeviceSerial = GetSelectedDeviceSerial(),
            ShellCommandText = ShellCommandTextBox.Text,
            InputText = InputTextTextBox.Text,
            TapX = TapXTextBox.Text,
            TapY = TapYTextBox.Text,
            ToolboxWidth = toolboxWidth > 0 ? toolboxWidth : null,
            OutputHeight = outputHeight > 0 ? outputHeight : null,
            WindowWidth = _lastNormalWindowWidth > 320 ? _lastNormalWindowWidth : (double?)null,
            WindowHeight = _lastNormalWindowHeight > 240 ? _lastNormalWindowHeight : (double?)null,
            WindowPosX = _lastNormalWindowPosition?.X,
            WindowPosY = _lastNormalWindowPosition?.Y,
            WindowStateName = WindowState == WindowState.FullScreen
                ? WindowState.Normal.ToString()
                : WindowState.ToString()
        };
    }

    private void ApplyToolsCategorySelection()
    {
        var index = ClampIndex(ToolsCategoryCombo.SelectedIndex, _toolsCategoryPanels.Count);
        for (var i = 0; i < _toolsCategoryPanels.Count; i++)
        {
            _toolsCategoryPanels[i].IsVisible = i == index;
        }
    }

    private MirrorViewMode GetSelectedViewMode()
    {
        var preset = ViewModePresets[ClampIndex(ViewModeCombo.SelectedIndex, ViewModePresets.Length)];
        return preset.Mode;
    }

    private string GetSelectedViewModeLabel()
    {
        var preset = ViewModePresets[ClampIndex(ViewModeCombo.SelectedIndex, ViewModePresets.Length)];
        return preset.Label;
    }

    private void SetViewModeSelection(MirrorViewMode mode)
    {
        var index = Array.FindIndex(ViewModePresets, p => p.Mode == mode);
        if (index < 0)
            index = 0;

        if (ViewModeCombo.SelectedIndex == index)
        {
            ApplyMirrorViewMode();
            return;
        }

        _suppressViewModeSelectionChange = true;
        try
        {
            ViewModeCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressViewModeSelectionChange = false;
        }

        ApplyMirrorViewMode();
    }

    private void ExitFullscreenOverlayView()
    {
        var restoreMode = _lastNonFullscreenViewMode == MirrorViewMode.Fullscreen
            ? MirrorViewMode.Normal
            : _lastNonFullscreenViewMode;
        SetViewModeSelection(restoreMode);
    }

    private void ApplyMirrorViewMode()
    {
        var mode = GetSelectedViewMode();
        if (mode != MirrorViewMode.Fullscreen)
        {
            _lastNonFullscreenViewMode = mode;
        }

        var expandedMirror = mode is MirrorViewMode.Focus or MirrorViewMode.Fullscreen;

        if (!expandedMirror)
        {
            RestoreMainPanelsLayout();
            SetFullscreenChromeHidden(false);
            ExitFullscreenIfNeeded();
        }
        else
        {
            SaveMainPanelsLayout();
            CollapseMainPanelsForMirror();

            if (mode == MirrorViewMode.Fullscreen)
            {
                SetFullscreenChromeHidden(true);
                EnterFullscreen();
            }
            else
            {
                SetFullscreenChromeHidden(false);
                ExitFullscreenIfNeeded();
            }
        }

        UpdateFooter();
    }

    private void SaveMainPanelsLayout()
    {
        if (MainContentGrid.ColumnDefinitions.Count >= 3 &&
            MainContentGrid.ColumnDefinitions[0].Width.Value > 0)
        {
            _savedToolboxWidth = MainContentGrid.ColumnDefinitions[0].Width;
        }

        if (RootLayoutGrid.RowDefinitions.Count >= 4 &&
            RootLayoutGrid.RowDefinitions[3].Height.Value > 0)
        {
            _savedOutputHeight = RootLayoutGrid.RowDefinitions[3].Height;
        }
    }

    private void CollapseMainPanelsForMirror()
    {
        ToolboxPanel.IsVisible = false;
        MainColumnSplitter.IsVisible = false;
        OutputRowSplitter.IsVisible = false;
        OutputPanel.IsVisible = false;

        if (MainContentGrid.ColumnDefinitions.Count >= 3)
        {
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(0);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            MainContentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }

        if (RootLayoutGrid.RowDefinitions.Count >= 4)
        {
            RootLayoutGrid.RowDefinitions[2].Height = new GridLength(0);
            RootLayoutGrid.RowDefinitions[3].Height = new GridLength(0);
        }
    }

    private void RestoreMainPanelsLayout()
    {
        ToolboxPanel.IsVisible = true;
        MainColumnSplitter.IsVisible = true;
        OutputRowSplitter.IsVisible = true;
        OutputPanel.IsVisible = true;

        if (MainContentGrid.ColumnDefinitions.Count >= 3)
        {
            MainContentGrid.ColumnDefinitions[0].Width = _savedToolboxWidth.Value > 0
                ? _savedToolboxWidth
                : new GridLength(410);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(8);
            MainContentGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }

        if (RootLayoutGrid.RowDefinitions.Count >= 4)
        {
            RootLayoutGrid.RowDefinitions[2].Height = new GridLength(8);
            RootLayoutGrid.RowDefinitions[3].Height = _savedOutputHeight.Value > 0
                ? _savedOutputHeight
                : new GridLength(240);
        }
    }

    private void SetFullscreenChromeHidden(bool hidden)
    {
        TopControlsBar.IsVisible = !hidden;
        FooterBar.IsVisible = !hidden;
        FullscreenExitOverlay.IsVisible = hidden;

        if (RootLayoutGrid.RowDefinitions.Count >= 5)
        {
            RootLayoutGrid.RowDefinitions[0].Height = hidden ? new GridLength(0) : GridLength.Auto;
            RootLayoutGrid.RowDefinitions[4].Height = hidden ? new GridLength(0) : GridLength.Auto;
        }
    }

    private void EnterFullscreen()
    {
        try
        {
            if (WindowState != WindowState.FullScreen)
            {
                _windowStateBeforeFullscreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Fullscreen unavailable: {FirstLine(ex.Message)}");
        }
    }

    private void ExitFullscreenIfNeeded()
    {
        if (WindowState != WindowState.FullScreen)
            return;

        try
        {
            var restoreState = _windowStateBeforeFullscreen == WindowState.FullScreen
                ? WindowState.Normal
                : _windowStateBeforeFullscreen;
            WindowState = restoreState;
        }
        catch (Exception ex)
        {
            SetStatus($"Exit fullscreen failed: {FirstLine(ex.Message)}");
        }
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

    private async Task<string> RunInputTextAsync()
    {
        var text = (InputTextTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Input text is empty.");

        var encodedText = EncodeAndroidInputText(text);
        var command = $"input text \"{encodedText}\"";
        _ = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(command));

        return
            $"Text sent to device.\n" +
            $"Device: {GetSelectedDeviceLabel()}\n" +
            $"Command: adb {BuildDeviceSelectorPreview()}shell {command}\n" +
            $"Text: {text}";
    }

    private async Task<string> RunTapCoordinatesAsync()
    {
        if (!TryParseNonNegativeInt(TapXTextBox.Text, out var x))
            throw new InvalidOperationException("Tap X must be a non-negative integer.");
        if (!TryParseNonNegativeInt(TapYTextBox.Text, out var y))
            throw new InvalidOperationException("Tap Y must be a non-negative integer.");

        return await RunTapAsync(x, y, "Tap coordinates");
    }

    private async Task<string> RunTapCenterAsync()
    {
        if (_currentFrameWidth <= 0 || _currentFrameHeight <= 0)
            throw new InvalidOperationException("No frame size available yet. Start mirror first.");

        var x = _currentFrameWidth / 2;
        var y = _currentFrameHeight / 2;
        return await RunTapAsync(x, y, "Tap center");
    }

    private async Task<string> RunTapAsync(int x, int y, string label)
    {
        var command = $"input tap {x} {y}";
        _ = await RunSelectedDeviceCommandAsync(AdbCommand.Shell(command));

        return
            $"{label} sent.\n" +
            $"Device: {GetSelectedDeviceLabel()}\n" +
            $"Command: adb {BuildDeviceSelectorPreview()}shell {command}";
    }

    private async Task<string> RunScreenshotAsync()
    {
        var target = await PickSaveFileAsync(
            title: "Save screenshot",
            suggestedFileName: $"screen_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            "png");
        if (target is null)
            return "Screenshot canceled.";

        var result = await AdbExecutor.RunBinaryAsync(
            "exec-out",
            "screencap -p",
            GetSelectedDeviceSerial()
        );

        await using (var file = await target.OpenWriteAsync())
        {
            await result.DataStream.CopyToAsync(file);
        }

        return $"Screenshot saved to {DescribeStorageFile(target)}\nDevice: {GetSelectedDeviceLabel()}";
    }

    private async Task<string> RunRecord5sAsync()
    {
        var target = await PickSaveFileAsync(
            title: "Save screen record",
            suggestedFileName: $"record_{DateTime.Now:yyyyMMdd_HHmmss}.h264",
            [new FilePickerFileType("Raw H264 stream") { Patterns = ["*.h264"] }],
            "h264");
        if (target is null)
            return "Recording canceled.";

        using var adb = AdbExecutor.RunBinaryStream(
            "exec-out",
            "screenrecord --output-format=h264 -",
            GetSelectedDeviceSerial()
        );

        await using var file = await target.OpenWriteAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await adb.Output.CopyToAsync(file, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected after 5 seconds.
        }

        return $"Recorded ~5s to {DescribeStorageFile(target)}\nDevice: {GetSelectedDeviceLabel()}";
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
        UpdateControlState();
    }

    private void ClearOutput()
    {
        OutputTextBox.Text = string.Empty;
        SetActionStatus("Output cleared");
        UpdateControlState();
    }

    private async Task CopyOutputToClipboardAsync()
    {
        var text = OutputTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetActionStatus("Output is empty");
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                SetActionStatus("Clipboard unavailable");
                return;
            }

            await clipboard.SetTextAsync(text);
            SetActionStatus("Output copied to clipboard");
        }
        catch (Exception ex)
        {
            SetActionStatus($"Copy failed: {FirstLine(ex.Message)}");
        }
    }

    private async Task SaveOutputToFileAsync()
    {
        var text = OutputTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetActionStatus("Output is empty");
            return;
        }

        try
        {
            var target = await PickSaveFileAsync(
                title: "Save output console",
                suggestedFileName: $"adb_output_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                [new FilePickerFileType("Text file") { Patterns = ["*.txt", "*.log"] }],
                "txt");

            if (target is null)
            {
                SetActionStatus("Save canceled");
                return;
            }

            await using (var stream = await target.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteAsync(text);
            }

            SetActionStatus($"Output saved to {DescribeStorageFile(target)}");
        }
        catch (Exception ex)
        {
            SetActionStatus($"Save failed: {FirstLine(ex.Message)}");
        }
    }

    private void SetActionStatus(string text)
    {
        ActionStatusText.Text = text;
    }

    private async Task<IStorageFile?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        IReadOnlyList<FilePickerFileType>? fileTypes = null,
        string? defaultExtension = null)
    {
        var storage = StorageProvider;
        if (storage is null)
            throw new InvalidOperationException("Storage provider is not available in this window.");

        return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = fileTypes
        });
    }

    private static string DescribeStorageFile(IStorageFile file)
        => file.TryGetLocalPath() ?? file.Name;

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
                    PreviewHintOverlay.IsVisible = false;

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
        PreviewHintOverlay.IsVisible = true;
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
        var viewMode = GetSelectedViewModeLabel();

        FooterText.Text =
            $"Device: {device} | View: {viewMode} | Preset: {res}, {bitrate}, render cap {renderCap}. " +
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
        ViewModeCombo.IsEnabled = true;
        ToolsCategoryCombo.IsEnabled = !_adbActionBusy;
        ExitFullscreenButton.IsEnabled = true;

        var actionControlsEnabled = !_adbActionBusy;
        foreach (var control in _adbActionControls)
        {
            control.IsEnabled = actionControlsEnabled;
        }
        var hasOutputText = !string.IsNullOrWhiteSpace(OutputTextBox.Text);
        CopyOutputButton.IsEnabled = hasOutputText;
        SaveOutputButton.IsEnabled = hasOutputText;
        ClearOutputButton.IsEnabled = hasOutputText;
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
        PreviewHintOverlay.IsVisible = true;

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
