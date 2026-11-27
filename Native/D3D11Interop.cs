using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Windows.Devices.Display.Core;
using WinRT;

namespace SideScreenHUD.Native;

/// <summary>
/// Minimal zero-dependency Direct3D 11 + IDisplayDeviceInterop bridge verified against
/// Windows 11 SDK 10.0.26100.0 headers (d3d11.h, d3d11_1.h, Windows.Devices.Display.Core.Interop.h).
/// Uploads BGRA32 frames into double-buffered Windows.Devices.Display.Core hardware primary surfaces
/// using branch-free / 32x32 cache-tiled rotation kernels.
/// </summary>
internal sealed unsafe class D3D11DirectPresenter : IDisposable
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_ID3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDisplayDeviceInterop = new("64338358-366A-471B-BD56-DD8EF48E439B");

    private const uint GENERIC_ALL = 0x10000000;
    private const uint D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_WRITE = 0x10000;
    private const uint D3D11_MAP_WRITE = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        uint driverType,
        IntPtr software,
        uint flags,
        uint* pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    private IntPtr _device;
    private IntPtr _device1;
    private IntPtr _context;
    private IntPtr _stagingTex;
    private readonly IntPtr[] _primaryTextures;
    private readonly uint _width;
    private readonly uint _height;
    private readonly bool _swapRedBlue;
    private bool _disposed;

    public D3D11DirectPresenter(
        DisplayDevice displayDevice,
        uint adapterLuidLow,
        int adapterLuidHigh,
        uint width,
        uint height,
        uint dxgiFormat,
        DisplaySurface[] primarySurfaces)
    {
        _width = width;
        _height = height;
        // Source capture bitmap is BGRA (87/91); swap R/B when the panel scanout format is RGBA8 UNORM (28) or RGBA8 sRGB (29).
        _swapRedBlue = dxgiFormat is 28 or 29;
        uint effectiveFormat = dxgiFormat != 0 ? dxgiFormat : DXGI_FORMAT_B8G8R8A8_UNORM;
        _primaryTextures = new IntPtr[primarySurfaces.Length];

        IntPtr adapterPtr = IntPtr.Zero;
        IntPtr displayDeviceRaw = IntPtr.Zero;
        IntPtr displayInterop = IntPtr.Zero;

        try
        {
            adapterPtr = FindDxgiAdapterByLuid(adapterLuidLow, adapterLuidHigh);
            if (adapterPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"未找到 LUID=0x{(uint)adapterLuidHigh:X8}_{adapterLuidLow:X8} 对应的 DXGI 显卡适配器。");
            }

            uint* levels = stackalloc uint[] { 0xb100, 0xb000, 0xa100 }; // 11_1, 11_0, 10_1
            int hr = D3D11CreateDevice(
                adapterPtr,
                D3D_DRIVER_TYPE_UNKNOWN,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels,
                3,
                D3D11_SDK_VERSION,
                out _device,
                out _,
                out _context);
            if (hr < 0 || _device == IntPtr.Zero || _context == IntPtr.Zero)
            {
                throw new COMException($"D3D11CreateDevice 失败 (HR=0x{hr:X8})", hr);
            }

            // QueryInterface ID3D11Device1
            var iidDev1 = IID_ID3D11Device1;
            hr = Marshal.QueryInterface(_device, in iidDev1, out _device1);
            if (hr < 0 || _device1 == IntPtr.Zero)
            {
                throw new COMException($"QueryInterface(ID3D11Device1) 失败 (HR=0x{hr:X8})", hr);
            }

            // Create CPU-writable staging texture (works reliably on Intel/AMD/NVIDIA/ARM64 drivers)
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = effectiveFormat,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_WRITE,
                MiscFlags = 0
            };
            var devVtbl = *(IntPtr**)_device;
            var createTex2D = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, out IntPtr, int>)devVtbl[5];
            hr = createTex2D(_device, &desc, IntPtr.Zero, out _stagingTex);
            if (hr < 0 || _stagingTex == IntPtr.Zero)
            {
                throw new COMException($"创建 D3D11 暂存纹理失败 (HR=0x{hr:X8})", hr);
            }

            // Query IDisplayDeviceInterop from WinRT DisplayDevice
            displayDeviceRaw = MarshalInspectable<DisplayDevice>.FromManaged(displayDevice);
            var iidInterop = IID_IDisplayDeviceInterop;
            hr = Marshal.QueryInterface(displayDeviceRaw, in iidInterop, out displayInterop);
            if (hr < 0 || displayInterop == IntPtr.Zero)
            {
                throw new COMException($"QueryInterface(IDisplayDeviceInterop) 失败 (HR=0x{hr:X8})", hr);
            }

            // IDisplayDeviceInteropVtbl[3] = CreateSharedHandle
            var interopVtbl = *(IntPtr**)displayInterop;
            var createSharedHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, IntPtr, out IntPtr, int>)interopVtbl[3];

            // ID3D11Device1Vtbl[48] = OpenSharedResource1
            var dev1Vtbl = *(IntPtr**)_device1;
            var openSharedRes1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, in Guid, out IntPtr, int>)dev1Vtbl[48];

            for (int i = 0; i < primarySurfaces.Length; i++)
            {
                IntPtr surfaceRaw = MarshalInspectable<DisplaySurface>.FromManaged(primarySurfaces[i]);
                IntPtr sharedNtHandle = IntPtr.Zero;
                try
                {
                    hr = createSharedHandle(displayInterop, surfaceRaw, IntPtr.Zero, GENERIC_ALL, IntPtr.Zero, out sharedNtHandle);
                    if (hr < 0 || sharedNtHandle == IntPtr.Zero)
                    {
                        throw new COMException($"CreateSharedHandle(DisplaySurface[{i}]) 失败 (HR=0x{hr:X8})", hr);
                    }

                    hr = openSharedRes1(_device1, sharedNtHandle, in IID_ID3D11Texture2D, out _primaryTextures[i]);
                    if (hr < 0 || _primaryTextures[i] == IntPtr.Zero)
                    {
                        throw new COMException($"OpenSharedResource1(DisplaySurface[{i}]) 失败 (HR=0x{hr:X8})", hr);
                    }
                }
                finally
                {
                    if (sharedNtHandle != IntPtr.Zero) Win32Interop.CloseHandle(sharedNtHandle);
                    if (surfaceRaw != IntPtr.Zero) Marshal.Release(surfaceRaw);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (displayInterop != IntPtr.Zero) Marshal.Release(displayInterop);
            if (displayDeviceRaw != IntPtr.Zero) Marshal.Release(displayDeviceRaw);
            if (adapterPtr != IntPtr.Zero) ReleaseCom(adapterPtr);
        }
    }

    /// <summary>
    /// Uploads a BGRA32 frame without rotation. Returns false on invalid input or D3D11 map failure.
    /// </summary>
    public bool UploadBgraFrame(IntPtr srcPixels, int srcStride, int srcWidth, int srcHeight, int bufferIndex) =>
        UploadBgraFrame(srcPixels, srcStride, srcWidth, srcHeight, bufferIndex, 0);

    /// <summary>
    /// Uploads a BGRA32 frame, rotating it clockwise by <paramref name="rotationDegrees"/> (0/90/180/270).
    /// For 90/270 the source frame is expected to be portrait (srcWidth == panel height).
    /// Returns false if disposed, arguments are invalid, or D3D11 staging Map fails.
    /// </summary>
    public bool UploadBgraFrame(IntPtr srcPixels, int srcStride, int srcWidth, int srcHeight, int bufferIndex, int rotationDegrees)
    {
        if (_disposed || bufferIndex < 0 || bufferIndex >= _primaryTextures.Length) return false;
        if (srcPixels == IntPtr.Zero || srcStride <= 0 || srcWidth <= 0 || srcHeight <= 0) return false;

        IntPtr dstPrimary = _primaryTextures[bufferIndex];
        if (dstPrimary == IntPtr.Zero || _stagingTex == IntPtr.Zero || _context == IntPtr.Zero) return false;

        var ctxVtbl = *(IntPtr**)_context;
        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, out D3D11_MAPPED_SUBRESOURCE, int>)ctxVtbl[14];
        var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)ctxVtbl[15];
        var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)ctxVtbl[47];
        var flush = (delegate* unmanaged[Stdcall]<IntPtr, void>)ctxVtbl[111];

        int hr = map(_context, _stagingTex, 0, D3D11_MAP_WRITE, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
        if (hr < 0 || mapped.pData == IntPtr.Zero || mapped.RowPitch == 0) return false;

        int rot = ((rotationDegrees % 360) + 360) % 360;
        int dw = (int)_width;
        int dh = (int)_height;
        int dstPitch = (int)mapped.RowPitch;
        byte* srcBase = (byte*)srcPixels;
        byte* dstBase = (byte*)mapped.pData;

        try
        {
            switch (rot)
            {
                case 0:
                    CopyRot0(srcBase, srcStride, srcWidth, srcHeight, dstBase, dstPitch, dw, dh, _swapRedBlue);
                    break;
                case 90:
                    CopyRot90(srcBase, srcStride, srcWidth, srcHeight, dstBase, dstPitch, dw, dh, _swapRedBlue);
                    break;
                case 180:
                    CopyRot180(srcBase, srcStride, srcWidth, srcHeight, dstBase, dstPitch, dw, dh, _swapRedBlue);
                    break;
                default:
                    CopyRot270(srcBase, srcStride, srcWidth, srcHeight, dstBase, dstPitch, dw, dh, _swapRedBlue);
                    break;
            }
        }
        finally
        {
            unmap(_context, _stagingTex, 0);
        }

        copyResource(_context, dstPrimary, _stagingTex);
        flush(_context);
        return true;
    }

    private static void CopyRot0(
        byte* srcBase, int srcStride, int srcWidth, int srcHeight,
        byte* dstBase, int dstPitch, int dw, int dh, bool swap)
    {
        int maxSrcW = Math.Min(srcWidth, srcStride / 4);
        int maxDstW = Math.Min(dw, dstPitch / 4);
        int copyW = Math.Min(maxDstW, maxSrcW);
        int copyH = Math.Min(dh, srcHeight);
        int copyBytes = copyW * 4;
        int dstRowBytes = maxDstW * 4;
        int trailingBytes = dstRowBytes - copyBytes;

        if (!swap)
        {
            if (copyW == maxDstW && copyH == dh && copyBytes == srcStride && copyBytes == dstPitch)
            {
                Buffer.MemoryCopy(srcBase, dstBase, (long)dstPitch * dh, (long)copyBytes * copyH);
                return;
            }

            for (int y = 0; y < copyH; y++)
            {
                byte* dRow = dstBase + (long)y * dstPitch;
                if (copyBytes > 0)
                {
                    Buffer.MemoryCopy(srcBase + (long)y * srcStride, dRow, dstPitch, copyBytes);
                }
                if (trailingBytes > 0)
                {
                    NativeMemory.Clear(dRow + copyBytes, (nuint)trailingBytes);
                }
            }
        }
        else
        {
            bool useSimd = Vector128.IsHardwareAccelerated && copyW >= 4;
            Vector128<byte> shuffleMask = Vector128.Create(
                (byte)2, 1, 0, 3,
                6, 5, 4, 7,
                10, 9, 8, 11,
                14, 13, 12, 15);
            Vector128<byte> alphaMask = Vector128.Create(
                (byte)0, 0, 0, 255,
                0, 0, 0, 255,
                0, 0, 0, 255,
                0, 0, 0, 255);

            for (int y = 0; y < copyH; y++)
            {
                byte* sRow = srcBase + (long)y * srcStride;
                byte* dRow = dstBase + (long)y * dstPitch;
                int x = 0;

                if (useSimd)
                {
                    int vecEnd = copyW - 3;
                    for (; x < vecEnd; x += 4)
                    {
                        int byteOff = x * 4;
                        Vector128<byte> v = Vector128.Load(sRow + byteOff);
                        Vector128<byte> swapped = Vector128.Shuffle(v, shuffleMask) | alphaMask;
                        swapped.Store(dRow + byteOff);
                    }
                }

                uint* sU32 = (uint*)sRow;
                uint* dU32 = (uint*)dRow;
                for (; x < copyW; x++)
                {
                    dU32[x] = SwapRedBlueOpaque(sU32[x]);
                }

                if (trailingBytes > 0)
                {
                    NativeMemory.Clear(dRow + copyBytes, (nuint)trailingBytes);
                }
            }
        }

        // Zero-fill any uncovered trailing rows when srcHeight < dh
        if (copyH < dh && dstRowBytes > 0)
        {
            if (dstRowBytes == dstPitch)
            {
                NativeMemory.Clear(dstBase + (long)copyH * dstPitch, (nuint)((long)(dh - copyH) * dstPitch));
            }
            else
            {
                for (int y = copyH; y < dh; y++)
                {
                    NativeMemory.Clear(dstBase + (long)y * dstPitch, (nuint)dstRowBytes);
                }
            }
        }
    }

    private static void CopyRot180(
        byte* srcBase, int srcStride, int srcWidth, int srcHeight,
        byte* dstBase, int dstPitch, int dw, int dh, bool swap)
    {
        // Formula: sx = srcWidth - 1 - dx, sy = srcHeight - 1 - dy
        int maxSrcW = Math.Min(srcWidth, srcStride / 4);
        int maxDstW = Math.Min(dw, dstPitch / 4);
        int dxMin = Math.Max(0, srcWidth - maxSrcW);
        int dxMax = Math.Min(maxDstW, srcWidth);
        int dyMax = Math.Min(dh, srcHeight);

        if (dxMin > 0 || dxMax < maxDstW || dyMax < dh)
        {
            ClearDestination(dstBase, dstPitch, maxDstW, dh);
        }

        if (dxMin >= dxMax || dyMax <= 0) return;

        int sxStart = srcWidth - 1 - dxMin;

        if (!swap)
        {
            for (int dy = 0; dy < dyMax; dy++)
            {
                uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                uint* sRow = (uint*)(srcBase + (long)(srcHeight - 1 - dy) * srcStride);
                int sx = sxStart;
                for (int dx = dxMin; dx < dxMax; dx++)
                {
                    dRow[dx] = sRow[sx--];
                }
            }
        }
        else
        {
            for (int dy = 0; dy < dyMax; dy++)
            {
                uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                uint* sRow = (uint*)(srcBase + (long)(srcHeight - 1 - dy) * srcStride);
                int sx = sxStart;
                for (int dx = dxMin; dx < dxMax; dx++)
                {
                    dRow[dx] = SwapRedBlueOpaque(sRow[sx--]);
                }
            }
        }
    }

    private static void CopyRot90(
        byte* srcBase, int srcStride, int srcWidth, int srcHeight,
        byte* dstBase, int dstPitch, int dw, int dh, bool swap)
    {
        // Clockwise 90 formula: sx = dy, sy = srcHeight - 1 - dx
        const int Tile = 32;
        int maxSrcW = Math.Min(srcWidth, srcStride / 4);
        int maxDstW = Math.Min(dw, dstPitch / 4);
        int dyMax = Math.Min(dh, maxSrcW);
        int dxMax = Math.Min(maxDstW, srcHeight);

        if (dyMax < dh || dxMax < maxDstW)
        {
            ClearDestination(dstBase, dstPitch, maxDstW, dh);
        }

        if (dyMax <= 0 || dxMax <= 0) return;

        if (!swap)
        {
            for (int ty = 0; ty < dyMax; ty += Tile)
            {
                int tyEnd = Math.Min(ty + Tile, dyMax);
                for (int tx = 0; tx < dxMax; tx += Tile)
                {
                    int txEnd = Math.Min(tx + Tile, dxMax);
                    long baseSrcRowOffset = (long)(srcHeight - 1 - tx) * srcStride;

                    for (int dy = ty; dy < tyEnd; dy++)
                    {
                        uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                        byte* sPtr = srcBase + baseSrcRowOffset + (long)dy * 4;
                        for (int dx = tx; dx < txEnd; dx++)
                        {
                            dRow[dx] = *(uint*)sPtr;
                            sPtr -= srcStride;
                        }
                    }
                }
            }
        }
        else
        {
            for (int ty = 0; ty < dyMax; ty += Tile)
            {
                int tyEnd = Math.Min(ty + Tile, dyMax);
                for (int tx = 0; tx < dxMax; tx += Tile)
                {
                    int txEnd = Math.Min(tx + Tile, dxMax);
                    long baseSrcRowOffset = (long)(srcHeight - 1 - tx) * srcStride;

                    for (int dy = ty; dy < tyEnd; dy++)
                    {
                        uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                        byte* sPtr = srcBase + baseSrcRowOffset + (long)dy * 4;
                        for (int dx = tx; dx < txEnd; dx++)
                        {
                            dRow[dx] = SwapRedBlueOpaque(*(uint*)sPtr);
                            sPtr -= srcStride;
                        }
                    }
                }
            }
        }
    }

    private static void CopyRot270(
        byte* srcBase, int srcStride, int srcWidth, int srcHeight,
        byte* dstBase, int dstPitch, int dw, int dh, bool swap)
    {
        // Clockwise 270 formula: sx = srcWidth - 1 - dy, sy = dx
        const int Tile = 32;
        int maxSrcW = Math.Min(srcWidth, srcStride / 4);
        int maxDstW = Math.Min(dw, dstPitch / 4);
        int dyMin = Math.Max(0, srcWidth - maxSrcW);
        int dyMax = Math.Min(dh, srcWidth);
        int dxMax = Math.Min(maxDstW, srcHeight);

        if (dyMin > 0 || dyMax < dh || dxMax < maxDstW)
        {
            ClearDestination(dstBase, dstPitch, maxDstW, dh);
        }

        if (dyMin >= dyMax || dxMax <= 0) return;

        if (!swap)
        {
            for (int ty = dyMin; ty < dyMax; ty += Tile)
            {
                int tyEnd = Math.Min(ty + Tile, dyMax);
                for (int tx = 0; tx < dxMax; tx += Tile)
                {
                    int txEnd = Math.Min(tx + Tile, dxMax);
                    long baseSrcRowOffset = (long)tx * srcStride;

                    for (int dy = ty; dy < tyEnd; dy++)
                    {
                        uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                        byte* sPtr = srcBase + baseSrcRowOffset + (long)(srcWidth - 1 - dy) * 4;
                        for (int dx = tx; dx < txEnd; dx++)
                        {
                            dRow[dx] = *(uint*)sPtr;
                            sPtr += srcStride;
                        }
                    }
                }
            }
        }
        else
        {
            for (int ty = dyMin; ty < dyMax; ty += Tile)
            {
                int tyEnd = Math.Min(ty + Tile, dyMax);
                for (int tx = 0; tx < dxMax; tx += Tile)
                {
                    int txEnd = Math.Min(tx + Tile, dxMax);
                    long baseSrcRowOffset = (long)tx * srcStride;

                    for (int dy = ty; dy < tyEnd; dy++)
                    {
                        uint* dRow = (uint*)(dstBase + (long)dy * dstPitch);
                        byte* sPtr = srcBase + baseSrcRowOffset + (long)(srcWidth - 1 - dy) * 4;
                        for (int dx = tx; dx < txEnd; dx++)
                        {
                            dRow[dx] = SwapRedBlueOpaque(*(uint*)sPtr);
                            sPtr += srcStride;
                        }
                    }
                }
            }
        }
    }

    private static void ClearDestination(byte* dstBase, int dstPitch, int maxDstW, int dh)
    {
        int rowBytes = maxDstW * 4;
        if (rowBytes <= 0 || dh <= 0) return;
        if (rowBytes == dstPitch)
        {
            NativeMemory.Clear(dstBase, (nuint)((long)dstPitch * dh));
        }
        else
        {
            for (int y = 0; y < dh; y++)
            {
                NativeMemory.Clear(dstBase + (long)y * dstPitch, (nuint)rowBytes);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SwapRedBlueOpaque(uint px) =>
        0xFF000000u | ((px & 0x000000FFu) << 16) | (px & 0x0000FF00u) | ((px >> 16) & 0x000000FFu);

    private static IntPtr FindDxgiAdapterByLuid(uint luidLow, int luidHigh)
    {
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            int hr = CreateDXGIFactory1(in IID_IDXGIFactory1, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero) return IntPtr.Zero;

            var factoryVtbl = *(IntPtr**)factoryPtr;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)factoryVtbl[12];

            for (uint i = 0; i < 32; i++)
            {
                if (enumAdapters1(factoryPtr, i, out IntPtr adapterPtr) < 0 || adapterPtr == IntPtr.Zero)
                    break;

                var adapterVtbl = *(IntPtr**)adapterPtr;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out DXGI_ADAPTER_DESC1, int>)adapterVtbl[10];
                if (getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc) >= 0 &&
                    desc.AdapterLuid.LowPart == luidLow &&
                    desc.AdapterLuid.HighPart == luidHigh)
                {
                    return adapterPtr; // caller owns reference
                }

                ReleaseCom(adapterPtr);
            }
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) ReleaseCom(factoryPtr);
        }
        return IntPtr.Zero;
    }

    private static void ReleaseCom(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try
        {
            var vtbl = *(IntPtr**)ptr;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            release(ptr);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _primaryTextures.Length; i++)
        {
            ReleaseCom(_primaryTextures[i]);
            _primaryTextures[i] = IntPtr.Zero;
        }
        ReleaseCom(_stagingTex); _stagingTex = IntPtr.Zero;
        ReleaseCom(_context); _context = IntPtr.Zero;
        ReleaseCom(_device1); _device1 = IntPtr.Zero;
        ReleaseCom(_device); _device = IntPtr.Zero;
    }
}
