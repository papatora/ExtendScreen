using System;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AzurateMirror.Sender.Capture;

/// <summary>
/// Captures a display via DXGI Desktop Duplication (GPU-accelerated, low latency).
/// One instance == one monitor's output. Frames come back as GPU textures (ID3D11Texture2D)
/// so FrameEncoder can hand them to Media Foundation without a CPU round-trip where possible.
///
/// Takes an explicit (adapterIndex, outputIndex) pair rather than a single flat index - v1 had
/// a latent bug here where a single "outputIndex" was actually matched per-adapter (resetting
/// at each new adapter), silently picking the wrong output whenever the target display lived on
/// a different DXGI adapter than the first one enumerated. That matters a lot for V2 since the
/// virtual display driver commonly registers as its own separate adapter from the real GPU -
/// see VirtualDisplayManager.FindOutput() for how the correct pair gets discovered.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    public ID3D11Device Device { get; private set; } = null!;
    public ID3D11DeviceContext Context { get; private set; } = null!;
    public int Width { get; }
    public int Height { get; }
    public string AdapterDescription { get; }

    private readonly IDXGIOutputDuplication _duplication;
    private bool _disposed;

    public DesktopDuplicator(uint adapterIndex, uint outputIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        if (!factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 chosenAdapter).Success)
            throw new InvalidOperationException($"No DXGI adapter at index {adapterIndex}.");

        if (!chosenAdapter.EnumOutputs(outputIndex, out IDXGIOutput rawOutput).Success)
        {
            chosenAdapter.Dispose();
            throw new InvalidOperationException($"No DXGI output at index {outputIndex} on adapter {adapterIndex}.");
        }

        using var chosenOutput = rawOutput.QueryInterface<IDXGIOutput1>();
        rawOutput.Dispose();

        AdapterDescription = chosenAdapter.Description1.Description;

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            chosenAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels,
            out ID3D11Device device).CheckError();

        chosenAdapter.Dispose();

        Device = device;
        Context = device.ImmediateContext;

        var bounds = chosenOutput.Description.DesktopCoordinates;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        _duplication = chosenOutput.DuplicateOutput(Device);
    }

    /// <summary>
    /// Blocks up to timeoutMs waiting for the next frame. Returns null on timeout (no new frame),
    /// which is normal when the desktop hasn't changed. Caller must Dispose the returned texture.
    /// </summary>
    public ID3D11Texture2D? AcquireNextFrame(int timeoutMs, out bool timedOut)
    {
        timedOut = false;
        Result result = _duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out IDXGIResource? desktopResource);

        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            timedOut = true;
            return null;
        }
        result.CheckError();

        using (desktopResource)
        {
            var texture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            return texture;
        }
    }

    public void ReleaseFrame() => _duplication.ReleaseFrame();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _duplication.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
