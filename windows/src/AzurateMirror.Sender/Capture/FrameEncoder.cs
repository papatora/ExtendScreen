using System;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace AzurateMirror.Sender.Capture;

public sealed record EncodedAccessUnit(byte[] AnnexB, bool IsKeyFrame, ulong TimestampMs);

/// <summary>
/// Wraps a Media Foundation hardware H.264 encoder MFT. Feeds BGRA textures in,
/// yields Annex-B H.264 access units out. Picks whichever hardware MFT the driver
/// stack surfaces first (Intel/AMD/NVIDIA) - see docs/PROTOCOL.md and the plan notes
/// on the hybrid-GPU laptop: do not assume a specific vendor.
/// </summary>
public sealed class FrameEncoder : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _mftProvidesOwnSamples;
    private bool _disposed;

    public string EncoderName { get; }
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public bool? UsingGpuConversion => _gpuSetupAttempted ? _useGpuConversion : null;

    // GPU BGRA->NV12 conversion (ID3D11VideoDevice/VideoProcessorBlt) - measured 5.26x faster than
    // the CPU Parallel.For path (1.61ms vs 8.49ms/frame @ 2560x1600, see windows/ExperimentOnly).
    // PERMANENTLY DISABLED as of 2026-09-03: live-diagnosed as the actual cause of a black
    // screen + ghosted-cursor-trail corruption on the tablet (Extend mode, virtual display).
    // Proven by direct A/B test - identical repro steps, only difference was forcing this flag
    // false: GPU path corrupted every time, CPU path clean every time. Several other real bugs
    // were found and fixed chasing this symptom before landing on the actual cause (an Android-
    // side layout loop, an over-rate encode loop, a periodic-keyframe fix that made things worse)
    // - none of them were it. The exact mechanism inside VideoProcessorBlt/the cached
    // input-view reuse hasn't been root-caused further; CPU conversion is already comfortably
    // fast enough (8.49ms/frame leaves plenty of headroom under the 33ms/frame budget for 30fps),
    // so the pragmatic fix is to stop using the broken path rather than keep debugging complex
    // D3D11 video-processor code with a known-good fallback sitting right there. Left the GPU
    // code in place (untouched) rather than deleted, in case someone wants to revisit root-causing
    // it later - just never let _useGpuConversion become true.
    private bool _useGpuConversion;
    private bool _gpuSetupAttempted;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _videoEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11Texture2D? _nv12Staging;
    private ID3D11VideoProcessorOutputView? _outputView;
    private ID3D11VideoProcessorInputView? _cachedInputView;
    private ID3D11Texture2D? _cachedInputTexture;

    public FrameEncoder(int width, int height, uint targetBitrate = 0, uint fps = 30)
    {
        _width = width;
        _height = height;

        // Default bitrate scales with pixel count - the old flat 8Mbps was tuned for the virtual
        // display's 800x600 default and looked visibly soft/blocky on sharp desktop UI (text,
        // window chrome) once the display runs at its native 2560x1600. ~4.9 bits/pixel/sec at
        // 30fps is a generous starting point for screen content (much higher entropy than natural
        // video at the same bpp); clamp so tiny/huge sources still get something sane.
        if (targetBitrate == 0)
            targetBitrate = (uint)Math.Clamp((long)width * height * 5 / 1000 * 1000, 6_000_000, 35_000_000);

        MediaFactory.MFStartup(true);

        var info = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
            null,
            info);

        IMFActivate? activate = null;
        foreach (var a in activates) { activate = a; break; }

        if (activate is null)
            throw new InvalidOperationException("No hardware H.264 encoder MFT found on this system.");

        EncoderName = activate.FriendlyName ?? "(unnamed hardware encoder)";
        _transform = activate.ActivateObject<IMFTransform>();

        // Hardware MFTs are asynchronous; the caller must explicitly opt in before touching
        // SetInputType/SetOutputType or MF returns MF_E_TRANSFORM_ASYNC_LOCKED.
        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);

        ConfigureTypes(targetBitrate, fps);

        // Many hardware (GPU-backed) encoder MFTs manage their own output sample pool and ignore
        // any sample we pre-allocate for ProcessOutput - they silently leave it empty and hand
        // back a replacement sample via OutputDataBuffer.Sample instead. Check which mode this
        // MFT uses so EncodeFrame reads from the right place.
        var outputStreamInfo = _transform.GetOutputStreamInfo(0);
        _mftProvidesOwnSamples = (outputStreamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

        _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void ConfigureTypes(uint targetBitrate, uint fps)
    {
        ulong frameSize = ((ulong)(uint)_width << 32) | (uint)_height;
        ulong frameRate = ((ulong)fps << 32) | 1;

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, targetBitrate);
        outputType.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
        outputType.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        _transform.SetOutputType(0, outputType, 0);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
        inputType.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        _transform.SetInputType(0, inputType, 0);

        // Low-latency / rate-control tuning via ICodecAPI is not exposed by Vortice.MediaFoundation
        // 3.8.3's bindings. The MFT's own defaults are used for v1; revisit if quality/latency
        // needs hand-tuning once the pipeline is proven end-to-end.
    }

    /// <summary>
    /// Encodes one BGRA texture. Internally converts to NV12 via a staging texture + CPU copy
    /// for the first working version (Phase 2 goal: prove the pipeline). A GPU-side color
    /// convert (video processor blit) can replace this later if the CPU copy proves too slow.
    /// </summary>
    public List<EncodedAccessUnit> EncodeFrame(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D bgraTexture, ulong timestampMs)
    {
        var results = new List<EncodedAccessUnit>();

        using IMFSample sample = TextureToNv12Sample(device, context, bgraTexture, timestampMs);

        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // MF_E_NOTACCEPTING - transform's input queue is full; drop this frame and retry next tick.
            return results;
        }

        while (true)
        {
            var dataBuffer = new OutputDataBuffer { StreamID = 0 };

            if (!_mftProvidesOwnSamples)
            {
                using var buf = MediaFactory.MFCreateMemoryBuffer(Math.Max(_width * _height * 2, 1 << 20));
                var providedSample = MediaFactory.MFCreateSample();
                providedSample.AddBuffer(buf); // AddBuffer addrefs internally, safe to dispose buf's own ref above
                dataBuffer.Sample = providedSample;
            }

            var status = _transform.ProcessOutput(Vortice.MediaFoundation.ProcessOutputFlags.None, 1, ref dataBuffer, out _);

            if (status.Failure)
            {
                dataBuffer.Sample?.Dispose();
                break;
            }

            // When the MFT provides its own samples, dataBuffer.Sample now points at a fresh
            // sample it allocated (replacing whatever we passed in, including null) - that's
            // the one with real data either way.
            using IMFSample? resultSample = dataBuffer.Sample;
            if (resultSample is null)
                break;

            using IMFMediaBuffer resultBuffer = resultSample.ConvertToContiguousBuffer();

            uint isKey = 0;
            try { isKey = resultSample.GetUInt32(SampleAttributeKeys.CleanPoint); } catch { }

            byte[] data = ReadBuffer(resultBuffer);
            if (Sps == null || Pps == null)
            {
                TryExtractSpsPpsFromSequenceHeader();
                if (Sps == null || Pps == null)
                    ExtractSpsPpsIfPresent(data);
            }
            results.Add(new EncodedAccessUnit(data, isKey != 0, timestampMs));
        }

        return results;
    }

    /// <summary>
    /// Primary SPS/PPS source: the MF_MT_MPEG_SEQUENCE_HEADER blob on the negotiated output
    /// type (populated once the encoder has produced its first output). This is the documented,
    /// reliable way to get parameter sets from a Windows H.264 MFT - per-frame inline NALs
    /// (ExtractSpsPpsIfPresent) are only a fallback for encoders that don't populate it.
    /// </summary>
    private void TryExtractSpsPpsFromSequenceHeader()
    {
        try
        {
            using var currentOutputType = _transform.GetOutputCurrentType(0);
            byte[]? header = currentOutputType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (header == null || header.Length == 0) return;

            foreach (var (start, end, nalType) in EnumerateNalUnits(header))
            {
                if (nalType == 7 && Sps == null) Sps = WithStartCode(header[start..end]);
                else if (nalType == 8 && Pps == null) Pps = WithStartCode(header[start..end]);
            }
        }
        catch
        {
            // Attribute not populated yet, or this MFT doesn't expose it - fall back to inline NALs.
        }
    }

    private static byte[] ReadBuffer(IMFMediaBuffer buffer)
    {
        buffer.Lock(out IntPtr ptr, out _, out int curLen);
        try
        {
            var data = new byte[curLen];
            System.Runtime.InteropServices.Marshal.Copy(ptr, data, 0, curLen);
            return data;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>Pulls SPS/PPS (NAL types 7/8) out of the first keyframe access unit for VIDEO_CONFIG.</summary>
    private void ExtractSpsPpsIfPresent(byte[] annexB)
    {
        if (Sps != null && Pps != null) return;

        foreach (var (start, end, nalType) in EnumerateNalUnits(annexB))
        {
            if (nalType == 7 && Sps == null) Sps = WithStartCode(annexB[start..end]);
            else if (nalType == 8 && Pps == null) Pps = WithStartCode(annexB[start..end]);
        }
    }

    /// <summary>
    /// MediaCodec's csd-0/csd-1 buffers are conventionally expected WITH the Annex-B start code
    /// prefix (same convention scrcpy and ExoPlayer use) - EnumerateNalUnits deliberately excludes
    /// it from its ranges, so re-add it here for anything headed into VIDEO_CONFIG.
    /// </summary>
    private static byte[] WithStartCode(byte[] nalWithoutStartCode)
    {
        var result = new byte[4 + nalWithoutStartCode.Length];
        result[0] = 0; result[1] = 0; result[2] = 0; result[3] = 1;
        Buffer.BlockCopy(nalWithoutStartCode, 0, result, 4, nalWithoutStartCode.Length);
        return result;
    }

    /// <summary>
    /// Splits an Annex-B buffer into (start, end, nalType) ranges bounding each NAL unit
    /// (header byte through payload, start-code excluded). Scans with explicit index-skipping
    /// so a 4-byte start code (00 00 00 01) is never also matched as an overlapping 3-byte one
    /// (00 00 01) at the next offset - that overlap previously produced a bogus zero-length NAL
    /// ahead of every 4-byte-prefixed unit, which silently broke SPS/PPS extraction.
    /// </summary>
    public static IEnumerable<(int start, int end, int nalType)> EnumerateNalUnits(byte[] data)
    {
        var starts = new List<int>();   // byte offset just after each start code
        var codeLens = new List<int>(); // 3 or 4, length of that start code

        int i = 0;
        while (i + 2 < data.Length)
        {
            if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                starts.Add(i + 4);
                codeLens.Add(4);
                i += 4;
            }
            else if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                starts.Add(i + 3);
                codeLens.Add(3);
                i += 3;
            }
            else
            {
                i++;
            }
        }

        for (int n = 0; n < starts.Count; n++)
        {
            int s = starts[n];
            if (s >= data.Length) continue;
            int nalType = data[s] & 0x1F;
            int e = n + 1 < starts.Count ? starts[n + 1] - codeLens[n + 1] : data.Length;
            yield return (s, e, nalType);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        _stagingTexture?.Dispose();
        _cachedInputView?.Dispose();
        _outputView?.Dispose();
        _nv12Staging?.Dispose();
        _nv12Texture?.Dispose();
        _videoProcessor?.Dispose();
        _videoEnumerator?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
        _transform.Dispose();
        MediaFactory.MFShutdown();
    }

    // --- BGRA -> NV12 conversion, CPU path (Phase 2: correctness first, optimize later) ---

    private ID3D11Texture2D? _stagingTexture;

    private IMFSample TextureToNv12Sample(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D bgraTexture, ulong timestampMs)
    {
        if (!_gpuSetupAttempted)
        {
            // GPU conversion is permanently disabled - see the comment on _useGpuConversion's
            // declaration above for why. Not even attempting SetUpGpuConversion here (rather than
            // attempting it and then forcing the flag back off) so UsingGpuConversion's status
            // log doesn't misreport a real "setup failed on this hardware" fallback when nothing
            // was actually attempted or wrong with the hardware.
            _gpuSetupAttempted = true;
            _useGpuConversion = false;
        }

        byte[] nv12 = _useGpuConversion
            ? TryGpuConvert(device, context, bgraTexture) ?? CpuConvert(context, bgraTexture)
            : CpuConvert(context, bgraTexture);

        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        System.Runtime.InteropServices.Marshal.Copy(nv12, 0, ptr, nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;

        // AddBuffer addrefs internally - `buffer`'s own COM reference must still be released here
        // (via `using` above), same pattern as the fallback-sample path in EncodeFrame. This was
        // previously missing: every encoded frame leaked one full NV12-sized native buffer
        // (~6MB at 2560x1600) that only got reclaimed whenever the GC finalizer eventually ran,
        // which measured as a 20-65GB working set after a few minutes of sustained streaming.
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = (long)(timestampMs * 10_000); // 100ns units
        sample.SampleDuration = 10_000_000 / 30;
        return sample;
    }

    private byte[] CpuConvert(ID3D11DeviceContext context, ID3D11Texture2D bgraTexture)
    {
        _stagingTexture ??= CreateStagingTexture(context.Device);
        context.CopyResource(_stagingTexture, bgraTexture);
        var mapped = context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { return BgraToNv12(mapped.DataPointer, (int)mapped.RowPitch, _width, _height); }
        finally { context.Unmap(_stagingTexture, 0); }
    }

    /// <summary>
    /// One-time setup of the D3D11 video processor for GPU BGRA-&gt;NV12 conversion. Throws on any
    /// failure so the caller can fall back to the CPU path - don't assume this succeeds on every
    /// GPU/driver. Requires the ID3D11Device to have been created with
    /// DeviceCreationFlags.VideoSupport (see DesktopDuplicator.cs) and the SOURCE texture to have
    /// BindFlags.RenderTarget set, not just ShaderResource - discovered empirically via
    /// windows/ExperimentOnly (CreateVideoProcessorInputView fails with E_INVALIDARG otherwise on
    /// this hardware; RenderTarget is already set on MainWindow's lastGoodFrame cache texture for
    /// the cursor-compositing GDI trick, so this is satisfied for free in production).
    /// </summary>
    private void SetUpGpuConversion(ID3D11Device device)
    {
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Vortice.DXGI.Rational(30, 1),
            InputWidth = (uint)_width,
            InputHeight = (uint)_height,
            OutputFrameRate = new Vortice.DXGI.Rational(30, 1),
            OutputWidth = (uint)_width,
            OutputHeight = (uint)_height,
            Usage = VideoUsage.OptimalSpeed,
        };
        _videoEnumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_videoEnumerator, 0);

        var nv12Desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        _nv12Texture = device.CreateTexture2D(nv12Desc);
        _nv12Staging = device.CreateTexture2D(nv12Desc with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        var outputViewDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
        };
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12Texture, _videoEnumerator, outputViewDesc);
    }

    /// <summary>Returns null (never throws) on any GPU-path failure so the caller falls back to
    /// CPU for that frame - a transient GPU hiccup shouldn't kill the whole encode pipeline.</summary>
    private byte[]? TryGpuConvert(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D bgraTexture)
    {
        try
        {
            if (!ReferenceEquals(_cachedInputTexture, bgraTexture))
            {
                _cachedInputView?.Dispose();
                var inputViewDesc = new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                };
                _cachedInputView = _videoDevice!.CreateVideoProcessorInputView(bgraTexture, _videoEnumerator!, inputViewDesc);
                _cachedInputTexture = bgraTexture;
            }

            var stream = new VideoProcessorStream { Enable = true, InputSurface = _cachedInputView };
            var result = _videoContext!.VideoProcessorBlt(_videoProcessor!, _outputView!, 0, new[] { stream });
            if (result.Failure) return null;

            context.CopyResource(_nv12Staging!, _nv12Texture!);
            var mapped = context.Map(_nv12Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try { return ReadNv12Planes(mapped.DataPointer, (int)mapped.RowPitch, _width, _height); }
            finally { context.Unmap(_nv12Staging!, 0); }
        }
        catch
        {
            return null;
        }
    }

    private static unsafe byte[] ReadNv12Planes(IntPtr src, int stride, int width, int height)
    {
        var nv12 = new byte[width * height + width * height / 2];
        byte* s = (byte*)src;
        for (int y = 0; y < height; y++)
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(s + y * stride), nv12, y * width, width);
        int yPlaneBytes = width * height;
        int uvHeight = height / 2;
        byte* uvBase = s + (height * stride);
        for (int y = 0; y < uvHeight; y++)
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(uvBase + y * stride), nv12, yPlaneBytes + y * width, width);
        return nv12;
    }

    private ID3D11Texture2D CreateStagingTexture(ID3D11Device device)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        return device.CreateTexture2D(desc);
    }

    /// <summary>
    /// Runs the per-pixel BGRA-&gt;NV12 conversion across all CPU cores via Parallel.For - at
    /// 2560x1600 (~4.1M pixels/frame) the original single-threaded scalar loop was the actual
    /// fps ceiling (measured ~18fps against a genuine 30fps source), NOT DXGI's normal
    /// change-driven capture behavior as first assumed. Each thread only ever touches its own
    /// disjoint row(s) of the `nv12` output array, so no synchronization is needed between them.
    /// </summary>
    private static unsafe byte[] BgraToNv12(IntPtr src, int srcStride, int width, int height)
    {
        var nv12 = new byte[width * height + width * height / 2];
        int yPlaneSize = width * height;

        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            byte* row = (byte*)src + y * srcStride;
            for (int x = 0; x < width; x++)
            {
                byte b = row[x * 4 + 0];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[y * width + x] = (byte)Math.Clamp(yVal, 0, 255);
            }
        });

        System.Threading.Tasks.Parallel.For(0, height / 2, yHalf =>
        {
            int y = yHalf * 2;
            byte* row = (byte*)src + y * srcStride;
            for (int x = 0; x < width; x += 2)
            {
                byte b = row[x * 4 + 0];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];
                int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                int uvIndex = yPlaneSize + (y / 2) * width + x;
                nv12[uvIndex] = (byte)Math.Clamp(uVal, 0, 255);
                nv12[uvIndex + 1] = (byte)Math.Clamp(vVal, 0, 255);
            }
        });

        return nv12;
    }
}
