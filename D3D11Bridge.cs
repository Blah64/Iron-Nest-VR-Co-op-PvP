using System;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using UnityEngine;
using UnityEngine.Rendering;

namespace IronNestVR
{
    /// <summary>
    /// Obtains the live Direct3D 11 device/context that Unity is rendering with (without recompiling
    /// the game) by reading the native pointer behind a throwaway RenderTexture, and exposes the
    /// adapter LUID for the OpenXR graphics-requirements gate plus a GPU texture copy used to feed
    /// OpenXR swapchains.
    ///
    /// Requires the game to run on D3D11 (launch with <c>-force-d3d11</c>); D3D12 would need Unity's
    /// command queue, which isn't reachable from an injected plugin.
    /// </summary>
    internal sealed unsafe class D3D11Bridge : IDisposable
    {
        public ID3D11Device* Device { get; private set; }
        public ID3D11DeviceContext* Context { get; private set; }
        public ulong AdapterLuid { get; private set; }

        private RenderTexture _scratch;

        public bool TryInit(out string error)
        {
            error = null;

            var api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D11)
            {
                error = $"graphics API is {api}; need Direct3D11. Launch the game with -force-d3d11.";
                return false;
            }

            // A tiny RT whose native handle is an ID3D11Texture2D created on Unity's device.
            _scratch = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32);
            _scratch.Create();
            var ptr = _scratch.GetNativeTexturePtr();
            if (ptr == IntPtr.Zero)
            {
                error = "RenderTexture native handle not ready yet (will retry).";
                return false;
            }

            var tex = (ID3D11Texture2D*)ptr;
            ID3D11Device* dev = null;
            tex->GetDevice(&dev);
            if (dev == null) { error = "ID3D11Texture2D::GetDevice returned null."; return false; }
            Device = dev;

            ID3D11DeviceContext* ctx = null;
            dev->GetImmediateContext(&ctx);
            Context = ctx;

            AdapterLuid = QueryAdapterLuid(dev);
            return true;
        }

        private static ulong QueryAdapterLuid(ID3D11Device* dev)
        {
            ulong luid = 0;
            var iid = IDXGIDevice.Guid;
            IDXGIDevice* dxgi = null;
            int hr = dev->QueryInterface(&iid, (void**)&dxgi);
            if (hr >= 0 && dxgi != null)
            {
                IDXGIAdapter* adapter = null;
                dxgi->GetAdapter(&adapter);
                if (adapter != null)
                {
                    AdapterDesc desc = default;
                    adapter->GetDesc(&desc);
                    // LUID is 8 bytes (DWORD LowPart + LONG HighPart); OpenXR exposes it as one UInt64.
                    luid = *(ulong*)&desc.AdapterLuid;
                    adapter->Release();
                }
                dxgi->Release();
            }
            return luid;
        }

        /// <summary>Full-resource GPU copy: dst and src are ID3D11Texture2D* (as void*).</summary>
        public void CopyTexture(void* dstResource, void* srcResource)
        {
            Context->CopyResource((ID3D11Resource*)dstResource, (ID3D11Resource*)srcResource);
        }

        /// <summary>IntPtr overload so non-unsafe callers (e.g. the self-test) can copy.</summary>
        public void CopyTexture(IntPtr dstResource, IntPtr srcResource)
            => CopyTexture((void*)dstResource, (void*)srcResource);

        public void Dispose()
        {
            if (_scratch != null) { UnityEngine.Object.Destroy(_scratch); _scratch = null; }
            // Device/Context are released when the process exits; we intentionally don't Release the
            // shared Unity device here.
            Device = null;
            Context = null;
        }
    }
}
