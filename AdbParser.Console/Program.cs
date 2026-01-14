
using AdbParser.Core.Execution;
using AdbParser.Core.Registry;
using AdbParser.Core.Screen;
using AdbParser.Core.Video;

AdbParserSetup.RegisterParsers();

Console.WriteLine("ADB Test Console\n");

while (true)
{
    Console.WriteLine("""
    === MENU ===
    1 - Devices
    2 - GetProp
    3 - List Packages
    4 - Battery (dumpsys)
    5 - Screenshot
    6 - Screenrecord (5s)
    7 - Test Fake Screen Stream
    8 - Test Adb Screen Stream
    0 - Exit
    """);

    Console.Write("Option: ");
    var input = Console.ReadLine();

    try
    {
        switch (input)
        {
            case "1":
                Dump("Devices", await AdbExecutor.RunAsync(AdbCommand.Devices()));
                break;

            case "2":
                Dump("GetProp", await AdbExecutor.RunAsync(AdbCommand.GetProp()));
                break;

            case "3":
                Dump("Packages", await AdbExecutor.RunAsync(AdbCommand.ListPackages()));
                break;

            case "4":
                Dump("Battery", await AdbExecutor.RunAsync(
                    AdbCommand.Shell("dumpsys battery")));
                break;

            case "5":
                await TakeScreenshot();
                break;

            case "6":
                await RecordScreen();
                break;

            case "7":
                await TestFakeScreenStream();
                break;

            case "8":
                await TestScreenStreamAsync();
                break;

            case "0":
                return;

            default:
                Console.WriteLine("Invalid option.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("ERROR:");
        Console.WriteLine(ex.Message);
    }

    Console.WriteLine("\nPress ENTER to return to menu...");
    Console.ReadLine();
}

// Helpers
static async Task TakeScreenshot()
{
    var result = await AdbExecutor.RunBinaryAsync(
        "exec-out",
        "screencap -p"
    );

    using var file = File.Create("screen.png");
    await result.DataStream.CopyToAsync(file);

    Console.WriteLine("Screenshot saved to screen.png");
}

static async Task RecordScreen()
{
    Console.WriteLine("Recording screenrecord for 5 seconds...");

    using var adb = AdbExecutor.RunBinaryStream(
        "exec-out",
        "screenrecord --output-format=h264 -"
    );

    using var file = File.Create("record.h264");
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    try
    {
        await adb.Output.CopyToAsync(file, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Expected
    }

    Console.WriteLine("Recording finished (record.h264)");
}

static async Task TestFakeScreenStream()
{
    var service = new FakeScreenStreamService();
    var options = new ScreenStreamOptions
    {
        Width = 160,
        Height = 120,
        MaxFps = 10
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

    int count = 0;

    await foreach (var frame in service.StartStream(options, cts.Token))
    {
        Console.WriteLine(
            $"Frame {++count} - {frame.Width}x{frame.Height} - {frame.Data.Length} bytes");
    }

    Console.WriteLine("Stream finished.");
}

static async Task TestScreenStreamAsync()
{
    Console.WriteLine("Starting screen stream (Ctrl+C to stop)...");

    var decoder = new FakeH264Decoder();
    var service = new AdbScreenStreamService(decoder);

    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var options = new ScreenStreamOptions
    {
        // Empty for now, using defaults
    };

    var frameCount = 0;

    try
    {
        await foreach (var frame in service.StartStream(options, cts.Token))
        {
            frameCount++;
            Console.WriteLine(
                $"Frame {frameCount} - {frame.Width}x{frame.Height} - {frame.Data.Length} bytes"
            );
        }
    }
    catch (OperationCanceledException) {}

    Console.WriteLine("Stream finished.");
}

static void Dump(string title, AdbResult<object> result)
{
    var file = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
    File.WriteAllText(file, result.RawOutput);
    Console.WriteLine($"== {title} ==");
    Console.WriteLine($"Parser: {result.ParserKey}");
    Console.WriteLine($"Raw output saved to: {file}");
    Console.WriteLine("-- DATA --");

    PrintData(result.Data); Console.WriteLine();
}

static void PrintData(object? data)
{
    if (data == null)
    {
        Console.WriteLine("(null)");
        return;
    }
    if (data is IEnumerable<string> strings)
    {
        foreach (var s in strings)
            Console.WriteLine(s);
        return;
    }
    if (data is System.Collections.IEnumerable enumerable)
    {
        foreach (var item in enumerable)
            Console.WriteLine(item);
        return;
    }
    Console.WriteLine(data);
}
