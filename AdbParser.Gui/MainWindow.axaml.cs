using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using AdbParser.Core.Screen;
using AdbParser.Core.Video;
using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AdbParser.Gui;

public partial class MainWindow : Window
{
    private WriteableBitmap? _bitmap;
    private int _frameCount;
    private CancellationTokenSource? _cts;
    private Task? _producerTask;
    private Task? _consumerTask;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        SetStatus("Initializing FFmpeg...");

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
            return;
        }

        var buffer = new VideoFrameBuffer();

        _cts = new CancellationTokenSource();
        var options = new ScreenStreamOptions();
        SetStatus("Streaming...");

        // =========================
        // PRODUCER: ADB + decoder
        // =========================
        _producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in service.StartStream(options, _cts.Token))
                {
                    await buffer.WriteAsync(frame, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when the operation is canceled.
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    SetStatus($"Stream error: {FirstLine(ex.Message)}"));
                _cts.Cancel();
            }
            finally
            {
                decoder.Dispose();
            }
        });

        // =========================
        // CONSUMER: render loop
        // =========================
        _consumerTask = Task.Run(async () =>
        {
            var frameInterval = TimeSpan.FromMilliseconds(33); // ~30 FPS

            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var frame = await buffer.ReadAsync(_cts.Token);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _frameCount++;
                        EnsureBitmap(frame);
                        RenderFrame(frame);
                        Title = $"Frames: {_frameCount}";
                    });

                    await Task.Delay(frameInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    SetStatus($"Render error: {FirstLine(ex.Message)}"));
                _cts.Cancel();
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Stopped");
    }

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

        using (var fb = _bitmap.Lock())
        {
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
        }

        ScreenImage.InvalidateVisual();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = $"Status: {text}";
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var newlineIndex = text.IndexOfAny(['\r', '\n']);
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }
}
