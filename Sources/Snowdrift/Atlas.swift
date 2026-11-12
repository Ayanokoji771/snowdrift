import CoreGraphics
import Foundation
import Metal

/// Every picture the app draws. All of them are painted in code at launch; the app ships no image files.
enum Sprite: Int, CaseIterable {
    case white, dot, crystal1, crystal2, crystal3
    case ballBase, ballTex, glow, bulb
    case coal, carrot, twig, bucket, topHat, scarf
    case frost
}

struct UVRect {
    var u0: Float, v0: Float, u1: Float, v1: Float
}

final class Atlas {
    static let size = 1024
    let uv: [UVRect]
    let texture: MTLTexture

    init?(device: MTLDevice, queue: MTLCommandQueue) {
        let n = Atlas.size
        guard let ctx = CGContext(data: nil, width: n, height: n, bitsPerComponent: 8, bytesPerRow: n * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        // Top-left origin, y down, so memory rows match texture rows.
        ctx.translateBy(x: 0, y: CGFloat(n))
        ctx.scaleBy(x: 1, y: -1)

        var table = Array(repeating: UVRect(u0: 0, v0: 0, u1: 0, v1: 0), count: Sprite.allCases.count)
        var cursorX = 4, cursorY = 4, rowHeight = 0
        func place(_ sprite: Sprite, _ w: Int, _ h: Int, _ draw: (CGContext, CGFloat, CGFloat) -> Void) {
            if cursorX + w + 4 > n {
                cursorX = 4
                cursorY += rowHeight + 8
                rowHeight = 0
            }
            ctx.saveGState()
            ctx.translateBy(x: CGFloat(cursorX), y: CGFloat(cursorY))
            ctx.clip(to: CGRect(x: 0, y: 0, width: w, height: h))
            draw(ctx, CGFloat(w), CGFloat(h))
            ctx.restoreGState()
            let inset: Float = 0.5
            table[sprite.rawValue] = UVRect(u0: (Float(cursorX) + inset) / Float(n), v0: (Float(cursorY) + inset) / Float(n),
                                         u1: (Float(cursorX + w) - inset) / Float(n), v1: (Float(cursorY + h) - inset) / Float(n))
            cursorX += w + 8
            rowHeight = max(rowHeight, h)
        }

        place(.white, 16, 16) { c, w, h in
            c.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
            c.fill(CGRect(x: 0, y: 0, width: w, height: h))
        }
        // Keep samples of the solid white sprite well inside it.
        do {
            let r = table[Sprite.white.rawValue]
            let mu = (r.u0 + r.u1) / 2, mv = (r.v0 + r.v1) / 2
            table[Sprite.white.rawValue] = UVRect(u0: mu, v0: mv, u1: mu, v1: mv)
        }
        place(.dot, 64, 64) { c, w, h in Atlas.drawDot(c, w, h) }
        place(.crystal1, 128, 128) { c, w, h in Atlas.drawCrystal(c, w, h, variant: 0) }
        place(.crystal2, 128, 128) { c, w, h in Atlas.drawCrystal(c, w, h, variant: 1) }
        place(.crystal3, 128, 128) { c, w, h in Atlas.drawCrystal(c, w, h, variant: 2) }
        place(.glow, 128, 128) { c, w, h in Atlas.drawGlow(c, w, h) }
        place(.bulb, 64, 96) { c, w, h in Atlas.drawBulb(c, w, h) }
        place(.coal, 48, 48) { c, w, h in Atlas.drawCoal(c, w, h) }
        place(.ballBase, 256, 256) { c, w, h in Atlas.drawBallBase(c, w, h) }
        place(.ballTex, 256, 256) { c, w, h in Atlas.drawBallTex(c, w, h) }
        place(.carrot, 160, 64) { c, w, h in Atlas.drawCarrot(c, w, h) }
        place(.twig, 192, 96) { c, w, h in Atlas.drawTwig(c, w, h) }
        place(.bucket, 160, 160) { c, w, h in Atlas.drawBucket(c, w, h) }
        place(.topHat, 160, 160) { c, w, h in Atlas.drawTopHat(c, w, h) }
        place(.scarf, 224, 128) { c, w, h in Atlas.drawScarf(c, w, h) }
        place(.frost, 256, 256) { c, w, h in Atlas.drawFrost(c, w, h) }

        let desc = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: n, height: n, mipmapped: true)
        desc.usage = [.shaderRead]
        guard let tex = device.makeTexture(descriptor: desc), let data = ctx.data else { return nil }
        tex.replace(region: MTLRegionMake2D(0, 0, n, n), mipmapLevel: 0, withBytes: data, bytesPerRow: n * 4)
        if let cb = queue.makeCommandBuffer(), let blit = cb.makeBlitCommandEncoder() {
            blit.generateMipmaps(for: tex)
            blit.endEncoding()
            cb.commit()
        }
        texture = tex
        uv = table
    }

    // MARK: - Painting helpers

    private static let rgb = CGColorSpace(name: CGColorSpace.sRGB)!

    private static func radial(_ c: CGContext, colors: [(CGFloat, CGFloat, CGFloat, CGFloat)], stops: [CGFloat],
                               from: CGPoint, fromRadius: CGFloat = 0, to: CGPoint, radius: CGFloat) {
        let comps = colors.flatMap { [$0.0, $0.1, $0.2, $0.3] }
        guard let g = CGGradient(colorSpace: rgb, colorComponents: comps, locations: stops, count: colors.count) else { return }
        c.drawRadialGradient(g, startCenter: from, startRadius: fromRadius, endCenter: to, endRadius: radius,
                             options: [.drawsAfterEndLocation])
    }

    private static func linear(_ c: CGContext, colors: [(CGFloat, CGFloat, CGFloat, CGFloat)], stops: [CGFloat],
                               from: CGPoint, to: CGPoint) {
        let comps = colors.flatMap { [$0.0, $0.1, $0.2, $0.3] }
        guard let g = CGGradient(colorSpace: rgb, colorComponents: comps, locations: stops, count: colors.count) else { return }
        c.drawLinearGradient(g, start: from, end: to, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    }

    static func drawDot(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let p = CGPoint(x: w / 2, y: h / 2)
        radial(c, colors: [(1, 1, 1, 1), (1, 1, 1, 0.95), (1, 1, 1, 0.35), (1, 1, 1, 0)], stops: [0, 0.42, 0.7, 1],
               from: p, to: p, radius: w / 2 - 1)
    }

    static func drawGlow(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let p = CGPoint(x: w / 2, y: h / 2)
        radial(c, colors: [(1, 1, 1, 0.85), (1, 1, 1, 0.32), (1, 1, 1, 0.08), (1, 1, 1, 0)], stops: [0, 0.3, 0.65, 1],
               from: p, to: p, radius: w / 2 - 1)
    }

    /// A six-armed snow crystal. Also used for the app icon.
    static func drawCrystal(_ c: CGContext, _ w: CGFloat, _ h: CGFloat, variant: Int, lineScale: CGFloat = 1) {
        let cx = w / 2, cy = h / 2
        let radius = w / 2 * 0.86
        c.saveGState()
        c.setStrokeColor(red: 1, green: 1, blue: 1, alpha: 1)
        c.setLineCap(.round)
        c.setLineJoin(.round)
        let branches: [(CGFloat, CGFloat)]
        switch variant {
        case 0: branches = [(0.42, 0.30), (0.68, 0.22)]
        case 1: branches = [(0.34, 0.20), (0.56, 0.30), (0.80, 0.16)]
        default: branches = [(0.55, 0.36)]
        }
        for arm in 0..<6 {
            c.saveGState()
            c.translateBy(x: cx, y: cy)
            c.rotate(by: CGFloat(arm) * .pi / 3)
            c.setLineWidth(w * 0.062 * lineScale)
            c.move(to: CGPoint(x: 0, y: 0))
            c.addLine(to: CGPoint(x: 0, y: -radius))
            c.strokePath()
            c.setLineWidth(w * 0.05 * lineScale)
            for (at, len) in branches {
                let base = CGPoint(x: 0, y: -radius * at)
                for side in [-1.0, 1.0] {
                    c.move(to: base)
                    c.addLine(to: CGPoint(x: CGFloat(side) * radius * len * 0.87, y: base.y - radius * len * 0.5))
                    c.strokePath()
                }
            }
            if variant == 2 {
                c.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
                c.fillEllipse(in: CGRect(x: -w * 0.045, y: -radius - w * 0.02, width: w * 0.09, height: w * 0.09))
            }
            c.restoreGState()
        }
        c.restoreGState()
    }

    static func drawBulb(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let body = CGMutablePath()
        // Teardrop: narrow neck at the top, round belly below.
        body.move(to: CGPoint(x: w * 0.5 - w * 0.14, y: h * 0.04))
        body.addLine(to: CGPoint(x: w * 0.5 + w * 0.14, y: h * 0.04))
        body.addCurve(to: CGPoint(x: w * 0.5, y: h * 0.97),
                      control1: CGPoint(x: w * 1.02, y: h * 0.38), control2: CGPoint(x: w * 0.86, y: h * 0.97))
        body.addCurve(to: CGPoint(x: w * 0.5 - w * 0.14, y: h * 0.04),
                      control1: CGPoint(x: w * 0.14, y: h * 0.97), control2: CGPoint(x: -w * 0.02, y: h * 0.38))
        body.closeSubpath()
        c.saveGState()
        c.addPath(body)
        c.clip()
        radial(c, colors: [(1, 1, 1, 1), (0.92, 0.92, 0.92, 1), (0.66, 0.66, 0.66, 1)], stops: [0, 0.5, 1],
               from: CGPoint(x: w * 0.4, y: h * 0.5), to: CGPoint(x: w * 0.5, y: h * 0.55), radius: h * 0.55)
        c.restoreGState()
    }

    static func drawCoal(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        c.saveGState()
        c.addEllipse(in: CGRect(x: 3, y: 3, width: w - 6, height: h - 6))
        c.clip()
        radial(c, colors: [(0.36, 0.38, 0.42, 1), (0.13, 0.14, 0.17, 1), (0.05, 0.05, 0.07, 1)], stops: [0, 0.55, 1],
               from: CGPoint(x: w * 0.38, y: h * 0.34), to: CGPoint(x: w * 0.5, y: h * 0.5), radius: w * 0.5)
        c.restoreGState()
    }

    static func drawBallBase(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        c.saveGState()
        c.addEllipse(in: CGRect(x: 3, y: 3, width: w - 6, height: h - 6))
        c.clip()
        radial(c, colors: [(1, 1, 1, 1), (0.97, 0.985, 1, 1), (0.87, 0.92, 0.975, 1), (0.70, 0.79, 0.90, 1)],
               stops: [0, 0.45, 0.8, 1],
               from: CGPoint(x: w * 0.37, y: h * 0.32), to: CGPoint(x: w * 0.47, y: h * 0.45), radius: w * 0.62)
        c.restoreGState()
        // Thin cool outline so the ball reads against white drifts.
        c.setStrokeColor(red: 0.62, green: 0.72, blue: 0.86, alpha: 0.55)
        c.setLineWidth(2.5)
        c.strokeEllipse(in: CGRect(x: 4, y: 4, width: w - 8, height: h - 8))
    }

    /// Faint dimples that turn with the ball, so rolling is visible.
    static func drawBallTex(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        var rng = Rng(seed: 0xC0FFEE)
        c.saveGState()
        c.addEllipse(in: CGRect(x: 8, y: 8, width: w - 16, height: h - 16))
        c.clip()
        for _ in 0..<26 {
            let a = CGFloat(rng.range(0, 2 * .pi))
            let d = CGFloat(rng.range(0.1, 0.84)) * w / 2
            let r = CGFloat(rng.range(5, 13))
            let p = CGPoint(x: w / 2 + cos(a) * d, y: h / 2 + sin(a) * d)
            radial(c, colors: [(0.55, 0.66, 0.82, 0.30), (0.55, 0.66, 0.82, 0)], stops: [0, 1], from: p, to: p, radius: r)
            let q = CGPoint(x: p.x - r * 0.5, y: p.y - r * 0.5)
            radial(c, colors: [(1, 1, 1, 0.5), (1, 1, 1, 0)], stops: [0, 1], from: q, to: q, radius: r * 0.7)
        }
        c.restoreGState()
    }

    /// Carrot nose pointing right.
    static func drawCarrot(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let path = CGMutablePath()
        path.move(to: CGPoint(x: w * 0.04, y: h * 0.5))
        path.addCurve(to: CGPoint(x: w * 0.12, y: h * 0.12),
                      control1: CGPoint(x: w * 0.03, y: h * 0.3), control2: CGPoint(x: w * 0.06, y: h * 0.14))
        path.addLine(to: CGPoint(x: w * 0.97, y: h * 0.47))
        path.addLine(to: CGPoint(x: w * 0.97, y: h * 0.53))
        path.addLine(to: CGPoint(x: w * 0.12, y: h * 0.88))
        path.addCurve(to: CGPoint(x: w * 0.04, y: h * 0.5),
                      control1: CGPoint(x: w * 0.06, y: h * 0.86), control2: CGPoint(x: w * 0.03, y: h * 0.7))
        path.closeSubpath()
        c.saveGState()
        c.addPath(path)
        c.clip()
        linear(c, colors: [(1.0, 0.66, 0.24, 1), (0.98, 0.50, 0.10, 1), (0.82, 0.34, 0.05, 1)], stops: [0, 0.5, 1],
               from: CGPoint(x: 0, y: h * 0.1), to: CGPoint(x: 0, y: h * 0.9))
        c.setStrokeColor(red: 0.72, green: 0.28, blue: 0.03, alpha: 0.55)
        c.setLineWidth(2.5)
        for i in 1...4 {
            let x = w * (0.16 + CGFloat(i) * 0.14)
            c.move(to: CGPoint(x: x, y: h * 0.2))
            c.addQuadCurve(to: CGPoint(x: x + w * 0.02, y: h * 0.62), control: CGPoint(x: x + w * 0.05, y: h * 0.4))
            c.strokePath()
        }
        c.restoreGState()
    }

    /// Twig arm growing from the left edge towards the upper right.
    static func drawTwig(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        c.saveGState()
        c.setLineCap(.round)
        c.setLineJoin(.round)
        func stroke(_ a: CGPoint, _ b: CGPoint, _ width: CGFloat) {
            c.setStrokeColor(red: 0.30, green: 0.19, blue: 0.11, alpha: 1)
            c.setLineWidth(width)
            c.move(to: a)
            c.addLine(to: b)
            c.strokePath()
            c.setStrokeColor(red: 0.50, green: 0.34, blue: 0.20, alpha: 1)
            c.setLineWidth(width * 0.45)
            c.move(to: CGPoint(x: a.x, y: a.y - width * 0.15))
            c.addLine(to: CGPoint(x: b.x, y: b.y - width * 0.15))
            c.strokePath()
        }
        let root = CGPoint(x: w * 0.04, y: h * 0.62)
        let elbow = CGPoint(x: w * 0.62, y: h * 0.46)
        stroke(root, elbow, h * 0.11)
        stroke(elbow, CGPoint(x: w * 0.94, y: h * 0.30), h * 0.085)
        stroke(elbow, CGPoint(x: w * 0.86, y: h * 0.66), h * 0.07)
        stroke(CGPoint(x: w * 0.78, y: h * 0.38), CGPoint(x: w * 0.84, y: h * 0.10), h * 0.06)
        stroke(CGPoint(x: w * 0.36, y: h * 0.53), CGPoint(x: w * 0.46, y: h * 0.26), h * 0.06)
        c.restoreGState()
    }

    /// Upside-down bucket, the classic snowman hat.
    static func drawBucket(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let path = CGMutablePath()
        path.move(to: CGPoint(x: w * 0.30, y: h * 0.14))
        path.addLine(to: CGPoint(x: w * 0.70, y: h * 0.14))
        path.addLine(to: CGPoint(x: w * 0.86, y: h * 0.84))
        path.addQuadCurve(to: CGPoint(x: w * 0.14, y: h * 0.84), control: CGPoint(x: w * 0.5, y: h * 0.96))
        path.closeSubpath()
        c.saveGState()
        c.addPath(path)
        c.clip()
        linear(c, colors: [(0.42, 0.50, 0.60, 1), (0.80, 0.86, 0.92, 1), (0.60, 0.68, 0.77, 1), (0.36, 0.43, 0.53, 1)],
               stops: [0, 0.3, 0.62, 1], from: CGPoint(x: w * 0.14, y: 0), to: CGPoint(x: w * 0.86, y: 0))
        c.setStrokeColor(red: 0.25, green: 0.31, blue: 0.40, alpha: 0.7)
        c.setLineWidth(3)
        for y in [0.34, 0.60] {
            c.move(to: CGPoint(x: 0, y: h * CGFloat(y)))
            c.addQuadCurve(to: CGPoint(x: w, y: h * CGFloat(y)), control: CGPoint(x: w * 0.5, y: h * CGFloat(y + 0.07)))
            c.strokePath()
        }
        c.restoreGState()
        // Rim.
        c.setStrokeColor(red: 0.30, green: 0.37, blue: 0.47, alpha: 1)
        c.setLineWidth(7)
        c.setLineCap(.round)
        c.move(to: CGPoint(x: w * 0.12, y: h * 0.84))
        c.addQuadCurve(to: CGPoint(x: w * 0.88, y: h * 0.84), control: CGPoint(x: w * 0.5, y: h * 0.97))
        c.strokePath()
    }

    static func drawTopHat(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        let crown = CGRect(x: w * 0.27, y: h * 0.12, width: w * 0.46, height: h * 0.64)
        c.saveGState()
        c.addPath(CGPath(roundedRect: crown, cornerWidth: 8, cornerHeight: 8, transform: nil))
        c.clip()
        linear(c, colors: [(0.10, 0.11, 0.14, 1), (0.30, 0.32, 0.38, 1), (0.12, 0.13, 0.16, 1)], stops: [0, 0.32, 1],
               from: CGPoint(x: crown.minX, y: 0), to: CGPoint(x: crown.maxX, y: 0))
        c.setFillColor(red: 0.82, green: 0.16, blue: 0.20, alpha: 1)
        c.fill(CGRect(x: crown.minX, y: h * 0.60, width: crown.width, height: h * 0.13))
        c.restoreGState()
        let brim = CGRect(x: w * 0.08, y: h * 0.74, width: w * 0.84, height: h * 0.13)
        c.saveGState()
        c.addPath(CGPath(roundedRect: brim, cornerWidth: brim.height / 2, cornerHeight: brim.height / 2, transform: nil))
        c.clip()
        linear(c, colors: [(0.26, 0.28, 0.33, 1), (0.08, 0.09, 0.11, 1)], stops: [0, 1],
               from: CGPoint(x: 0, y: brim.minY), to: CGPoint(x: 0, y: brim.maxY))
        c.restoreGState()
    }

    /// Frost growing from the top-left corner: a cold haze with ice ferns.
    static func drawFrost(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        radial(c, colors: [(1, 1, 1, 0.5), (0.92, 0.96, 1, 0.22), (0.92, 0.96, 1, 0.06), (0.92, 0.96, 1, 0)],
               stops: [0, 0.35, 0.7, 1], from: .zero, to: .zero, radius: w)
        var rng = Rng(seed: 1231)
        c.setLineCap(.round)
        func fern(_ x: CGFloat, _ y: CGFloat, _ angle: CGFloat, _ length: CGFloat, _ width: CGFloat, _ depth: Int) {
            guard depth > 0, length > 3 else { return }
            let x2 = x + cos(angle) * length, y2 = y + sin(angle) * length
            // Fade out with distance from the corner.
            let dist = (x2 * x2 + y2 * y2).squareRoot() / w
            let alpha = max(0, 0.75 - dist * 0.75) * (0.45 + 0.15 * CGFloat(depth))
            c.setStrokeColor(red: 1, green: 1, blue: 1, alpha: alpha)
            c.setLineWidth(width)
            c.move(to: CGPoint(x: x, y: y))
            c.addLine(to: CGPoint(x: x2, y: y2))
            c.strokePath()
            let steps = max(1, Int(length / 9))
            for k in 1..<max(2, steps) {
                let t = CGFloat(k) / CGFloat(steps)
                let px = x + cos(angle) * length * t, py = y + sin(angle) * length * t
                let side: CGFloat = k % 2 == 0 ? 1 : -1
                fern(px, py, angle + side * CGFloat(rng.range(0.8, 1.15)), length * 0.3 * (1.1 - t * 0.6), width * 0.6, depth - 1)
            }
            fern(x2, y2, angle + CGFloat(rng.range(-0.4, 0.4)), length * 0.6, width * 0.78, depth - 1)
        }
        for _ in 0..<10 {
            let a = CGFloat(rng.range(0.08, 1.49))
            fern(CGFloat(rng.range(0, 14)), CGFloat(rng.range(0, 14)), a, CGFloat(rng.range(50, 105)) * w / 256, 2.2, 3)
        }
        // A few loose ice crystals.
        for _ in 0..<26 {
            let d = CGFloat(rng.range(0.1, 0.75)) * w, a = CGFloat(rng.range(0, 1.57))
            let size = CGFloat(rng.range(5, 12))
            c.saveGState()
            c.translateBy(x: cos(a) * d - size / 2, y: sin(a) * d - size / 2)
            c.setAlpha(max(0, 0.7 - d / w))
            drawCrystal(c, size, size, variant: 2, lineScale: 1.4)
            c.restoreGState()
        }
    }

    /// Drawn in greys so one picture can be tinted any colour.
    static func drawScarf(_ c: CGContext, _ w: CGFloat, _ h: CGFloat) {
        func striped(_ path: CGPath, vertical: Bool) {
            c.saveGState()
            c.addPath(path)
            c.clip()
            c.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
            c.fill(CGRect(x: 0, y: 0, width: w, height: h))
            c.setFillColor(red: 0.72, green: 0.72, blue: 0.72, alpha: 1)
            let step = w * 0.09
            var t: CGFloat = 0
            while t < max(w, h) {
                if vertical {
                    c.fill(CGRect(x: 0, y: t, width: w, height: step * 0.5))
                } else {
                    c.fill(CGRect(x: t, y: 0, width: step * 0.5, height: h))
                }
                t += step
            }
            linear(c, colors: [(0, 0, 0, 0), (0, 0, 0, 0.22)], stops: [0, 1],
                   from: CGPoint(x: 0, y: 0), to: CGPoint(x: 0, y: h))
            c.restoreGState()
        }
        let tail = CGMutablePath()
        tail.move(to: CGPoint(x: w * 0.60, y: h * 0.22))
        tail.addLine(to: CGPoint(x: w * 0.78, y: h * 0.22))
        tail.addLine(to: CGPoint(x: w * 0.84, y: h * 0.94))
        tail.addLine(to: CGPoint(x: w * 0.66, y: h * 0.98))
        tail.closeSubpath()
        striped(tail, vertical: true)
        let band = CGMutablePath()
        band.move(to: CGPoint(x: w * 0.04, y: h * 0.10))
        band.addQuadCurve(to: CGPoint(x: w * 0.96, y: h * 0.10), control: CGPoint(x: w * 0.5, y: h * 0.30))
        band.addLine(to: CGPoint(x: w * 0.96, y: h * 0.34))
        band.addQuadCurve(to: CGPoint(x: w * 0.04, y: h * 0.34), control: CGPoint(x: w * 0.5, y: h * 0.56))
        band.closeSubpath()
        striped(band, vertical: false)
    }
}
