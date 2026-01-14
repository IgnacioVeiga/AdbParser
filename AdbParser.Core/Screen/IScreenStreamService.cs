
namespace AdbParser.Core.Screen;

public interface IScreenStreamService
{
    IAsyncEnumerable<VideoFrame> StartStream(
        ScreenStreamOptions options,
        CancellationToken cancellationToken
    );
}
