namespace AdbParser.Core.Parsers;

public class DevicesParser : IAdbParser<List<DeviceInfo>>
{
    public List<DeviceInfo> Parse(string rawOutput)
    {
        var result = new List<DeviceInfo>();
        var lines = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices")) continue;

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                result.Add(new DeviceInfo
                {
                    Serial = parts[0].Trim(),
                    Status = parts[1].Trim()
                });
            }
        }

        return result;
    }
}

public class DeviceInfo
{
    public string Serial { get; set; } = "";
    public string Status { get; set; } = "";
}
