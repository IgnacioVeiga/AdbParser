using System.Runtime.CompilerServices;

namespace AdbParser.Core.Screen;

public sealed class FakeScreenStreamService : IScreenStreamService
{
    public async IAsyncEnumerable<VideoFrame> StartStream(
        ScreenStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int width = options.Width ?? 320;
        int height = options.Height ?? 240;
        int fps = options.MaxFps > 0 ? options.MaxFps : 30;
        int delayMs = 1000 / fps;

        int frameIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = GenerateFrame(width, height, frameIndex++);
            yield return frame;

            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static VideoFrame GenerateFrame(int width, int height, int frameIndex)
    {
        var buffer = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;

                buffer[offset + 0] = (byte)((frameIndex) % 256);       // B
                buffer[offset + 1] = (byte)((y + frameIndex) % 256);   // G
                buffer[offset + 2] = (byte)((x + frameIndex) % 256);   // R
                buffer[offset + 3] = 255;                              // A
            }
        }

        return new VideoFrame
        {
            Width = width,
            Height = height,
            Format = PixelFormat.Bgra32,
            Data = buffer
        };
    }

}
