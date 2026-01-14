using System.Diagnostics;

namespace AdbParser.Core.Execution;

public sealed class AdbBinaryProcess : IDisposable
{
    public Process Process { get; }
    public Stream Output { get; }

    internal AdbBinaryProcess(Process process)
    {
        Process = process;
        Output = process.StandardOutput.BaseStream;
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
