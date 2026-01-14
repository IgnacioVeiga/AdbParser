using AdbParser.Core.Screen;

namespace AdbParser.Core.Video;

public interface IH264Decoder : IDisposable
{
    bool TryDecode(
        ReadOnlySpan<byte> h264Data,
        out VideoFrame frame
    );
}
