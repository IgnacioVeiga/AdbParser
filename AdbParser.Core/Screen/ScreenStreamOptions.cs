namespace AdbParser.Core.Screen;

public sealed class ScreenStreamOptions
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int BitRate { get; init; } = 8_000_000;
    public int MaxFps { get; init; } = 30;
}
