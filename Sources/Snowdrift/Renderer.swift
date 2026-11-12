import CoreGraphics
import Foundation
import Metal

/// One vertex: position in points (top-left origin), atlas coordinate, premultiplied colour.
struct Vertex {
    var x: Float, y: Float, u: Float, v: Float
    var r: Float, g: Float, b: Float, a: Float
}

/// A frame's worth of triangles, in painter's order.
final class Batch {
    var verts = ContiguousArray<Vertex>()
    let uv: [UVRect]
    private let whiteU: Float
    private let whiteV: Float

    init(uv: [UVRect]) {
        self.uv = uv
        whiteU = uv[Sprite.white.rawValue].u0
        whiteV = uv[Sprite.white.rawValue].v0
        verts.reserveCapacity(60_000)
    }

    func clear() { verts.removeAll(keepingCapacity: true) }

    /// Textured quad. Colour is straight alpha; `additive` makes it glow instead of cover.
    @inline(__always)
    func sprite(_ s: Sprite, x: Float, y: Float, hw: Float, hh: Float, rot: Float = 0,
                r: Float = 1, g: Float = 1, b: Float = 1, a: Float = 1, additive: Bool = false, flipX: Bool = false,
                flipY: Bool = false) {
        let t = uv[s.rawValue]
        let u0 = flipX ? t.u1 : t.u0, u1 = flipX ? t.u0 : t.u1
        let tv0 = flipY ? t.v1 : t.v0, tv1 = flipY ? t.v0 : t.v1
        let pr = r * a, pg = g * a, pb = b * a, pa: Float = additive ? a * 0.45 : a
        var ax = hw, ay: Float = 0, bx: Float = 0, by = hh
        if rot != 0 {
            let c = cos(rot), sn = sin(rot)
            ax = hw * c; ay = hw * sn
            bx = -hh * sn; by = hh * c
        }
        let v0 = Vertex(x: x - ax - bx, y: y - ay - by, u: u0, v: tv0, r: pr, g: pg, b: pb, a: pa)
        let v1 = Vertex(x: x + ax - bx, y: y + ay - by, u: u1, v: tv0, r: pr, g: pg, b: pb, a: pa)
        let v2 = Vertex(x: x - ax + bx, y: y - ay + by, u: u0, v: tv1, r: pr, g: pg, b: pb, a: pa)
        let v3 = Vertex(x: x + ax + bx, y: y + ay + by, u: u1, v: tv1, r: pr, g: pg, b: pb, a: pa)
        verts.append(v0); verts.append(v1); verts.append(v2)
        verts.append(v2); verts.append(v1); verts.append(v3)
    }

    /// Flat-coloured vertex (premultiplied colour).
    @inline(__always)
    func flat(_ x: Float, _ y: Float, _ r: Float, _ g: Float, _ b: Float, _ a: Float) -> Vertex {
        Vertex(x: x, y: y, u: whiteU, v: whiteV, r: r * a, g: g * a, b: b * a, a: a)
    }

    @inline(__always)
    func quad(_ a: Vertex, _ b: Vertex, _ c: Vertex, _ d: Vertex) {
        verts.append(a); verts.append(b); verts.append(c)
        verts.append(c); verts.append(b); verts.append(d)
    }

    @inline(__always)
    func tri(_ a: Vertex, _ b: Vertex, _ c: Vertex) {
        verts.append(a); verts.append(b); verts.append(c)
    }

    func rect(x: Float, y: Float, w: Float, h: Float, r: Float, g: Float, b: Float, a: Float) {
        quad(flat(x, y, r, g, b, a), flat(x + w, y, r, g, b, a), flat(x, y + h, r, g, b, a), flat(x + w, y + h, r, g, b, a))
    }

    /// Thick line segment.
    func line(_ x0: Float, _ y0: Float, _ x1: Float, _ y1: Float, width: Float, r: Float, g: Float, b: Float, a: Float) {
        let dx = x1 - x0, dy = y1 - y0
        let len = max(0.0001, (dx * dx + dy * dy).squareRoot())
        let nx = -dy / len * width / 2, ny = dx / len * width / 2
        quad(flat(x0 + nx, y0 + ny, r, g, b, a), flat(x1 + nx, y1 + ny, r, g, b, a),
             flat(x0 - nx, y0 - ny, r, g, b, a), flat(x1 - nx, y1 - ny, r, g, b, a))
    }
}

/// Per-view vertex buffers, reused between frames.
final class FrameBuffers {
    fileprivate var buffers: [MTLBuffer?] = [nil, nil, nil]
    fileprivate var index = 0
    fileprivate let semaphore = DispatchSemaphore(value: 3)
}

final class Renderer {
    let device: MTLDevice
    let queue: MTLCommandQueue
    let atlas: Atlas
    private let pipeline: MTLRenderPipelineState
    private let sampler: MTLSamplerState

    static let pixelFormat: MTLPixelFormat = .bgra8Unorm

    private static let shaderSource = """
    #include <metal_stdlib>
    using namespace metal;

    struct VIn {
        packed_float2 pos;
        packed_float2 uv;
        packed_float4 color;
    };

    struct VOut {
        float4 position [[position]];
        float2 uv;
        float4 color;
    };

    vertex VOut vmain(uint vid [[vertex_id]],
                      const device VIn *verts [[buffer(0)]],
                      constant float2 &viewport [[buffer(1)]]) {
        VIn v = verts[vid];
        float2 p = float2(v.pos) / viewport;
        VOut o;
        o.position = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
        o.uv = float2(v.uv);
        o.color = float4(v.color);
        return o;
    }

    fragment float4 fmain(VOut in [[stage_in]],
                          texture2d<float> tex [[texture(0)]],
                          sampler smp [[sampler(0)]],
                          constant float &fade [[buffer(0)]]) {
        return tex.sample(smp, in.uv) * in.color * fade;
    }
    """

    init?(device: MTLDevice) {
        guard let queue = device.makeCommandQueue(), let atlas = Atlas(device: device, queue: queue) else { return nil }
        self.device = device
        self.queue = queue
        self.atlas = atlas
        do {
            let lib = try device.makeLibrary(source: Renderer.shaderSource, options: nil)
            let desc = MTLRenderPipelineDescriptor()
            desc.vertexFunction = lib.makeFunction(name: "vmain")
            desc.fragmentFunction = lib.makeFunction(name: "fmain")
            let att = desc.colorAttachments[0]!
            att.pixelFormat = Renderer.pixelFormat
            att.isBlendingEnabled = true
            att.rgbBlendOperation = .add
            att.alphaBlendOperation = .add
            att.sourceRGBBlendFactor = .one
            att.sourceAlphaBlendFactor = .one
            att.destinationRGBBlendFactor = .oneMinusSourceAlpha
            att.destinationAlphaBlendFactor = .oneMinusSourceAlpha
            pipeline = try device.makeRenderPipelineState(descriptor: desc)
        } catch {
            NSLog("Snowdrift: shader setup failed: \(error)")
            return nil
        }
        let sd = MTLSamplerDescriptor()
        sd.minFilter = .linear
        sd.magFilter = .linear
        sd.mipFilter = .linear
        sd.sAddressMode = .clampToEdge
        sd.tAddressMode = .clampToEdge
        guard let sampler = device.makeSamplerState(descriptor: sd) else { return nil }
        self.sampler = sampler
    }

    func makeBatch() -> Batch { Batch(uv: atlas.uv) }

    /// Draws the batch into `pass`. `viewport` is the view size in points.
    func draw(batch: Batch, buffers: FrameBuffers, pass: MTLRenderPassDescriptor, viewport: SIMD2<Float>,
              present: MTLDrawable?, wait: Bool = false, fade: Float = 1) {
        buffers.semaphore.wait()
        guard let cb = queue.makeCommandBuffer() else {
            buffers.semaphore.signal()
            return
        }
        let sem = buffers.semaphore
        cb.addCompletedHandler { _ in sem.signal() }

        let count = batch.verts.count
        var vbuf: MTLBuffer?
        if count > 0 {
            let needed = count * MemoryLayout<Vertex>.stride
            buffers.index = (buffers.index + 1) % buffers.buffers.count
            if (buffers.buffers[buffers.index]?.length ?? 0) < needed {
                buffers.buffers[buffers.index] = device.makeBuffer(length: max(needed * 3 / 2, 1 << 18), options: .storageModeShared)
            }
            vbuf = buffers.buffers[buffers.index]
            if let vbuf {
                batch.verts.withUnsafeBufferPointer { p in
                    vbuf.contents().copyMemory(from: p.baseAddress!, byteCount: needed)
                }
            }
        }

        if let enc = cb.makeRenderCommandEncoder(descriptor: pass) {
            if let vbuf, count > 0 {
                var vp = viewport
                enc.setRenderPipelineState(pipeline)
                enc.setVertexBuffer(vbuf, offset: 0, index: 0)
                enc.setVertexBytes(&vp, length: MemoryLayout<SIMD2<Float>>.size, index: 1)
                enc.setFragmentTexture(atlas.texture, index: 0)
                enc.setFragmentSamplerState(sampler, index: 0)
                var f = fade
                enc.setFragmentBytes(&f, length: MemoryLayout<Float>.size, index: 0)
                enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: count)
            }
            enc.endEncoding()
        }
        if let present { cb.present(present) }
        cb.commit()
        if wait { cb.waitUntilCompleted() }
    }
}
