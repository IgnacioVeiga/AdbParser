namespace AdbParser.Core.Execution;

public sealed class AdbBinaryResult
{
    public Stream DataStream { get; init; } = Stream.Null;
    public int ExitCode { get; init; }
}
