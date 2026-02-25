using System.Diagnostics;

namespace AdbParser.Core.Execution;

public sealed class AdbBinaryProcess : IDisposable
{
    private readonly Task<string> _stderrDrainTask;

    public Process Process { get; }
    public Stream Output { get; }
    public Task<string> ErrorOutput => _stderrDrainTask;

    internal AdbBinaryProcess(Process process)
    {
        Process = process;
        Output = process.StandardOutput.BaseStream;
        _stderrDrainTask = process.StandardError.ReadToEndAsync();
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill();
        }
        catch { }

        Process.Dispose();
    }
}
