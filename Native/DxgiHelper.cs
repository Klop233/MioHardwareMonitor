using System.Runtime.InteropServices;

namespace SideScreenHUD.Native;

internal sealed class DxgiAdapterInfo
{
    public string Name { get; set; } = "";
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public ulong DedicatedVideoMemoryBytes { get; set; }
    public ulong SharedSystemMemoryBytes { get; set; }
    public string LuidKey { get; set; } = "";
    public bool IsSoftware { get; set; }

    public double DedicatedVideoMemoryGb => DedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    // AMD (0x1002), NVIDIA (0x10DE), Intel discrete/integrated (0x8086)
    public bool IsLikelyDiscrete => !IsSoftware && (VendorId == 0x1002 || VendorId == 0x10DE || DedicatedVideoMemoryGb >= 2.0);
}

/// <summary>
/// Queries exact 64-bit GPU DedicatedVideoMemory and LUID via DXGI COM vtables,
/// avoiding Win32_VideoController's 4GB 32-bit overflow limit.
/// </summary>
internal static unsafe class DxgiHelper
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

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

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    public static List<DxgiAdapterInfo> EnumerateAdapters()
    {
        var result = new List<DxgiAdapterInfo>();
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            int hr = CreateDXGIFactory1(in IID_IDXGIFactory1, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero) return result;

            // IDXGIFactory1 vtable:
            // 0..2 IUnknown, 3..6 IDXGIObject, 7..11 IDXGIFactory, 12 EnumAdapters1
            var factoryVtbl = *(IntPtr**)factoryPtr;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)factoryVtbl[12];
            var releaseFactory = (delegate* unmanaged[Stdcall]<IntPtr, uint>)factoryVtbl[2];

            for (uint i = 0; i < 32; i++)
            {
                int ehr = enumAdapters1(factoryPtr, i, out IntPtr adapterPtr);
                if (ehr < 0 || adapterPtr == IntPtr.Zero) break;

                try
                {
                    // IDXGIAdapter1 vtable:
                    // 0..2 IUnknown, 3..6 IDXGIObject, 7..9 IDXGIAdapter, 10 GetDesc1
                    var adapterVtbl = *(IntPtr**)adapterPtr;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out DXGI_ADAPTER_DESC1, int>)adapterVtbl[10];

                    if (getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc) >= 0)
                    {
                        string name = new string(desc.Description).TrimEnd('\0').Trim();
                        bool isSoftware = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                            || name.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase);

                        string luidKey = $"luid_0x{(uint)desc.AdapterLuid.HighPart:x8}_0x{desc.AdapterLuid.LowPart:x8}";

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(new DxgiAdapterInfo
                            {
                                Name = name,
                                VendorId = desc.VendorId,
                                DeviceId = desc.DeviceId,
                                DedicatedVideoMemoryBytes = desc.DedicatedVideoMemory,
                                SharedSystemMemoryBytes = desc.SharedSystemMemory,
                                LuidKey = luidKey,
                                IsSoftware = isSoftware
                            });
                        }
                    }
                }
                finally
                {
                    var adapterVtbl = *(IntPtr**)adapterPtr;
                    var releaseAdapter = (delegate* unmanaged[Stdcall]<IntPtr, uint>)adapterVtbl[2];
                    releaseAdapter(adapterPtr);
                }
            }

            releaseFactory(factoryPtr);
            factoryPtr = IntPtr.Zero;
        }
        catch
        {
            if (factoryPtr != IntPtr.Zero)
            {
                try
                {
                    var factoryVtbl = *(IntPtr**)factoryPtr;
                    var releaseFactory = (delegate* unmanaged[Stdcall]<IntPtr, uint>)factoryVtbl[2];
                    releaseFactory(factoryPtr);
                }
                catch { }
            }
        }

        // Put discrete GPUs with largest VRAM first
        return result
            .Where(a => !a.IsSoftware)
            .OrderByDescending(a => a.IsLikelyDiscrete)
            .ThenByDescending(a => a.DedicatedVideoMemoryBytes)
            .ToList();
    }
}
