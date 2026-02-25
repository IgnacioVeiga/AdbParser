# Copilot instructions for AdbParser

This file gives targeted, actionable guidance for AI coding agents working on this repository.

- **Purpose:** The solution provides small tools to run ADB commands, parse outputs, and stream device screens (CLI + Avalonia GUI).

**Big picture**
- Multi-project .NET solution (net8.0): `AdbParser.Core` (parsers, execution, screen/video plumbing), `AdbParser.Console` (CLI examples), `AdbParser.Gui` (Avalonia UI).
- `AdbExecutor` executes `adb` processes and returns parsed results via the `AdbParserRegistry` lookup (see [AdbParser.Core/Execution/AdbExecutor.cs](AdbParser.Core/Execution/AdbExecutor.cs)).
- Parsers implement `IAdbParser<T>` and are wrapped as `IAdbParserWrapper` instances in the registry. See [AdbParser.Core/Parsers](AdbParser.Core/Parsers).
- Screen/video: `AdbScreenStreamService` streams H.264 from `adb exec-out screenrecord -`, decoders live under `AdbParser.Core/Video` (FFmpeg interop).
- FFmpeg integration is intended to use system-native libraries (Linux/Fedora supported via `FFMPEG_ROOT` or standard library paths); current package binding is `FFmpeg.AutoGen 7.1.1`.

**Key files to reference**
- Parser registry and setup: [AdbParser.Core/Registry/AdbParserRegistry.cs](AdbParser.Core/Registry/AdbParserRegistry.cs), [AdbParser.Core/Registry/AdbParserSetup.cs](AdbParser.Core/Registry/AdbParserSetup.cs)
- Execution layer: [AdbParser.Core/Execution/AdbExecutor.cs](AdbParser.Core/Execution/AdbExecutor.cs)
- Parser examples: [AdbParser.Core/Parsers/DevicesParser.cs](AdbParser.Core/Parsers/DevicesParser.cs), [AdbParser.Core/Parsers/GetPropParser.cs](AdbParser.Core/Parsers/GetPropParser.cs)
- Console runner: [AdbParser.Console/Program.cs](AdbParser.Console/Program.cs)
- FFmpeg loader: [AdbParser.Core/Video/FfmpegLoader.cs](AdbParser.Core/Video/FfmpegLoader.cs)
- H.264 decoder contract: [AdbParser.Core/Video/IH264Decoder.cs](AdbParser.Core/Video/IH264Decoder.cs) (`Feed(...)` + `TryGetFrame(...)` to drain multiple frames per chunk)

**How to run / developer workflows**
- Build solution: `dotnet build AdbParser.sln` (targets net8.0). Use the solution root as CWD.
- Run console demo: `dotnet run --project AdbParser.Console` — useful to exercise parsers and screen flows.
- Run GUI: `dotnet run --project AdbParser.Gui` (requires GUI environment).
- The code calls the `adb` executable directly; ensure `adb` is available in `PATH` or provide full path when testing locally.
- For native video decoding, FFmpeg native libs must be present. `FfmpegLoader` searches `FFMPEG_ROOT`/`PATH` and common locations; set `FFMPEG_ROOT` if needed.
- The GUI (`MainWindow`) now surfaces stream/FFmpeg errors in a status label instead of crashing immediately on decoder initialization failures.

**Project-specific conventions and patterns**
- Parser registration: static registration is done in `AdbParserSetup.RegisterParsers()`. Add new parsers by calling `AdbParserRegistry.Register("key", new MyParser())`.
- Parser keys support partial/wildcard resolution. Example keys: `devices`, `shell:getprop`, `shell:pm`, `shell:*`, `*` (see registry resolve logic).
- Parsers implement `IAdbParser<T>` and return domain types (avoid changing the registry shape).
- The core design separates: command execution (AdbExecutor) → raw output → registry resolves parser → typed data.

**Examples**
- Add a new parser skeleton:

```csharp
public class MyParser : IAdbParser<MyResult>
{
    public MyResult Parse(string rawOutput) { /* parse and return typed object */ }
}

// register
AdbParserRegistry.Register("shell:mycmd", new MyParser());
```

- Use console flows to validate parsers quickly: call the relevant `AdbExecutor.RunAsync(AdbCommand.XXX())` from `AdbParser.Console/Program.cs`.

**Integration notes / gotchas**
- `AdbExecutor` throws on non-zero `adb` exit codes; callers generally surface those messages to the console — tests or agents must handle exceptions.
- Binary streams: `RunBinaryAsync` returns an in-memory stream; `RunBinaryStream` returns a process wrapper for streaming — prefer streaming when handling large outputs (screenrecord).
- H.264 chunk boundaries are arbitrary; when modifying decoders/services, do not assume one frame per read. Feed bytes, then drain all available decoded frames.
- FFmpeg native libs are required for `FfmpegH264Decoder`. On Linux use package manager (`apt install ffmpeg`) or set `FFMPEG_ROOT` to a directory with `libavcodec`/`libavformat`.

If anything here is unclear or you want me to expand examples (e.g., add a new parser end-to-end or a small unit-test harness), tell me which section to improve.
