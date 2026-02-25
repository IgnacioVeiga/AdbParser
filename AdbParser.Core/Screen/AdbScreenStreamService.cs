
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
        var screenrecordArgs = BuildScreenrecordArgs(options);

        using var process = AdbExecutor.RunBinaryStream(
            "exec-out",
            screenrecordArgs,
            options.DeviceSerial
        );

        var buffer = new byte[64 * 1024];
        var hasProducedFrames = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await process.Output.ReadAsync(
                buffer.AsMemory(),
                cancellationToken
            );

            if (read <= 0)
            {
                await process.Process.WaitForExitAsync(cancellationToken);
                var stderr = await process.ErrorOutput;

                if (process.Process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"adb screenrecord stream ended with exit code {process.Process.ExitCode}: {stderr.Trim()}");
                }

                if (!hasProducedFrames && !string.IsNullOrWhiteSpace(stderr))
                {
                    throw new InvalidOperationException(
                        $"adb screenrecord did not produce frames: {stderr.Trim()}");
                }

                yield break;
            }

            _decoder.Feed(buffer.AsSpan(0, read));

            while (_decoder.TryGetFrame(out var frame))
            {
                hasProducedFrames = true;
                yield return frame;
            }
        }
    }

    private static string BuildScreenrecordArgs(ScreenStreamOptions options)
    {
        var parts = new List<string> { "screenrecord" };

        if (options.Width is > 0 && options.Height is > 0)
        {
            parts.Add($"--size {options.Width.Value}x{options.Height.Value}");
        }

        if (options.BitRate > 0)
        {
            parts.Add($"--bit-rate {options.BitRate}");
        }

        // --max-fps is not supported consistently across Android versions, so we
        // keep rendering throttling on the consumer side for now.
        parts.Add("--output-format=h264 -");

        return string.Join(' ', parts);
    }
}
