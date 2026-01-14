using System.Diagnostics;
using AdbParser.Core.Registry;

namespace AdbParser.Core.Execution;

public static class AdbExecutor
{
    public static async Task<AdbResult<object>> RunAsync(AdbCommand command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "adb",
            Arguments = $"{command.Command} {command.Arguments}".Trim(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new Exception("Failed to start adb.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception(
                string.IsNullOrWhiteSpace(error)
                    ? "ADB failed without message."
                    : error
            );
        }

        if (!string.IsNullOrWhiteSpace(error))
            throw new Exception(error);

        var parser = AdbParserRegistry.Resolve(command.ParserKey)
            ?? throw new Exception($"No parser found for {command.ParserKey}");

        return new AdbResult<object>
        {
            ParserKey = command.ParserKey,
            RawOutput = output,
            Data = parser.Parse(output)
        };
    }

    public static async Task<AdbBinaryResult> RunBinaryAsync(
    string command,
    string arguments = "")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "adb",
            Arguments = $"{command} {arguments}".Trim(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new Exception("Failed to start adb.");

        var memory = new MemoryStream();

        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new Exception(error);
        }

        memory.Position = 0;

        return new AdbBinaryResult
        {
            DataStream = memory,
            ExitCode = process.ExitCode
        };
    }

    public static AdbBinaryProcess RunBinaryStream(
        string command,
        string arguments = "")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "adb",
            Arguments = $"{command} {arguments}".Trim(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi)
            ?? throw new Exception("Failed to start adb.");

        return new AdbBinaryProcess(process);
    }

}
