namespace AdbParser.Core.Parsers;

public class PackagesParser : IAdbParser<List<string>>
{
    public List<string> Parse(string rawOutput)
    {
        var result = new List<string>();
        var lines = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("package:"))
                result.Add(trimmed["package:".Length..]);
            else
                result.Add(trimmed);
        }

        return result;
    }
}
