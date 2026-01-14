using System.Threading.Channels;

namespace AdbParser.Core.Screen;

public sealed class VideoFrameBuffer
{
    private readonly Channel<VideoFrame> _channel;

    public VideoFrameBuffer()
    {
        _channel = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }
        );
    }

    public ValueTask WriteAsync(VideoFrame frame, CancellationToken ct)
        => _channel.Writer.WriteAsync(frame, ct);

    public ValueTask<VideoFrame> ReadAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);
}
