namespace AdbParser.Core.Screen;

public sealed class VideoFrame
{
    public byte[] Data { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public PixelFormat Format { get; init; } = PixelFormat.Bgra32;
}

public enum PixelFormat
{
    Rgb24,
    Bgra32
}