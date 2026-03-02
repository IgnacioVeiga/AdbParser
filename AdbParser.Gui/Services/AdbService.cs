using System.Threading.Tasks;
using AdbParser.Core.Execution;

namespace AdbParser.Gui.Services;

public class AdbService : IAdbService
{
    public Task<AdbResult<object>> RunRawAsync(AdbCommand command, string? deviceSerial = null)
        => AdbExecutor.RunAsync(command, deviceSerial);

    public async Task<string> RunFormattedAsync(AdbCommand command, string? deviceSerial = null)
    {
        var result = await AdbExecutor.RunAsync(command, deviceSerial);
        return FormatResult(result);
    }

    public Task<AdbBinaryResult> RunBinaryAsync(string command, string arguments = "", string? deviceSerial = null)
        => AdbExecutor.RunBinaryAsync(command, arguments, deviceSerial);

    public AdbBinaryProcess RunBinaryStream(string command, string arguments = "", string? deviceSerial = null)
        => AdbExecutor.RunBinaryStream(command, arguments, deviceSerial);

    private static string FormatResult(AdbResult<object> result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Parser: {result.ParserKey}");
        sb.AppendLine();
        sb.AppendLine("Data:");
        if (result.Data is null)
            sb.AppendLine("(null)");
        else if (result.Data is System.Collections.IEnumerable enumerable && result.Data is not string)
        {
            foreach (var item in enumerable)
                sb.AppendLine(item?.ToString());
        }
        else
        {
            sb.AppendLine(result.Data.ToString() ?? "(null)");
        }

        if (!string.IsNullOrWhiteSpace(result.RawOutput))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Raw output:");
            sb.AppendLine(result.RawOutput.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }
}
