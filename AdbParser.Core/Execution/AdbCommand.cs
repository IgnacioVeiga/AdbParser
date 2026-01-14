namespace AdbParser.Core.Execution;

public class AdbCommand
{
    public string Command { get; }
    public string Arguments { get; }
    public string ParserKey { get; }

    private AdbCommand(string command, string arguments, string parserKey)
    {
        Command = command;
        Arguments = arguments;
        ParserKey = parserKey;
    }

    // adb devices
    public static AdbCommand Devices()
        => new("devices", "", "devices");

    // adb shell getprop
    public static AdbCommand GetProp()
        => new("shell", "getprop", "shell:getprop");

    // adb shell pm list packages
    public static AdbCommand ListPackages()
        => new("shell", "pm list packages", "shell:pm");

    // Generic: adb shell <anything>
    public static AdbCommand Shell(string arguments)
        => new("shell", arguments, "shell:*");

    // Ultra generic
    public static AdbCommand Raw(string command, string arguments = "")
        => new(command, arguments, "*");
}
