using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace AdbParser.Core.Video;

public static class FfmpegLoader
{
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded)
            return;

        var tried = new List<string>();

        string[] envVars = ["FFMPEG_ROOT", "FFMPEG_ROOT_PATH", "FFMPEG_HOME", "FFMPEG_BIN", "FFMPEG_DIR"];
        foreach (var ev in envVars)
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(ev);
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                {
                    if (TryLoadFromDir(value, tried))
                    {
                        ffmpeg.RootPath = value;
                        _loaded = true;
                        return;
                    }
                    tried.Add(value);
                }
            }
            catch { }
        }

        // PATH: prefer directory that contains ffmpeg executable
        string ffmpegExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var p in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    var candidate = Path.GetFullPath(p);
                    var exePath = Path.Combine(candidate, ffmpegExe);
                    if (File.Exists(exePath))
                    {
                        if (TryLoadFromDir(candidate, tried))
                        {
                            ffmpeg.RootPath = candidate;
                            _loaded = true;
                            return;
                        }
                        tried.Add(candidate);
                    }
                }
                catch { }
            }
        }

        // Common locations per platform
        string[] candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates =
            [
                Path.Combine(pf, "ffmpeg", "bin"),
                Path.Combine(pf86, "ffmpeg", "bin"),
                "C:\\ffmpeg\\bin"
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates =
            [
                "/usr/local/lib",
                "/opt/homebrew/lib",
                "/usr/local/opt/ffmpeg/lib",
                "/usr/local/bin"
            ];
        }
        else // Linux / other Unixes
        {
            candidates =
            [
                "/usr/lib64",
                "/usr/lib",
                "/usr/local/lib",
                "/usr/local/lib64",
                "/lib",
                "/lib64",
                "/usr/lib/x86_64-linux-gnu",
                "/usr/local/bin",
                "/snap/bin"
            ];
        }

        foreach (var c in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(c))
                    continue;
                if (!Directory.Exists(c))
                    continue;

                if (TryLoadFromDir(c, tried))
                {
                    ffmpeg.RootPath = c;
                    _loaded = true;
                    return;
                }

                tried.Add(c);
            }
            catch { }
        }

        // Nothing usable found => give an actionable error.
        throw new InvalidOperationException(
            "Native FFmpeg libraries not found (libavcodec, libavutil, libswscale).\n" +
            "Paths checked: " + string.Join(", ", tried.Take(20)) + "\n" +
            "Solution: Install FFmpeg on your system (e.g. 'sudo apt install ffmpeg libavcodec-dev' on Debian/Ubuntu) " +
            "or set the FFMPEG_ROOT environment variable pointing to the directory containing the libraries.");
    }

    private static bool TryLoadFromDir(string dir, List<string> tried)
    {
        try
        {
            if (!Directory.Exists(dir))
                return false;

            string[][] requiredPatterns;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                requiredPatterns =
                [
                    ["avcodec-*.dll", "libavcodec-*.dll", "avcodec*.dll"],
                    ["avutil-*.dll", "libavutil-*.dll", "avutil*.dll"],
                    ["swscale-*.dll", "libswscale-*.dll", "swscale*.dll"]
                ];
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                requiredPatterns =
                [
                    ["libavcodec*.dylib"],
                    ["libavutil*.dylib"],
                    ["libswscale*.dylib"]
                ];
            }
            else
            {
                requiredPatterns =
                [
                    ["libavcodec*.so*", "avcodec*.so*"],
                    ["libavutil*.so*", "avutil*.so*"],
                    ["libswscale*.so*", "swscale*.so*"]
                ];
            }

            foreach (var patterns in requiredPatterns)
            {
                bool loaded = false;
                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        tried.Add(file);
                        try
                        {
                            if (NativeLibrary.TryLoad(file, out var handle))
                            {
                                NativeLibrary.Free(handle);
                                loaded = true;
                                break;
                            }
                        }
                        catch (DllNotFoundException) { }
                        catch (BadImageFormatException) { }
                        catch { }
                    }

                    if (loaded)
                        break;
                }

                if (!loaded)
                    return false;
            }

            return true;
        }
        catch { }
        return false;
    }
}
