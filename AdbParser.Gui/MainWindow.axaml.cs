using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AdbParser.Core.Screen;
using AdbParser.Core.Video;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AdbParser.Gui;

public partial class MainWindow : Window
{
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

    private long _decodedFrameCount;
    private long _renderedFrameCount;
    private long _lastDecodedFrameCount;
    private long _lastRenderedFrameCount;
    private double _decodedFps;
    private double _renderedFps;
    private DateTime _lastStatsSampleUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _statsTimer;

    private int _currentFrameWidth;
    private int _currentFrameHeight;
    private long _lastRenderTicksUtc;
    private int? _renderFpsCap;

    public MainWindow()
    {
        InitializeComponent();

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
        await StartStreamingAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _statsTimer.Stop();
        await StopStreamingAsync(windowClosing: true);
    }

    private void ConfigureControls()
    {
        ResolutionCombo.ItemsSource = Array.ConvertAll(ResolutionPresets, p => p.Label);
        BitrateCombo.ItemsSource = Array.ConvertAll(BitratePresets, p => p.Label);
        RenderFpsCombo.ItemsSource = Array.ConvertAll(RenderFpsPresets, p => p.Label);

        ResolutionCombo.SelectedIndex = 0;
        BitrateCombo.SelectedIndex = 1; // 8 Mbps default
        RenderFpsCombo.SelectedIndex = 0; // Unlimited (lower latency)

        StartButton.Click += async (_, _) => await StartStreamingAsync();
        StopButton.Click += async (_, _) => await StopStreamingAsync();
        ReconnectButton.Click += async (_, _) => await ReconnectAsync();

        ResolutionCombo.SelectionChanged += (_, _) => UpdateFooter();
        BitrateCombo.SelectionChanged += (_, _) => UpdateFooter();
        RenderFpsCombo.SelectionChanged += (_, _) => UpdateFooter();

        FrameInfoText.Text = "No frame yet";
        SetStatus("Disconnected");
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
        SetStatus("Connecting to device...");
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
            _ = Dispatcher.UIThread.InvokeAsync(() => SetStatus(message));
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

        FooterText.Text =
            $"Preset: {res}, {bitrate}, render cap {renderCap}. " +
            "Settings apply on Start/Reconnect. Lower bitrate/resolution if latency is high.";
    }

    private void UpdateControlState()
    {
        var hasSession = _cts is not null;
        var running = hasSession && !_isStopping && !(_cts?.IsCancellationRequested ?? true);

        StartButton.IsEnabled = !hasSession;
        StopButton.IsEnabled = hasSession;
        ReconnectButton.IsEnabled = !_isStopping;

        ResolutionCombo.IsEnabled = !running;
        BitrateCombo.IsEnabled = !running;
        RenderFpsCombo.IsEnabled = !running;
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
