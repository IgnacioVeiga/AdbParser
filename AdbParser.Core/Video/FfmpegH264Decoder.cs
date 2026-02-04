using AdbParser.Core.Screen;
using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace AdbParser.Core.Video;

/// <summary>
/// Decodes an Annex B H.264 byte stream (as emitted by "adb exec-out screenrecord -")
/// into BGRA frames for the GUI renderer, handling arbitrary chunk boundaries by
/// parsing and stitching NAL units before feeding the FFmpeg decoder.
/// </summary>
public sealed unsafe class FfmpegH264Decoder : IH264Decoder
{
    private AVCodecContext* _codecContext;
    private AVCodec* _codec;
    private AVCodecParserContext* _parser;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private byte[]? _frameBuffer;
    private int _frameWidth;
    private int _frameHeight;
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

        _parser = ffmpeg.av_parser_init((int)_codec->id);
        if (_parser == null)
        {
            throw new InvalidOperationException("Failed to create H264 parser context");
        }

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
        {
            throw new InvalidOperationException("Failed to allocate AVFrame");
        }

        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
        {
            throw new InvalidOperationException("Failed to allocate AVPacket");
        }

        _initialized = true;
    }

    public bool TryDecode(ReadOnlySpan<byte> h264Data, out VideoFrame frame)
    {
        // We parse the raw H.264 byte stream into packets and immediately decode them.
        // ADB delivers arbitrary chunk boundaries, so the parser is mandatory to stitch
        // NAL units before handing them to the decoder, and any decoded frame is
        // converted to BGRA for the GUI renderer to consume safely on any platform.
        frame = default!;

        if (!_initialized || h264Data.IsEmpty)
        {
            return false;
        }

        fixed (byte* dataPtr = h264Data)
        {
            byte* data = dataPtr;
            var remaining = h264Data.Length;

            while (remaining > 0)
            {
                int parsed = ffmpeg.av_parser_parse2(
                    _parser,
                    _codecContext,
                    &_packet->data,
                    &_packet->size,
                    data,
                    remaining,
                    ffmpeg.AV_NOPTS_VALUE,
                    ffmpeg.AV_NOPTS_VALUE,
                    0
                );

                if (parsed < 0)
                {
                    return false;
                }

                data += parsed;
                remaining -= parsed;

                if (_packet->size <= 0)
                {
                    continue;
                }

                if (TryDecodePacket(out frame))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_parser != null)
        {
            ffmpeg.av_parser_close(_parser);
            _parser = null;
        }

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

    private bool TryDecodePacket(out VideoFrame frame)
    {
        frame = default!;

        int sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
        if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            return false;
        }

        while (true)
        {
            int receiveRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (receiveRet < 0)
            {
                return false;
            }

            frame = ConvertFrame(_frame);
            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_packet_unref(_packet);
            return true;
        }

        ffmpeg.av_packet_unref(_packet);
        return false;
    }

    private VideoFrame ConvertFrame(AVFrame* decodedFrame)
    {
        int width = decodedFrame->width;
        int height = decodedFrame->height;

        EnsureConversionResources(width, height, (AVPixelFormat)decodedFrame->format);

        byte_ptrArray4 dstData = default;
        int_array4 dstLinesize = default;

        fixed (byte* dstBuffer = _frameBuffer)
        {
            ffmpeg.av_image_fill_arrays(
                ref dstData,
                ref dstLinesize,
                dstBuffer,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                width,
                height,
                1
            );

            ffmpeg.sws_scale(
                _swsContext,
                decodedFrame->data,
                decodedFrame->linesize,
                0,
                height,
                dstData,
                dstLinesize
            );
        }

        var managedCopy = new byte[_frameBuffer!.Length];
        Buffer.BlockCopy(_frameBuffer, 0, managedCopy, 0, managedCopy.Length);

        return new VideoFrame
        {
            Data = managedCopy,
            Width = width,
            Height = height,
            Format = PixelFormat.Bgra32
        };
    }

    private void EnsureConversionResources(int width, int height, AVPixelFormat sourceFormat)
    {
        if (_swsContext != null && _frameBuffer != null && _frameWidth == width && _frameHeight == height)
        {
            return;
        }

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        _swsContext = ffmpeg.sws_getContext(
            width,
            height,
            sourceFormat,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR,
            null,
            null,
            null
        );

        if (_swsContext == null)
        {
            throw new InvalidOperationException("Failed to create FFmpeg scaling context for BGRA conversion.");
        }

        _frameWidth = width;
        _frameHeight = height;
        int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, width, height, 1);
        _frameBuffer = new byte[bufferSize];
    }
}
