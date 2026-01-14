using System.Text.RegularExpressions;

namespace AdbParser.Core.Parsers;

public class GetPropParser : IAdbParser<Dictionary<string, string>>
{
    private static readonly Regex _regex = new(@"\[(.*?)\]: \[(.*?)\]");

    public Dictionary<string, string> Parse(string rawOutput)
    {
        var dict = new Dictionary<string, string>();
        var lines = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var match = _regex.Match(line);
            if (match.Success)
                dict[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return dict;
    }
}
