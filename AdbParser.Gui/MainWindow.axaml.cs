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

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var decoder = new FfmpegH264Decoder();
        var service = new AdbScreenStreamService(decoder);

        var buffer = new VideoFrameBuffer();

        _cts = new CancellationTokenSource();
        var options = new ScreenStreamOptions();

        // =========================
        // PRODUCER: ADB + decoder
        // =========================
        _ = Task.Run(async () =>
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
        });

        // =========================
        // CONSUMER: render loop
        // =========================
        _ = Task.Run(async () =>
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
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
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
}
