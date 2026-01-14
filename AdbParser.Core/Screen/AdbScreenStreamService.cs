
using AdbParser.Core.Video;
using AdbParser.Core.Execution;

namespace AdbParser.Core.Screen;

public sealed class AdbScreenStreamService(IH264Decoder decoder) : IScreenStreamService
{
    private readonly IH264Decoder _decoder = decoder;

    public async IAsyncEnumerable<VideoFrame> StartStream(
        ScreenStreamOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        using var process = AdbExecutor.RunBinaryStream(
            "exec-out",
            "screenrecord --output-format=h264 -"
        );

        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await process.Output.ReadAsync(
                buffer.AsMemory(),
                cancellationToken
            );

            if (read <= 0)
                yield break;

            if (_decoder.TryDecode(buffer.AsSpan(0, read), out var frame))
            {
                yield return frame;
            }
        }
    }

}
