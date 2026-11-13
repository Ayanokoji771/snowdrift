import AppKit
import SwiftUI
import ImageIO
import Metal
import UniformTypeIdentifiers

private func writePNG(_ image: CGImage, to path: String) -> Bool {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let dest = CGImageDestinationCreateWithURL(url, UTType.png.identifier as CFString, 1, nil) else { return false }
    CGImageDestinationAddImage(dest, image, nil)
    return CGImageDestinationFinalize(dest)
}

/// `Snowdrift --icon out.png` paints the 1024 px app icon.
enum IconMaker {
    static func write(path: String) -> Bool {
        let n = 1024
        guard let c = CGContext(data: nil, width: n, height: n, bitsPerComponent: 8, bytesPerRow: n * 4,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
        c.translateBy(x: 0, y: CGFloat(n))
        c.scaleBy(x: 1, y: -1)

        let body = CGRect(x: 100, y: 100, width: 824, height: 824)
        let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

        // Soft drop shadow.
        c.saveGState()
        c.setShadow(offset: CGSize(width: 0, height: 10), blur: 28, color: CGColor(gray: 0, alpha: 0.35))
        c.addPath(shape)
        c.setFillColor(CGColor(red: 0.1, green: 0.15, blue: 0.35, alpha: 1))
        c.fillPath()
        c.restoreGState()

        c.saveGState()
        c.addPath(shape)
        c.clip()
        let sky = CGGradient(colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                             colorComponents: [0.06, 0.10, 0.28, 1, 0.16, 0.30, 0.64, 1, 0.42, 0.62, 0.93, 1],
                             locations: [0, 0.6, 1], count: 3)!
        c.drawLinearGradient(sky, start: CGPoint(x: 0, y: body.minY), end: CGPoint(x: 0, y: body.maxY), options: [])

        // Falling flakes.
        var rng = Rng(seed: 42)
        for _ in 0..<70 {
            let x = CGFloat(rng.range(110, 914)), y = CGFloat(rng.range(110, 760))
            let r = CGFloat(rng.range(3, 9))
            c.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: CGFloat(rng.range(0.35, 0.9))))
            c.fillEllipse(in: CGRect(x: x - r, y: y - r, width: r * 2, height: r * 2))
        }

        // Drifts.
        c.setFillColor(CGColor(red: 0.86, green: 0.91, blue: 0.98, alpha: 1))
        let back = CGMutablePath()
        back.move(to: CGPoint(x: 100, y: 800))
        back.addCurve(to: CGPoint(x: 924, y: 760), control1: CGPoint(x: 360, y: 700), control2: CGPoint(x: 640, y: 830))
        back.addLine(to: CGPoint(x: 924, y: 924))
        back.addLine(to: CGPoint(x: 100, y: 924))
        back.closeSubpath()
        c.addPath(back)
        c.fillPath()
        c.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        let front = CGMutablePath()
        front.move(to: CGPoint(x: 100, y: 850))
        front.addCurve(to: CGPoint(x: 924, y: 830), control1: CGPoint(x: 420, y: 770), control2: CGPoint(x: 700, y: 900))
        front.addLine(to: CGPoint(x: 924, y: 924))
        front.addLine(to: CGPoint(x: 100, y: 924))
        front.closeSubpath()
        c.addPath(front)
        c.fillPath()
        c.restoreGState()

        // Big crystal.
        c.saveGState()
        c.translateBy(x: 512 - 260, y: 430 - 260)
        c.setShadow(offset: .zero, blur: 30, color: CGColor(red: 0.7, green: 0.85, blue: 1, alpha: 0.9))
        Atlas.drawCrystal(c, 520, 520, variant: 1, lineScale: 0.9)
        c.restoreGState()

        guard let image = c.makeImage() else { return false }
        return writePNG(image, to: path)
    }
}

/// `Snowdrift --snapshot out.png` runs the simulation on a made-up desktop and saves a picture.
/// Used to check the look and the physics without touching the real screen.
enum Snapshot {
    static func run(path: String, seconds: Float = 75, season: Int = 0, evening: Bool = false) -> Bool {
        guard let device = MTLCreateSystemDefaultDevice(), let renderer = Renderer(device: device) else {
            print("no Metal")
            return false
        }
        let W: Float = 1512, H: Float = 982, top: Float = 33
        let sim = Sim(width: W, height: H, topInset: top)
        sim.config = SimConfig(intensity: 2, wind: 1, drifts: 3, tool: 1, garland: 3, depth: true, lowPower: false,
                               flakeSize: 1, melt: false, tree: true, frost: true, sparkle: true, season: season, evening: evening)
        let winter = season == 0
        var fireworksLaunched = false

        let windows = [
            WinInfo(id: 11, rect: CGRect(x: 820, y: 300, width: 560, height: 380), isWidget: false),
            WinInfo(id: 10, rect: CGRect(x: 120, y: 180, width: 820, height: 520), isWidget: false),
            WinInfo(id: 12, rect: CGRect(x: 1180, y: 120, width: 240, height: 140), isWidget: true),
        ]
        var snap = WindowSnapshot(version: 1, windows: windows, dock: CGRect(x: 463, y: 939, width: 585, height: 43))
        sim.applyWindows(snap, originX: 0, originY: 0, elapsed: 1, existing: { $0 })

        // A snowman made of three balls dropped onto the floor drift.
        if winter { _ = sim.debugAddBall(x: 300, y: 860, radius: 30) }
        var t: Float = 0
        let dt: Float = 1 / 60
        var dropped = 0
        var moved = false
        while t < seconds {
            if !winter { dropped = 3 }
            if dropped == 0 && t > 8 { _ = sim.debugAddBall(x: 302, y: 700, radius: 22); dropped = 1 }
            if dropped == 1 && t > 10 { _ = sim.debugAddBall(x: 300, y: 600, radius: 15); dropped = 2 }
            if dropped == 2 && t > 12 { _ = sim.debugAddBall(x: 1300, y: 850, radius: 18); dropped = 3 }
            // Move the back window a bit mid-way, to check that its drift follows.
            if !moved && t > seconds * 0.7 {
                moved = true
                snap.windows[0] = WinInfo(id: 11, rect: CGRect(x: 860, y: 310, width: 560, height: 380), isWidget: false)
                snap.version += 1
                sim.applyWindows(snap, originX: 0, originY: 0, elapsed: 1, existing: { $0 })
            }
            if winter && !fireworksLaunched && t > seconds - 4.5 {
                fireworksLaunched = true
                sim.festive.launchFireworks(seconds: 5)
            }
            // Sweep the cursor through the left part of the big window's drift once.
            let sweeping = t > seconds * 0.85 && t < seconds * 0.85 + 0.5
            // In summer evenings the cursor rests on the desktop, so the fireflies gather round it.
            let resting = !winter && evening
            let cx: Float = sweeping ? 160 + (t - seconds * 0.85) * 400 : (resting ? 1050 : -500)
            sim.step(dt: dt, cursorX: cx, cursorY: sweeping ? 176 : 800, cursorInside: sweeping || resting)
            t += dt
        }
        let stats = sim.debugStats
        print("flakes \(stats.flakes) debris \(stats.debris) surfaces \(stats.surfaces) snow \(Int(stats.snow)) maxH \(sim.debugMaxHeight()) balls \(sim.balls.count) stacks \(sim.balls.filter { $0.below == nil }.count) nan \(sim.debugHasNaN())")

        // Timing of one frame of simulation + geometry.
        let batch = renderer.makeBatch()
        let t0 = CFAbsoluteTimeGetCurrent()
        for _ in 0..<120 {
            sim.step(dt: dt, cursorX: -500, cursorY: 0, cursorInside: false)
            batch.clear()
            sim.build(batch)
        }
        let ms = (CFAbsoluteTimeGetCurrent() - t0) / 120 * 1000
        print(String(format: "cpu per frame %.3f ms, vertices %d", ms, batch.verts.count))

        guard let snowImage = renderSnow(renderer: renderer, batch: batch, width: W, height: H, scale: 2) else { return false }
        let scale = 2
        let pw = Int(W) * scale, ph = Int(H) * scale
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let c = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: pw * 4, space: space,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
        let snow = snowImage

        // Fake desktop, drawn top-down.
        c.saveGState()
        c.translateBy(x: 0, y: CGFloat(ph))
        c.scaleBy(x: CGFloat(scale), y: -CGFloat(scale))
        let bg = evening ? CGGradient(colorSpace: space, colorComponents: [0.02, 0.03, 0.08, 1, 0.08, 0.10, 0.20, 1], locations: [0, 1], count: 2)!
                         : CGGradient(colorSpace: space, colorComponents: [0.10, 0.13, 0.30, 1, 0.33, 0.30, 0.55, 1], locations: [0, 1], count: 2)!
        c.drawLinearGradient(bg, start: .zero, end: CGPoint(x: 0, y: CGFloat(H)), options: [])
        c.setFillColor(CGColor(gray: 0.1, alpha: 0.35))
        c.fill(CGRect(x: 0, y: 0, width: CGFloat(W), height: CGFloat(top)))
        for w in snap.windows.reversed() {
            let r = w.rect
            let path = CGPath(roundedRect: r, cornerWidth: w.isWidget ? 22 : 12, cornerHeight: w.isWidget ? 22 : 12, transform: nil)
            c.saveGState()
            c.setShadow(offset: CGSize(width: 0, height: 10), blur: 30, color: CGColor(gray: 0, alpha: 0.5))
            c.addPath(path)
            c.setFillColor(w.isWidget ? CGColor(gray: 0.25, alpha: 0.85) : CGColor(gray: 0.95, alpha: 1))
            c.fillPath()
            c.restoreGState()
            if !w.isWidget {
                c.saveGState()
                c.addPath(path)
                c.clip()
                c.setFillColor(CGColor(gray: 0.86, alpha: 1))
                c.fill(CGRect(x: r.minX, y: r.minY, width: r.width, height: 52))
                for (k, col) in [(0, CGColor(red: 1, green: 0.37, blue: 0.34, alpha: 1)), (1, CGColor(red: 1, green: 0.74, blue: 0.2, alpha: 1)),
                                 (2, CGColor(red: 0.16, green: 0.78, blue: 0.25, alpha: 1))] {
                    c.setFillColor(col)
                    c.fillEllipse(in: CGRect(x: r.minX + 18 + CGFloat(k) * 20, y: r.minY + 20, width: 12, height: 12))
                }
                c.restoreGState()
            }
        }
        if let d = snap.dock {
            c.addPath(CGPath(roundedRect: d.insetBy(dx: 0, dy: 4), cornerWidth: 18, cornerHeight: 18, transform: nil))
            c.setFillColor(CGColor(gray: 0.85, alpha: 0.35))
            c.fillPath()
        }
        c.restoreGState()
        c.draw(snow, in: CGRect(x: 0, y: 0, width: pw, height: ph))

        guard let image = c.makeImage() else { return false }
        return writePNG(image, to: path)
    }
}

/// Renders a batch offscreen and returns it as an image (snow on transparent).
func renderSnow(renderer: Renderer, batch: Batch, width W: Float, height H: Float, scale: Int) -> CGImage? {
    let device = renderer.device
    let pw = Int(W) * scale, ph = Int(H) * scale
    let desc = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: Renderer.pixelFormat, width: pw, height: ph, mipmapped: false)
    desc.usage = [.renderTarget, .shaderRead]
    desc.storageMode = .managed
    guard let tex = device.makeTexture(descriptor: desc) else { return nil }
    let pass = MTLRenderPassDescriptor()
    pass.colorAttachments[0].texture = tex
    pass.colorAttachments[0].loadAction = .clear
    pass.colorAttachments[0].storeAction = .store
    pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
    renderer.draw(batch: batch, buffers: FrameBuffers(), pass: pass, viewport: SIMD2(W, H), present: nil, wait: true)
    if let cb = renderer.queue.makeCommandBuffer(), let blit = cb.makeBlitCommandEncoder() {
        blit.synchronize(resource: tex)
        blit.endEncoding()
        cb.commit()
        cb.waitUntilCompleted()
    }
    var bytes = [UInt8](repeating: 0, count: pw * ph * 4)
    tex.getBytes(&bytes, bytesPerRow: pw * 4, from: MTLRegionMake2D(0, 0, pw, ph), mipmapLevel: 0)
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
    return CGImage(width: pw, height: ph, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: pw * 4, space: space,
                   bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue),
                   provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
}

/// Debug aid for the live app: `SNOWDRIFT_DEBUG=/some/dir open Snowdrift.app` logs frame stats
/// and saves what each screen draws, composited over the window outlines it saw.
enum LiveDebug {
    static let dir: String? = ProcessInfo.processInfo.environment["SNOWDRIFT_DEBUG"]

    static func capture(renderer: Renderer, sim: Sim, screenIndex: Int, windows: [CGRect], dock: CGRect?, frames: Int, seconds: Double,
                        covered: Bool = false, visibility: Float = 1) {
        guard let dir else { return }
        let batch = renderer.makeBatch()
        sim.build(batch)
        let W = sim.W, H = sim.H
        let stats = sim.debugStats
        let line = String(format: "screen %d: %d frames in %.1fs (%.1f fps), flakes %d, debris %d, surfaces %d, snow %.0f, maxH %.1f, balls %d, windows %d, fullscreen %@, visibility %.2f\n",
                          screenIndex, frames, seconds, Double(frames) / max(0.001, seconds), stats.flakes, stats.debris,
                          stats.surfaces, stats.snow, sim.debugMaxHeight(), sim.balls.count, windows.count,
                          covered ? "yes" : "no", visibility)
        if let h = FileHandle(forWritingAtPath: dir + "/log.txt") ?? {
            FileManager.default.createFile(atPath: dir + "/log.txt", contents: nil)
            return FileHandle(forWritingAtPath: dir + "/log.txt")
        }() {
            h.seekToEndOfFile()
            h.write(line.data(using: .utf8)!)
            h.closeFile()
        }
        guard let snow = renderSnow(renderer: renderer, batch: batch, width: W, height: H, scale: 1) else { return }
        let pw = Int(W), ph = Int(H)
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let c = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: pw * 4, space: space,
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        c.saveGState()
        c.translateBy(x: 0, y: CGFloat(ph))
        c.scaleBy(x: 1, y: -1)
        c.setFillColor(CGColor(red: 0.12, green: 0.14, blue: 0.3, alpha: 1))
        c.fill(CGRect(x: 0, y: 0, width: pw, height: ph))
        c.setStrokeColor(CGColor(red: 1, green: 0.6, blue: 0.2, alpha: 0.9))
        c.setLineWidth(1.5)
        for r in windows { c.stroke(r) }
        if let dock {
            c.setStrokeColor(CGColor(red: 0.3, green: 1, blue: 0.5, alpha: 0.9))
            c.stroke(dock)
        }
        c.restoreGState()
        c.draw(snow, in: CGRect(x: 0, y: 0, width: pw, height: ph))
        if let image = c.makeImage() { _ = writePNG(image, to: dir + "/screen\(screenIndex).png") }
    }
}

/// `Snowdrift --selftest` checks the cases that used to go wrong.
enum SelfTest {
    static func run() -> Bool {
        var ok = true
        func check(_ cond: Bool, _ what: String) {
            print((cond ? "PASS " : "FAIL ") + what)
            if !cond { ok = false }
        }
        let sim = Sim(width: 1512, height: 982, topInset: 33)
        sim.config = SimConfig(intensity: 2, wind: 2, drifts: 3, tool: 0, garland: 0, depth: true, lowPower: false,
                               flakeSize: 1, melt: false, tree: false, frost: false, sparkle: false)
        let dock = CGRect(x: 463, y: 939, width: 585, height: 43)
        let win = WinInfo(id: 7, rect: CGRect(x: 200, y: 300, width: 600, height: 400), isWidget: false)
        func run(_ seconds: Float) {
            var t: Float = 0
            while t < seconds {
                sim.step(dt: 1 / 60, cursorX: -500, cursorY: -500, cursorInside: false)
                t += 1 / 60
            }
        }

        // Dock shown: snow settles on it.
        sim.applyWindows(WindowSnapshot(version: 1, windows: [win], dock: dock), originX: 0, originY: 0, elapsed: 1, existing: { $0 })
        run(40)
        let before = sim.debugDock
        check(before.active && before.snow > 500, "snow settles on a visible Dock (\(Int(before.snow)))")

        // Dock hidden (full-screen app): it stops being a surface, its snow is kept, nothing piles up in the air.
        sim.applyWindows(WindowSnapshot(version: 2, windows: [win], dock: nil), originX: 0, originY: 0, elapsed: 1, existing: { $0 })
        run(40)
        let hidden = sim.debugDock
        check(!hidden.active, "hidden Dock is not a surface")
        check(abs(hidden.snow - before.snow) < 1, "hidden Dock keeps its snow and gets no more")

        // Dock back: the old drift is there again.
        sim.applyWindows(WindowSnapshot(version: 3, windows: [win], dock: dock), originX: 0, originY: 0, elapsed: 1, existing: { $0 })
        let back = sim.debugDock
        check(back.active && back.snow >= before.snow - 1, "Dock drift comes back with the Dock")

        // A window that moves while the snow is hidden doesn't shake its drift off.
        let debrisBefore = sim.debugDebris
        let moved = WinInfo(id: 7, rect: CGRect(x: 700, y: 200, width: 600, height: 400), isWidget: false)
        sim.applyWindows(WindowSnapshot(version: 4, windows: [moved], dock: dock), originX: 0, originY: 0, elapsed: 10, existing: { $0 })
        check(sim.debugDebris - debrisBefore < 5, "no snow shaken off by moves while hidden")

        // ...but a real fast yank does shake it.
        let yanked = WinInfo(id: 7, rect: CGRect(x: 200, y: 260, width: 600, height: 400), isWidget: false)
        sim.applyWindows(WindowSnapshot(version: 5, windows: [yanked], dock: dock), originX: 0, originY: 0, elapsed: 1.0 / 60, existing: { $0 })
        check(sim.debugDebris - debrisBefore > 20, "a fast yank still shakes snow off")
        check(!sim.debugHasNaN(), "no NaN")

        // Seasons.
        func seasonSim(_ season: Int) -> Sim {
            let s = Sim(width: 1512, height: 982, topInset: 33)
            s.config = SimConfig(intensity: 2, wind: 1, drifts: 3, tool: 0, garland: 0, depth: true, lowPower: false,
                                 flakeSize: 1, melt: false, tree: true, frost: false, sparkle: false, season: season, evening: true)
            s.applyWindows(WindowSnapshot(version: 1, windows: [win], dock: dock), originX: 0, originY: 0, elapsed: 1, existing: { $0 })
            for _ in 0..<(60 * 50) { s.step(dt: 1 / 60, cursorX: 760, cursorY: 600, cursorInside: true) }
            return s
        }
        let spring = seasonSim(1), summer = seasonSim(2), autumn = seasonSim(3)
        check(spring.debugStats.snow > 300 && spring.debugMaxHeight() < 16, "spring petals lie in a thin layer (\(Int(spring.debugStats.snow)), max \(spring.debugMaxHeight()))")
        check(summer.debugStats.snow == 0 && summer.debugStats.flakes > 10, "summer fluff floats and never piles up (\(summer.debugStats.flakes) in the air)")
        check(autumn.debugStats.snow > 1500, "autumn leaves heap up on windows, the Dock and the floor (\(Int(autumn.debugStats.snow)))")
        check(!spring.debugHasNaN() && !summer.debugHasNaN() && !autumn.debugHasNaN(), "no NaN in any season")

        // Winter to spring: the snowmen melt and the drifts blow away.
        let w = Sim(width: 1512, height: 982, topInset: 33)
        w.config = SimConfig(intensity: 2, wind: 1, drifts: 3, tool: 1, garland: 0, depth: true, lowPower: false,
                             flakeSize: 1, melt: false, tree: false, frost: false, sparkle: false)
        w.applyWindows(WindowSnapshot(version: 1, windows: [win], dock: dock), originX: 0, originY: 0, elapsed: 1, existing: { $0 })
        _ = w.debugAddBall(x: 300, y: 860, radius: 30)
        for _ in 0..<(60 * 20) { w.step(dt: 1 / 60, cursorX: -500, cursorY: -500, cursorInside: false) }
        let hadSnow = w.debugStats.snow
        w.config.season = 1
        check(hadSnow > 1000 && w.debugStats.snow == 0 && !w.hasBalls && w.debugDebris > 20,
              "a new season blows the old one away (snow \(Int(hadSnow)) → \(Int(w.debugStats.snow)), puffs \(w.debugDebris))")
        return ok
    }
}

/// `Snowdrift --island-preview out.png` draws the music island in its three states with a made-up track.
enum IslandPreview {
    @MainActor
    static func run(path: String) -> Bool {
        let size = 256
        guard let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
        // A made-up album cover: dusk sky, moon, hills.
        let grad = CGGradient(colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                              colorComponents: [0.95, 0.45, 0.55, 1, 0.45, 0.25, 0.65, 1, 0.12, 0.10, 0.35, 1],
                              locations: [0, 0.5, 1], count: 3)!
        ctx.drawLinearGradient(grad, start: CGPoint(x: 0, y: 0), end: CGPoint(x: 0, y: 256), options: [])
        ctx.setFillColor(CGColor(red: 1, green: 0.95, blue: 0.85, alpha: 1))
        ctx.fillEllipse(in: CGRect(x: 150, y: 150, width: 60, height: 60))
        ctx.setFillColor(CGColor(red: 0.08, green: 0.06, blue: 0.2, alpha: 1))
        ctx.fillEllipse(in: CGRect(x: -60, y: -120, width: 260, height: 220))
        ctx.fillEllipse(in: CGRect(x: 90, y: -140, width: 260, height: 230))
        let art = NSImage(cgImage: ctx.makeImage()!, size: NSSize(width: size, height: size))

        let playing = NowPlaying()
        playing.source = .spotify
        playing.trackID = "x"
        playing.title = "Снег идёт всю ночь (Winter Mix)"
        playing.artist = "Aurora Lights"
        playing.isPlaying = true
        playing.duration = 214
        playing.basePosition = 83
        playing.baseTime = Date()
        playing.lastActive = Date()
        playing.artwork = art
        playing.accent = MusicService.accent(of: art)
        let empty = NowPlaying()

        let m = IslandMetrics(notchWidth: 182, notchHeight: 32, hasNotch: true)
        let compact = IslandState()
        let open = IslandState()
        open.expanded = true

        func screenTop(_ now: NowPlaying, _ state: IslandState, height: CGFloat) -> some View {
            ZStack(alignment: .top) {
                LinearGradient(colors: [Color(red: 0.16, green: 0.2, blue: 0.38), Color(red: 0.38, green: 0.32, blue: 0.55)],
                               startPoint: .top, endPoint: .bottom)
                Rectangle().fill(.white.opacity(0.08)).frame(height: 32)
                IslandView(now: now, state: state, m: m, service: MusicService.shared)
                // The physical notch hides the very top of the island.
                Rectangle().fill(.black).frame(width: 182, height: 32)
            }
            .frame(width: 640, height: height)
            .clipShape(RoundedRectangle(cornerRadius: 12))
        }

        let view = VStack(spacing: 18) {
            screenTop(playing, compact, height: 70)
            screenTop(playing, open, height: 270)
            screenTop(empty, open, height: 190)
        }
        .padding(18)
        .background(Color(white: 0.1))

        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let cg = renderer.cgImage else { return false }
        return writePNG(cg, to: path)
    }
}
