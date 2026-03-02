using System.Diagnostics;
using AdbParser.Core.Registry;
using System.Runtime.InteropServices;
using System.IO;

namespace AdbParser.Core.Execution;

public static class AdbExecutor
{
    private static string? _cachedAdbPath;

    private static string ResolveAdbPath()
    {
        if (!string.IsNullOrEmpty(_cachedAdbPath))
            return _cachedAdbPath;

        // Allow explicit override
        var overridePath = Environment.GetEnvironmentVariable("ADB_PATH");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            _cachedAdbPath = overridePath;
            return _cachedAdbPath;
        }

        // Check Android SDK env vars
        var sdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
                      ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(sdkRoot))
        {
            candidates.Add(Path.Combine(sdkRoot, "platform-tools", GetAdbName()));
        }

        // Common locations (Windows and Linux)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            candidates.Add(Path.Combine(localAppData, "Android", "sdk", "platform-tools", GetAdbName()));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "Android", "platform-tools", GetAdbName()));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "Android", "platform-tools", GetAdbName()));

        // Search PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(p.TrimEnd(Path.DirectorySeparatorChar), GetAdbName()));
            }
            catch
            {
                // ignore malformed path elements
            }
        }

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c))
                continue;

            if (File.Exists(c))
            {
                _cachedAdbPath = c;
                return _cachedAdbPath;
            }
        }

        // Not found
        var msg = "adb executable not found. Install Android platform-tools and ensure 'adb' is on PATH, or set ANDROID_SDK_ROOT/ANDROID_HOME to the SDK root, or set ADB_PATH to the adb executable path.";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            msg += " On Linux you can usually install it as 'adb' via your package manager (e.g. apt install adb) or by installing Android SDK platform-tools.";

        throw new FileNotFoundException(msg);
    }

    private static string GetAdbName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "adb.exe";
        return "adb";
    }
    public static async Task<AdbResult<object>> RunAsync(AdbCommand command, string? deviceSerial = null)
    {
        var psi = CreateProcessStartInfo(command.Command, command.Arguments, deviceSerial);

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
        string arguments = "",
        string? deviceSerial = null)
    {
        var psi = CreateProcessStartInfo(command, arguments, deviceSerial);

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
        string arguments = "",
        string? deviceSerial = null)
    {
        var psi = CreateProcessStartInfo(command, arguments, deviceSerial);
        var process = Process.Start(psi)
            ?? throw new Exception("Failed to start adb.");

        return new AdbBinaryProcess(process);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string command, string arguments, string? deviceSerial)
    {
        var adbPath = ResolveAdbPath();
        return new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = BuildArguments(command, arguments, deviceSerial),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string BuildArguments(string command, string arguments, string? deviceSerial)
    {
        var adbArgs = $"{command} {arguments}".Trim();
        if (string.IsNullOrWhiteSpace(deviceSerial))
            return adbArgs;

        // ADB serials do not usually contain spaces, but quoting keeps the
        // invocation resilient for emulator names / future variations.
        var escapedSerial = deviceSerial.Replace("\"", "\\\"");
        return $"-s \"{escapedSerial}\" {adbArgs}";
    }
}
