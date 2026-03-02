using System.Threading.Tasks;
using AdbParser.Core.Execution;

namespace AdbParser.Gui.Services;

public interface IAdbService
{
    Task<AdbResult<object>> RunRawAsync(AdbCommand command, string? deviceSerial = null);
    Task<string> RunFormattedAsync(AdbCommand command, string? deviceSerial = null);
    Task<AdbBinaryResult> RunBinaryAsync(string command, string arguments = "", string? deviceSerial = null);
    AdbBinaryProcess RunBinaryStream(string command, string arguments = "", string? deviceSerial = null);
}
