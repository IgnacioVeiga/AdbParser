using AdbParser.Core.Screen;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace AdbParser.Core.Video;

public sealed unsafe class FfmpegH264Decoder : IH264Decoder
{
    private AVCodecContext* _codecContext;
    private AVCodec* _codec;
    private bool _initialized;

    public FfmpegH264Decoder()
    {
        FfmpegLoader.Load();
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            _codec = ffmpeg.avcodec_find_decoder_by_name("h264");
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                "Error initializing FFmpeg.AutoGen. This usually occurs when the native FFmpeg libraries could not be loaded, or there is an architecture incompatibility.\n" +
                "Make sure the libraries (libavcodec, libavformat, libavutil) are installed and that the FFMPEG_ROOT variable points to the correct directory.\n" +
                "Original message: " + ex.Message, ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "Native FFmpeg library not found. Install FFmpeg or set FFMPEG_ROOT. Message: " + ex.Message, ex);
        }

        if (_codec == null)
        {
            throw new InvalidOperationException($"H264 decoder not found (ffmpeg.RootPath={ffmpeg.RootPath}). The library version might not expose the expected symbol.");
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(_codec);
        if (_codecContext == null)
        {
            throw new InvalidOperationException("Failed to create AVCodecContext");
        }

        int ret = ffmpeg.avcodec_open2(_codecContext, _codec, null);
        if (ret < 0)
        {
            string msg = AvErrorString(ret);
            AVCodecContext* ctx = _codecContext;
            ffmpeg.avcodec_free_context(&ctx);
            _codecContext = null;
            throw new InvalidOperationException($"Failed to open H264 decoder: {msg} (ffmpeg.RootPath={ffmpeg.RootPath})");
        }

        _initialized = true;
    }

    public bool TryDecode(ReadOnlySpan<byte> h264Data, out VideoFrame frame)
    {
        frame = default!;
        return false; // Not yet implemented
    }

    public void Dispose()
    {
        if (_codecContext != null)
        {
            AVCodecContext* ctx = _codecContext;
            ffmpeg.avcodec_free_context(&ctx);
            _codecContext = null;
        }
        _initialized = false;
    }

    private static string AvErrorString(int err)
    {
        const int size = 1024;
        var buffer = stackalloc sbyte[size];
        ffmpeg.av_strerror(err, (byte*)buffer, size);
        return Marshal.PtrToStringAnsi(new IntPtr(buffer)) ?? $"error_{err}";
    }
}
