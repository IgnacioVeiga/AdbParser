using AdbParser.Core.Screen;

namespace AdbParser.Core.Video;

public sealed class FakeH264Decoder : IH264Decoder
{
    private int _frameIndex;
    private readonly Queue<VideoFrame> _frames = new();

    public void Feed(ReadOnlySpan<byte> h264Data)
    {
        if (h264Data.IsEmpty)
            return;

        const int width = 320;
        const int height = 240;

        _frameIndex++;

        var data = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;

                data[offset + 0] = (byte)(_frameIndex % 256);       // B
                data[offset + 1] = (byte)((y + _frameIndex) % 256); // G
                data[offset + 2] = (byte)((x + _frameIndex) % 256); // R
                data[offset + 3] = 255;                             // A
            }
        }

        _frames.Enqueue(new VideoFrame
        {
            Width = width,
            Height = height,
            Format = PixelFormat.Bgra32,
            Data = data
        });
    }

    public bool TryGetFrame(out VideoFrame frame)
    {
        if (_frames.Count > 0)
        {
            frame = _frames.Dequeue();
            return true;
        }

        frame = default!;
        return false;
    }

    public void Dispose() { }
}
