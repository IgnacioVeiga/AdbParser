namespace AdbParser.Core.Parsers;

public class GenericParser : IAdbParser<List<string>>
{
    public List<string> Parse(string rawOutput)
    {
        return [.. rawOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())];
    }
}
