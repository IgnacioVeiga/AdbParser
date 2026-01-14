namespace AdbParser.Core.Parsers;

public interface IAdbParserWrapper
{
    object Parse(string rawOutput);
    Type ResultType { get; }
}

public class AdbParserWrapper<T> : IAdbParserWrapper
{
    private readonly IAdbParser<T> _parser;

    public AdbParserWrapper(IAdbParser<T> parser)
    {
        _parser = parser;
    }

    public object Parse(string rawOutput) => _parser.Parse(rawOutput)!;
    public Type ResultType => typeof(T);
}
