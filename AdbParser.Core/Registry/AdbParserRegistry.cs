using AdbParser.Core.Parsers;

namespace AdbParser.Core.Registry;

public static class AdbParserRegistry
{
    private static readonly Dictionary<string, IAdbParserWrapper> _parsers = new();

    public static void Register<T>(string key, IAdbParser<T> parser)
    {
        _parsers[key] = new AdbParserWrapper<T>(parser);
    }

    public static IAdbParserWrapper? Resolve(string key)
    {
        if (_parsers.TryGetValue(key, out var exact))
            return exact;

        var idx = key.IndexOf(':');
        if (idx > 0)
        {
            var wildcard = key[..idx] + ":*";
            if (_parsers.TryGetValue(wildcard, out var partial))
                return partial;
        }

        return _parsers.TryGetValue("*", out var generic) ? generic : null;
    }
}
