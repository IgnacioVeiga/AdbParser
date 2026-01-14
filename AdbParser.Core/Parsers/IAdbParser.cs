namespace AdbParser.Core.Parsers;

public interface IAdbParser<T>
{
    T Parse(string rawOutput);
}
