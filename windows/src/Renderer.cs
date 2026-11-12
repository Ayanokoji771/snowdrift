using System;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Snowdrift
{
    /// <summary>Draws a Batch with one Direct3D 11 pass: all sprites and drifts in a single draw call.</summary>
    public sealed class Renderer : IDisposable
    {
        public readonly ID3D11Device Device;
        public readonly ID3D11DeviceContext Context;
        public readonly IDXGIDevice DxgiDevice;
        public readonly IDXGIFactory2 Factory;
        public readonly Atlas Atlas;
        public readonly bool IsWarp;

        private readonly ID3D11VertexShader vs;
        private readonly ID3D11PixelShader ps;
        private readonly ID3D11InputLayout layout;
        private readonly ID3D11Buffer cbuffer;
        private readonly ID3D11SamplerState sampler;
        private readonly ID3D11BlendState blend;
        private readonly ID3D11RasterizerState raster;
        private readonly ID3D11Texture2D atlasTex;
        private readonly ID3D11ShaderResourceView atlasSrv;
        private ID3D11Buffer vbuffer;
        private int vbufferVerts;

        private const int Stride = 32;

        private const string ShaderSource = @"
cbuffer Params : register(b0)
{
    float2 viewport;
    float fade;
    float pad;
};

Texture2D tex : register(t0);
SamplerState smp : register(s0);

struct VIn
{
    float2 pos : POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

struct VOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

VOut vmain(VIn v)
{
    VOut o;
    float2 p = v.pos / viewport;
    o.position = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
    o.uv = v.uv;
    o.color = v.color;
    return o;
}

float4 pmain(VOut i) : SV_Target
{
    return tex.Sample(smp, i.uv) * i.color * fade;
}
";

        private Renderer(ID3D11Device device, ID3D11DeviceContext context, bool warp)
        {
            Device = device;
            Context = context;
            IsWarp = warp;
            DxgiDevice = device.QueryInterface<IDXGIDevice>();
            using (IDXGIAdapter adapter = DxgiDevice.GetAdapter())
                Factory = adapter.GetParent<IDXGIFactory2>();

            byte[] vsCode = Compile("vmain", "vs_4_0");
            byte[] psCode = Compile("pmain", "ps_4_0");
            vs = device.CreateVertexShader(vsCode);
            ps = device.CreatePixelShader(psCode);
            layout = device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
            }, vsCode);

            cbuffer = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            blend = device.CreateBlendState(BlendDescription.AlphaBlend);
            raster = device.CreateRasterizerState(RasterizerDescription.CullNone);

            Atlas = new Atlas();
            var td = new Texture2DDescription
            {
                Width = Atlas.Size,
                Height = Atlas.Size,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            };
            atlasTex = device.CreateTexture2D(td);
            unsafe
            {
                fixed (byte* p = Atlas.Pixels)
                    context.UpdateSubresource(atlasTex, 0, null, (IntPtr)p, Atlas.Size * 4, 0);
            }
            atlasSrv = device.CreateShaderResourceView(atlasTex);
            context.GenerateMips(atlasSrv);
        }

        private static byte[] Compile(string entry, string profile)
        {
            ReadOnlyMemory<byte> code = Compiler.Compile(ShaderSource, entry, "snow.hlsl", profile);
            return code.ToArray();
        }

        /// <summary>Hardware device, or the WARP software device when there is no usable GPU.</summary>
        public static Renderer Create(bool forceWarp = false)
        {
            FeatureLevel[] levels = { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            if (!forceWarp)
            {
                var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                                                 out ID3D11Device device, out ID3D11DeviceContext context);
                if (hr.Success) return new Renderer(device, context, false);
            }
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                                    out ID3D11Device wdevice, out ID3D11DeviceContext wcontext).CheckError();
            return new Renderer(wdevice, wcontext, true);
        }

        public Batch MakeBatch() => new Batch(Atlas.UV);

        private void EnsureVertexBuffer(int verts)
        {
            if (vbuffer != null && vbufferVerts >= verts) return;
            vbuffer?.Dispose();
            vbufferVerts = Math.Max(verts * 3 / 2, 1 << 15);
            vbuffer = Device.CreateBuffer(new BufferDescription((uint)(vbufferVerts * Stride), BindFlags.VertexBuffer,
                                                                ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }

        /// <summary>Clears the target and draws the batch. viewW/viewH is the size in points.</summary>
        public void Draw(Batch batch, ID3D11RenderTargetView rtv, int pxW, int pxH, float viewW, float viewH, float fade)
        {
            var c = Context;
            c.OMSetRenderTargets(rtv);
            c.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 0));
            int n = batch.Count;
            if (n == 0) return;

            EnsureVertexBuffer(n);
            MappedSubresource vm = c.Map(vbuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                fixed (Vertex* p = batch.Verts)
                    Buffer.MemoryCopy(p, (void*)vm.DataPointer, (long)vbufferVerts * Stride, (long)n * Stride);
            }
            c.Unmap(vbuffer, 0);

            MappedSubresource cm = c.Map(cbuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                float* f = (float*)cm.DataPointer;
                f[0] = viewW;
                f[1] = viewH;
                f[2] = fade;
                f[3] = 0;
            }
            c.Unmap(cbuffer, 0);

            c.RSSetViewport(0, 0, pxW, pxH);
            c.RSSetState(raster);
            c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            c.IASetInputLayout(layout);
            c.IASetVertexBuffer(0, vbuffer, (uint)Stride, 0);
            c.VSSetShader(vs);
            c.VSSetConstantBuffer(0, cbuffer);
            c.PSSetShader(ps);
            c.PSSetConstantBuffer(0, cbuffer);
            c.PSSetShaderResource(0, atlasSrv);
            c.PSSetSampler(0, sampler);
            c.OMSetBlendState(blend);
            c.Draw((uint)n, 0);
        }

        /// <summary>Draws a batch off screen and returns BGRA premultiplied pixels (for snapshots and tests).</summary>
        public byte[] RenderToPixels(Batch batch, int pxW, int pxH, float viewW, float viewH)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)pxW,
                Height = (uint)pxH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            };
            using var target = Device.CreateTexture2D(desc);
            using var rtv = Device.CreateRenderTargetView(target);
            Draw(batch, rtv, pxW, pxH, viewW, viewH, 1);

            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = Device.CreateTexture2D(desc);
            Context.CopyResource(staging, target);
            MappedSubresource m = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var pixels = new byte[pxW * pxH * 4];
            try
            {
                for (int y = 0; y < pxH; y++)
                    Marshal.Copy(m.DataPointer + (int)(y * m.RowPitch), pixels, y * pxW * 4, pxW * 4);
            }
            finally { Context.Unmap(staging, 0); }
            return pixels;
        }

        public void Dispose()
        {
            vbuffer?.Dispose();
            atlasSrv?.Dispose();
            atlasTex?.Dispose();
            raster?.Dispose();
            blend?.Dispose();
            sampler?.Dispose();
            cbuffer?.Dispose();
            layout?.Dispose();
            ps?.Dispose();
            vs?.Dispose();
            Factory?.Dispose();
            DxgiDevice?.Dispose();
            Context?.Dispose();
            Device?.Dispose();
        }
    }
}
