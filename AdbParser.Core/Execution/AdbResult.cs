namespace AdbParser.Core.Execution;

public class AdbResult<T>
{
    public string ParserKey { get; init; } = "";
    public string RawOutput { get; init; } = "";
    public T Data { get; init; } = default!;
}
