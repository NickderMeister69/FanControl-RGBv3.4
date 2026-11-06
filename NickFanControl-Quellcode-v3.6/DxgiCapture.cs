using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NickFanControl;

/// <summary>
/// Bildschirm-Erfassung über DXGI Desktop Duplication (GPU). Verkleinert das Bild per Mipmaps
/// auf der Grafikkarte, bevor es gelesen wird – sehr sparsam und funktioniert auch bei vielen Vollbild-Spielen.
/// </summary>
public sealed class DxgiCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _mipTex;
    private ID3D11ShaderResourceView? _srv;
    private ID3D11Texture2D? _staging;
    private int _level, _sw, _sh;

    public bool Ready => _dup != null;

    public void Init()
    {
        Dispose();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
            using (output)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                    out _device, out _ctx).CheckError();
                _dup = output1.DuplicateOutput(_device);
            }
        }
    }

    /// <summary>Holt ein verkleinertes Bild (BGRA, w×h). false = kein neues Bild oder Fehler.</summary>
    public bool TryCapture(byte[] dst, int w, int h)
    {
        if (_dup == null || _device == null || _ctx == null) return false;

        var r = _dup.AcquireNextFrame(20, out _, out IDXGIResource res);
        if (r.Failure)
        {
            if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return false; // Bild unverändert
            throw new COMException("DXGI: " + r.Description, r.Code);
        }

        try
        {
            using (res)
            using (var tex = res.QueryInterface<ID3D11Texture2D>())
            {
                var d = tex.Description;
                if (d.Format != Format.B8G8R8A8_UNorm) throw new NotSupportedException("HDR/anderes Format: " + d.Format);
                EnsureTextures(d.Width, d.Height);
                _ctx.CopySubresourceRegion(_mipTex!, 0, 0, 0, 0, tex, 0, null);
            }
        }
        finally
        {
            _dup.ReleaseFrame();
        }

        _ctx.GenerateMips(_srv!);
        _ctx.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTex!, (uint)_level, null);
        var map = _ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var row = new byte[_sw * 4];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(_sh - 1, y * _sh / h);
                Marshal.Copy(map.DataPointer + (nint)(sy * (long)map.RowPitch), row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(_sw - 1, x * _sw / w);
                    Buffer.BlockCopy(row, sx * 4, dst, (y * w + x) * 4, 4);
                }
            }
        }
        finally
        {
            _ctx.Unmap(_staging!, 0);
        }
        return true;
    }

    private void EnsureTextures(uint width, uint height)
    {
        if (_mipTex != null && _mipTex.Description.Width == width && _mipTex.Description.Height == height) return;
        _srv?.Dispose(); _mipTex?.Dispose(); _staging?.Dispose();

        _mipTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 0, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.GenerateMips
        });
        _srv = _device.CreateShaderResourceView(_mipTex);

        // Mip-Stufe wählen, die noch mindestens ~128 Pixel breit ist
        _level = 0;
        while ((width >> (_level + 1)) >= 128 && (height >> (_level + 1)) >= 72) _level++;
        _sw = (int)Math.Max(1, width >> _level);
        _sh = (int)Math.Max(1, height >> _level);

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_sw, Height = (uint)_sh, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None
        });
    }

    public void Dispose()
    {
        _staging?.Dispose(); _staging = null;
        _srv?.Dispose(); _srv = null;
        _mipTex?.Dispose(); _mipTex = null;
        _dup?.Dispose(); _dup = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
    }
}
