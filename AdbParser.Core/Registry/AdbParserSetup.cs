using AdbParser.Core.Parsers;

namespace AdbParser.Core.Registry;

public static class AdbParserSetup
{
    public static void RegisterParsers()
    {
        AdbParserRegistry.Register("devices", new DevicesParser());
        AdbParserRegistry.Register("shell:getprop", new GetPropParser());
        AdbParserRegistry.Register("shell:pm", new PackagesParser());
        AdbParserRegistry.Register("shell:*", new GenericParser());
        AdbParserRegistry.Register("*", new GenericParser());
    }
}
