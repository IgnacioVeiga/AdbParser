using AdbParser.Core.Screen;

namespace AdbParser.Core.Video;

public interface IH264Decoder : IDisposable
{
    void Feed(ReadOnlySpan<byte> h264Data);
    bool TryGetFrame(out VideoFrame frame);
}
