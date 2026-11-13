import Foundation

/// Extras for one screen: fireworks, a corner decoration for each season (Christmas tree, cherry tree,
/// sunflowers, pumpkins), frosty corners, and the small living things: butterflies and fireflies.
final class Festive {
    private let W: Float
    private let H: Float
    private var rng = Rng()
    private var time: Float = 0

    init(width: Float, height: Float) {
        W = width
        H = height
        treeH = min(230, max(130, height * 0.22))
        treeW = treeH * 0.74
        treeX = width - treeW * 0.5 - 34
        treeBox = FRect(x0: treeX - treeW * 0.5 - 34, y0: height - treeH - 18, x1: treeX + treeW * 0.5 + 34, y1: height)
        layoutTree()
    }

    private(set) var season: Season = .winter
    private var evening = false
    private var occluders: [FRect] = []

    func step(dt: Float, season: Season, showDecor: Bool, evening: Bool, cursorX: Float?, cursorY: Float, occluders: [FRect]) {
        time += dt
        if season != self.season {
            // The old decoration goes at once; the new one fades in.
            self.season = season
            treeAlpha = 0
            fallingPetals.removeAll()
            butterflies.removeAll()
        }
        self.evening = evening
        self.occluders = occluders
        stepFireworks(dt: dt)
        var free = true
        for o in occluders where o.x0 < treeBox.x1 && o.x1 > treeBox.x0 && o.y0 < treeBox.y1 && o.y1 > treeBox.y0 {
            free = false
            break
        }
        let target: Float = showDecor && free ? 1 : 0
        treeAlpha += (target - treeAlpha) * min(1, dt * 5)
        if abs(target - treeAlpha) < 0.003 { treeAlpha = target }
        stepFallingPetals(dt: dt)
        stepButterflies(dt: dt, cursorX: cursorX, cursorY: cursorY)
        stepFireflies(dt: dt, cursorX: cursorX, cursorY: cursorY)
    }

    /// The decoration in the corner for this season.
    func drawDecor(_ batch: Batch) {
        switch season {
        case .winter: drawTree(batch)
        case .spring: drawSakura(batch)
        case .summer: drawSunflowers(batch)
        case .autumn: drawPumpkins(batch)
        }
    }

    /// Butterflies, fireflies and petals blowing off the cherry tree (in front of everything else).
    func drawCreatures(_ batch: Batch) {
        drawFallingPetals(batch)
        drawButterflies(batch)
        drawFireflies(batch)
    }

    // MARK: - Fireworks

    private struct Rocket {
        var x: Float, y: Float, vx: Float, vy: Float
        var color: Int, kind: Int
        var trail: Float
    }

    private struct Spark {
        var x: Float, y: Float, vx: Float, vy: Float
        var life: Float, maxLife: Float
        var r: Float, g: Float, b: Float
        var size: Float, drag: Float, gravity: Float
        var twinkle: Bool
    }

    private var rockets: [Rocket] = []
    private var sparks: [Spark] = []
    private var until: Float = 0
    private var nextLaunch: Float = 0

    private static let colors: [(Float, Float, Float)] = [
        (1.0, 0.32, 0.30), (1.0, 0.80, 0.28), (0.40, 1.0, 0.50), (0.38, 0.66, 1.0),
        (1.0, 0.45, 0.88), (0.74, 0.52, 1.0), (1.0, 1.0, 1.0), (1.0, 0.58, 0.18),
    ]

    var fireworksActive: Bool { time < until || !rockets.isEmpty || !sparks.isEmpty }

    func launchFireworks(seconds: Float) {
        until = max(until, time + seconds)
        nextLaunch = min(nextLaunch, time + 0.1)
    }

    func stopFireworks() { until = 0 }

    private func stepFireworks(dt: Float) {
        if time < until && time >= nextLaunch {
            let salvo = rng.unit() < 0.22 ? 3 : (rng.unit() < 0.4 ? 2 : 1)
            for _ in 0..<salvo {
                let apex = rng.range(H * 0.12, H * 0.42)
                let g: Float = 320
                let vy = -(2 * g * (H - apex)).squareRoot()
                rockets.append(Rocket(x: rng.range(W * 0.1, W * 0.9), y: H + 4, vx: rng.range(-35, 35), vy: vy,
                                      color: rng.int(Festive.colors.count), kind: rng.int(5), trail: 0))
            }
            nextLaunch = time + rng.range(0.45, 1.3)
        }

        var i = 0
        while i < rockets.count {
            var r = rockets[i]
            r.vy += 320 * dt
            r.x += r.vx * dt
            r.y += r.vy * dt
            r.trail += dt
            while r.trail > 0.014 {
                r.trail -= 0.014
                addSpark(x: r.x + rng.range(-1, 1), y: r.y + 2, vx: rng.range(-12, 12) - r.vx * 0.1, vy: rng.range(10, 50),
                         life: rng.range(0.25, 0.55), color: (1.0, 0.78, 0.45), size: 1.1, drag: 2.5, gravity: 60, twinkle: false)
            }
            if r.vy > -25 {
                explode(r)
                rockets.swapAt(i, rockets.count - 1)
                rockets.removeLast()
                continue
            }
            rockets[i] = r
            i += 1
        }

        let n = sparks.count
        var j = 0
        while j < sparks.count {
            var s = sparks[j]
            s.life -= dt
            if s.life <= 0 {
                sparks.swapAt(j, sparks.count - 1)
                sparks.removeLast()
                continue
            }
            let k = max(0, 1 - s.drag * dt)
            s.vx *= k
            s.vy = s.vy * k + s.gravity * dt
            s.x += s.vx * dt
            s.y += s.vy * dt
            sparks[j] = s
            j += 1
        }
        _ = n
    }

    private func addSpark(x: Float, y: Float, vx: Float, vy: Float, life: Float, color: (Float, Float, Float),
                          size: Float, drag: Float, gravity: Float, twinkle: Bool) {
        guard sparks.count < 6000 else { return }
        sparks.append(Spark(x: x, y: y, vx: vx, vy: vy, life: life, maxLife: life, r: color.0, g: color.1, b: color.2,
                            size: size, drag: drag, gravity: gravity, twinkle: twinkle))
    }

    private func explode(_ r: Rocket) {
        let main = Festive.colors[r.color]
        let second = Festive.colors[(r.color + 3) % Festive.colors.count]
        // Flash.
        addSpark(x: r.x, y: r.y, vx: 0, vy: 0, life: 0.22, color: (1, 1, 1), size: 22, drag: 0, gravity: 0, twinkle: false)
        switch r.kind {
        case 0: // Peony: a full ball of one colour.
            for _ in 0..<110 {
                let a = rng.range(0, 6.283), sp = rng.range(140, 230) * rng.unit().squareRoot().squareRoot()
                addSpark(x: r.x, y: r.y, vx: cos(a) * sp, vy: sin(a) * sp, life: rng.range(1.2, 1.9), color: main,
                         size: rng.range(1.4, 2.1), drag: 1.25, gravity: 70, twinkle: false)
            }
        case 1: // Willow: slow golden trails that droop.
            for _ in 0..<80 {
                let a = rng.range(0, 6.283), sp = rng.range(110, 180)
                addSpark(x: r.x, y: r.y, vx: cos(a) * sp, vy: sin(a) * sp, life: rng.range(2.2, 3.0), color: (1.0, 0.76, 0.32),
                         size: rng.range(1.2, 1.7), drag: 1.7, gravity: 45, twinkle: true)
            }
        case 2: // Ring with a heart of another colour.
            for k in 0..<64 {
                let a = Float(k) / 64 * 6.283
                addSpark(x: r.x, y: r.y, vx: cos(a) * 210, vy: sin(a) * 210, life: rng.range(1.3, 1.7), color: main,
                         size: 1.8, drag: 1.3, gravity: 60, twinkle: false)
            }
            for _ in 0..<36 {
                let a = rng.range(0, 6.283), sp = rng.range(30, 90)
                addSpark(x: r.x, y: r.y, vx: cos(a) * sp, vy: sin(a) * sp, life: rng.range(1.0, 1.4), color: second,
                         size: 1.6, drag: 1.4, gravity: 60, twinkle: false)
            }
        case 3: // Confetti: every spark its own colour, glittering.
            for _ in 0..<120 {
                let a = rng.range(0, 6.283), sp = rng.range(60, 220)
                addSpark(x: r.x, y: r.y, vx: cos(a) * sp, vy: sin(a) * sp, life: rng.range(1.1, 1.8),
                         color: Festive.colors[rng.int(Festive.colors.count)], size: rng.range(1.3, 1.9), drag: 1.3,
                         gravity: 75, twinkle: true)
            }
        default: // Two-tone chrysanthemum.
            for k in 0..<120 {
                let a = rng.range(0, 6.283), sp = rng.range(150, 240)
                addSpark(x: r.x, y: r.y, vx: cos(a) * sp, vy: sin(a) * sp, life: rng.range(1.3, 2.0),
                         color: k % 2 == 0 ? main : second, size: rng.range(1.3, 1.9), drag: 1.2, gravity: 70, twinkle: false)
            }
        }
    }

    func drawFireworks(_ batch: Batch) {
        for r in rockets {
            batch.sprite(.glow, x: r.x, y: r.y, hw: 9, hh: 9, r: 1, g: 0.8, b: 0.5, a: 0.7, additive: true)
            batch.sprite(.dot, x: r.x, y: r.y, hw: 2.2, hh: 2.2, r: 1, g: 0.95, b: 0.8, a: 1, additive: true)
        }
        for s in sparks {
            let t = s.life / s.maxLife
            var a = min(1, t * 1.8)
            if s.twinkle && t < 0.6 {
                a *= sin(time * 38 + s.x * 0.7 + s.y) > -0.2 ? 1 : 0.15
            }
            if s.size > 10 {
                // Explosion flash.
                batch.sprite(.glow, x: s.x, y: s.y, hw: s.size * 3, hh: s.size * 3, a: a * 0.55, additive: true)
                continue
            }
            let sz = s.size * 1.25
            batch.sprite(.glow, x: s.x, y: s.y, hw: sz * 4.6, hh: sz * 4.6, r: s.r, g: s.g, b: s.b, a: a * 0.6, additive: true)
            let core: Float = 0.45
            batch.sprite(.dot, x: s.x, y: s.y, hw: sz * 1.5, hh: sz * 1.5,
                         r: s.r + (1 - s.r) * core, g: s.g + (1 - s.g) * core, b: s.b + (1 - s.b) * core, a: a)
        }
    }

    // MARK: - Christmas tree

    private struct Ornament { var x: Float, y: Float, r: Float, color: Int }
    private struct Light { var x: Float, y: Float, color: Int, phase: Float }
    private struct Tier { var apexY: Float, bottomY: Float, halfW: Float, sag: Float }

    private let treeH: Float
    private let treeW: Float
    private let treeX: Float
    private let treeBox: FRect
    private var treeAlpha: Float = 0
    private var tiers: [Tier] = []
    private var ornaments: [Ornament] = []
    private var lights: [Light] = []

    private static let ornamentColors: [(Float, Float, Float)] = [
        (0.92, 0.18, 0.20), (1.0, 0.76, 0.22), (0.25, 0.52, 1.0), (0.85, 0.35, 0.95), (0.95, 0.95, 1.0),
    ]

    private func layoutTree() {
        var seeded = Rng(seed: 2027)
        let h = treeH, w = treeW
        for i in 0..<4 {
            let fi = Float(i)
            let bottom = H - h * 0.11 - fi * h * 0.2
            let height = h * (0.36 - 0.03 * fi)
            tiers.append(Tier(apexY: bottom - height, bottomY: bottom, halfW: w * (1 - 0.2 * fi) / 2, sag: height * 0.07))
        }
        for (i, t) in tiers.enumerated() {
            let count = 4 - i / 2
            for k in 0..<count {
                let y = t.bottomY - (t.bottomY - t.apexY) * seeded.range(0.12, 0.4)
                let half = t.halfW * (y - t.apexY) / (t.bottomY - t.apexY) * 0.8
                let x = treeX - half + (Float(k) + seeded.range(0.25, 0.75)) / Float(count) * half * 2
                ornaments.append(Ornament(x: x, y: y, r: h * seeded.range(0.026, 0.034), color: (i + k * 2) % Festive.ornamentColors.count))
            }
            // A swag of lights across each tier.
            let lineY = t.bottomY - (t.bottomY - t.apexY) * 0.55
            let half = t.halfW * 0.5
            for k in 0..<6 {
                let u = Float(k) / 5
                let x = treeX - half + u * half * 2
                let y = lineY + sin(u * .pi) * (t.bottomY - t.apexY) * 0.16
                lights.append(Light(x: x, y: y, color: (k + i) % 4, phase: seeded.range(0, 6.28)))
            }
        }
    }

    func drawTree(_ batch: Batch) {
        let al = treeAlpha
        guard al > 0.003 else { return }
        let h = treeH, w = treeW, cx = treeX

        // A mound of snow around the foot of the tree.
        for k in 0..<26 {
            let u = Float(k) / 25 * 2 - 1
            let x = cx + u * w * 0.78
            let hgt = (1 - u * u) * 7 + 3
            batch.sprite(.dot, x: x, y: H - hgt * 0.5, hw: hgt * 1.1 + 3, hh: hgt * 0.9 + 2, a: al)
        }

        // Trunk.
        batch.rect(x: cx - w * 0.07, y: H - h * 0.14, w: w * 0.14, h: h * 0.14, r: 0.36, g: 0.22, b: 0.12, a: al)
        batch.rect(x: cx - w * 0.07, y: H - h * 0.14, w: w * 0.04, h: h * 0.14, r: 0.25, g: 0.15, b: 0.08, a: al)

        // Branches, bottom tier first so upper tiers overlap it.
        for t in tiers {
            let apex = batch.flat(cx, t.apexY, 0.22, 0.58, 0.32, al)
            let left = batch.flat(cx - t.halfW, t.bottomY, 0.05, 0.27, 0.14, al)
            let mid = batch.flat(cx, t.bottomY + t.sag, 0.08, 0.37, 0.19, al)
            let right = batch.flat(cx + t.halfW, t.bottomY, 0.11, 0.45, 0.23, al)
            batch.tri(apex, left, mid)
            batch.tri(apex, mid, right)
            // Snow along the lower edge and on the tip.
            let steps = Int(t.halfW * 2 / 5)
            for k in 0...steps {
                let u = Float(k) / Float(steps)
                let x = cx - t.halfW + u * t.halfW * 2
                let y = t.bottomY + t.sag * sin(u * .pi) - 1.5
                let s: Float = k % 3 == 0 ? 3.6 : 2.8
                batch.sprite(.dot, x: x, y: y, hw: s, hh: s, a: al * 0.95)
            }
            for k in 0..<4 {
                let u = Float(k) / 4 * 0.22
                batch.sprite(.dot, x: cx - t.halfW * u * 0.9, y: t.apexY + (t.bottomY - t.apexY) * u + 2, hw: 2.6, hh: 2.6, a: al * 0.9)
            }
        }

        for o in ornaments {
            let c = Festive.ornamentColors[o.color]
            batch.sprite(.ballBase, x: o.x, y: o.y, hw: o.r, hh: o.r, r: c.0, g: c.1, b: c.2, a: al)
            batch.sprite(.dot, x: o.x - o.r * 0.32, y: o.y - o.r * 0.35, hw: o.r * 0.35, hh: o.r * 0.35, a: al * 0.8)
        }

        for (i, l) in lights.enumerated() {
            let c = Festive.colors[[1, 0, 3, 2][l.color]]
            let tw = 0.45 + 0.55 * (0.5 + 0.5 * sin(time * 2.6 + l.phase + Float(i) * 0.4))
            batch.sprite(.glow, x: l.x, y: l.y, hw: 9, hh: 9, r: c.0, g: c.1, b: c.2, a: al * tw * 0.7, additive: true)
            batch.sprite(.dot, x: l.x, y: l.y, hw: 1.9, hh: 1.9, r: 1, g: 1, b: 0.9, a: al * (0.5 + 0.5 * tw), additive: true)
        }

        // Star.
        let sy = tiers.last.map { $0.apexY } ?? (H - h)
        let sr = h * 0.075
        let pulse = 0.75 + 0.25 * sin(time * 2)
        batch.sprite(.glow, x: cx, y: sy, hw: sr * 4, hh: sr * 4, r: 1, g: 0.85, b: 0.4, a: al * 0.55 * pulse, additive: true)
        let centre = batch.flat(cx, sy, 1, 0.95, 0.6, al)
        for k in 0..<10 {
            let a0 = Float(k) / 10 * 6.283 - .pi / 2
            let a1 = Float(k + 1) / 10 * 6.283 - .pi / 2
            let r0 = k % 2 == 0 ? sr : sr * 0.45
            let r1 = k % 2 == 0 ? sr * 0.45 : sr
            batch.tri(centre, batch.flat(cx + cos(a0) * r0, sy + sin(a0) * r0, 1, 0.78, 0.2, al),
                      batch.flat(cx + cos(a1) * r1, sy + sin(a1) * r1, 1, 0.78, 0.2, al))
        }

        // Presents.
        let boxes: [(Float, Float, Float, (Float, Float, Float), (Float, Float, Float))] = [
            (-0.46, 32, 24, (0.86, 0.17, 0.2), (1, 0.82, 0.3)),
            (0.30, 26, 31, (0.2, 0.42, 0.9), (0.95, 0.95, 1)),
            (-0.14, 22, 16, (0.15, 0.62, 0.32), (0.92, 0.2, 0.22)),
        ]
        for (dx, bw, bh, c, rib) in boxes {
            let x = cx + dx * w - bw / 2, y = H - bh
            batch.rect(x: x, y: y, w: bw, h: bh, r: c.0, g: c.1, b: c.2, a: al)
            batch.rect(x: x, y: y, w: bw, h: bh * 0.22, r: c.0 * 1.12, g: c.1 * 1.12, b: c.2 * 1.12, a: al)
            batch.rect(x: x + bw * 0.42, y: y, w: bw * 0.16, h: bh, r: rib.0, g: rib.1, b: rib.2, a: al)
            batch.rect(x: x, y: y + bh * 0.3, w: bw, h: bh * 0.14, r: rib.0, g: rib.1, b: rib.2, a: al)
            batch.sprite(.dot, x: x + bw * 0.38, y: y - 2, hw: 4, hh: 3, r: rib.0, g: rib.1, b: rib.2, a: al)
            batch.sprite(.dot, x: x + bw * 0.62, y: y - 2, hw: 4, hh: 3, r: rib.0, g: rib.1, b: rib.2, a: al)
        }
    }

    // MARK: - Frost

    func drawFrost(_ batch: Batch) {
        let s = min(W, H) * 0.3
        let hw = s / 2
        let a: Float = 0.8
        batch.sprite(.frost, x: hw, y: hw, hw: hw, hh: hw, r: 0.92, g: 0.96, b: 1, a: a)
        batch.sprite(.frost, x: W - hw, y: hw, hw: hw, hh: hw, r: 0.92, g: 0.96, b: 1, a: a, flipX: true)
        batch.sprite(.frost, x: hw, y: H - hw, hw: hw, hh: hw, r: 0.92, g: 0.96, b: 1, a: a, flipY: true)
        batch.sprite(.frost, x: W - hw, y: H - hw, hw: hw, hh: hw, r: 0.92, g: 0.96, b: 1, a: a, flipX: true, flipY: true)
    }

    // MARK: - Spring: a cherry tree in blossom

    private struct Twig { var x0: Float, y0: Float, x1: Float, y1: Float, width: Float }
    private struct Blossom { var x: Float, y: Float, r: Float, tint: Int, rot: Float }

    private lazy var sakura: (twigs: [Twig], clouds: [Blossom], flowers: [Blossom]) = layoutSakura()

    private func layoutSakura() -> (twigs: [Twig], clouds: [Blossom], flowers: [Blossom]) {
        var rng = Rng(seed: 31415)
        let h = treeH, cx = treeX
        var twigs: [Twig] = []
        var ends: [(Float, Float)] = []
        // A trunk that leans a little, then branches that split twice.
        let top = (cx - h * 0.04, H - h * 0.52)
        twigs.append(Twig(x0: cx, y0: H, x1: top.0, y1: top.1, width: h * 0.075))
        func branch(_ x: Float, _ y: Float, _ angle: Float, _ length: Float, _ width: Float, _ depth: Int) {
            let x2 = x + sin(angle) * length, y2 = y - cos(angle) * length
            twigs.append(Twig(x0: x, y0: y, x1: x2, y1: y2, width: width))
            if depth == 0 { ends.append((x2, y2)); return }
            for side in [-1.0, 1.0] as [Float] {
                branch(x2, y2, angle + side * rng.range(0.35, 0.7), length * rng.range(0.6, 0.75), width * 0.62, depth - 1)
            }
        }
        for a in [-0.75, -0.1, 0.6] as [Float] {
            branch(top.0, top.1, a + rng.range(-0.1, 0.1), h * rng.range(0.2, 0.26), h * 0.045, 2)
        }
        var clouds: [Blossom] = []
        var flowers: [Blossom] = []
        for (x, y) in ends {
            for _ in 0..<4 {
                clouds.append(Blossom(x: x + rng.range(-h * 0.08, h * 0.08), y: y + rng.range(-h * 0.06, h * 0.05),
                                      r: h * rng.range(0.07, 0.11), tint: rng.int(Palette.petals.count), rot: 0))
            }
            for _ in 0..<5 {
                flowers.append(Blossom(x: x + rng.range(-h * 0.11, h * 0.11), y: y + rng.range(-h * 0.08, h * 0.07),
                                       r: h * rng.range(0.022, 0.03), tint: rng.int(Palette.petals.count), rot: rng.range(0, 1.26)))
            }
        }
        return (twigs, clouds, flowers)
    }

    private func drawSakura(_ batch: Batch) {
        let al = treeAlpha
        guard al > 0.003 else { return }
        let t = sakura
        // A carpet of fallen petals under the tree.
        for k in 0..<22 {
            let u = Float(k) / 21 * 2 - 1
            let c = Palette.petals[k % Palette.petals.count]
            batch.sprite(.dot, x: treeX + u * treeW * 0.8, y: H - 2.5, hw: 9, hh: 3.5, r: c.0, g: c.1, b: c.2, a: al * 0.8)
        }
        for tw in t.twigs {
            batch.line(tw.x0, tw.y0, tw.x1, tw.y1, width: tw.width, r: 0.30, g: 0.18, b: 0.14, a: al)
            batch.line(tw.x0 - tw.width * 0.18, tw.y0, tw.x1 - tw.width * 0.18, tw.y1, width: tw.width * 0.35, r: 0.44, g: 0.29, b: 0.22, a: al)
        }
        for b in t.clouds {
            let c = Palette.petals[b.tint]
            batch.sprite(.dot, x: b.x, y: b.y, hw: b.r, hh: b.r * 0.9, r: c.0, g: c.1, b: c.2, a: al * 0.85)
        }
        for b in t.flowers {
            let c = Palette.petals[b.tint]
            for k in 0..<5 {
                let a = b.rot + Float(k) / 5 * 6.283
                batch.sprite(.petal, x: b.x + sin(a) * b.r * 0.7, y: b.y - cos(a) * b.r * 0.7, hw: b.r * 0.62, hh: b.r * 0.78,
                             rot: a, r: min(1, c.0 + 0.05), g: min(1, c.1 + 0.08), b: min(1, c.2 + 0.06), a: al)
            }
            batch.sprite(.dot, x: b.x, y: b.y, hw: b.r * 0.32, hh: b.r * 0.32, r: 0.95, g: 0.45, b: 0.55, a: al)
        }
    }

    // Petals blowing off the cherry tree.
    private struct FallingPetal { var x: Float, y: Float, vx: Float, vy: Float, rot: Float, spin: Float, flip: Float, tint: Int, life: Float }
    private var fallingPetals: [FallingPetal] = []
    private var petalClock: Float = 0

    private func stepFallingPetals(dt: Float) {
        if season == .spring && treeAlpha > 0.5 {
            petalClock += dt
            let clouds = sakura.clouds
            while petalClock > 0.35 && !clouds.isEmpty {
                petalClock -= 0.35
                let b = clouds[rng.int(clouds.count)]
                fallingPetals.append(FallingPetal(x: b.x + rng.range(-b.r, b.r), y: b.y + rng.range(-b.r, b.r) * 0.5,
                                                  vx: rng.range(-30, 10), vy: rng.range(8, 20), rot: rng.range(0, 6.28),
                                                  spin: rng.range(-3, 3), flip: rng.range(0, 6.28), tint: rng.int(Palette.petals.count), life: 0))
            }
        }
        var i = 0
        while i < fallingPetals.count {
            var p = fallingPetals[i]
            p.life += dt
            p.vx += (sin(time * 1.3 + p.flip) * 25 - p.vx) * min(1, dt * 1.5)
            p.x += p.vx * dt
            p.y += (p.vy + 10 * sin(time * 2 + p.flip)) * dt
            p.rot += p.spin * dt
            p.flip += 3 * dt
            if p.y > H + 10 || p.life > 30 {
                fallingPetals.swapAt(i, fallingPetals.count - 1)
                fallingPetals.removeLast()
                continue
            }
            fallingPetals[i] = p
            i += 1
        }
    }

    private func drawFallingPetals(_ batch: Batch) {
        for p in fallingPetals {
            let c = Palette.petals[p.tint]
            batch.sprite(.petal, x: p.x, y: p.y, hw: 4 * max(0.22, abs(cos(p.flip))), hh: 4.6, rot: p.rot, r: c.0, g: c.1, b: c.2, a: 0.95)
        }
    }

    // MARK: - Summer: sunflowers

    private func drawSunflowers(_ batch: Batch) {
        let al = treeAlpha
        guard al > 0.003 else { return }
        let h = treeH, w = treeW
        // Grass.
        for k in 0..<40 {
            let u = Float(k) / 39 * 2 - 1
            let x = treeX + u * w * 0.85
            let len = 8 + Float((k * 37) % 11)
            let lean = sin(time * 1.1 + Float(k)) * 1.5 + Float(k % 3 - 1) * 2
            batch.line(x, H, x + lean, H - len, width: 1.6, r: 0.24, g: 0.56, b: 0.20, a: al)
        }
        let spots: [(Float, Float)] = [(-0.38, 0.78), (0.02, 1.0), (0.36, 0.62)]
        for (i, (dx, tall)) in spots.enumerated() {
            let sway = sin(time * 0.8 + Float(i) * 1.7) * 0.05
            let baseX = treeX + dx * w
            let len = h * tall
            let hx = baseX + sin(sway) * len, hy = H - cos(sway) * len
            batch.line(baseX, H, hx, hy, width: 4, r: 0.22, g: 0.50, b: 0.18, a: al)
            for (k, at) in [0.35, 0.62].enumerated() {
                let side: Float = (k + i) % 2 == 0 ? 1 : -1
                let lx = baseX + (hx - baseX) * Float(at), ly = H + (hy - H) * Float(at)
                batch.sprite(.leaf, x: lx + side * 11, y: ly - 4, hw: 9, hh: 13, rot: side * 1.0 + sway,
                             r: 0.30, g: 0.62, b: 0.22, a: al)
            }
            let r = h * (0.085 + 0.02 * tall)
            for ring in 0..<2 {
                let petals = 16
                for k in 0..<petals {
                    let a = Float(k) / Float(petals) * 6.283 + Float(ring) * 0.2 + sway
                    let rr = r * (ring == 0 ? 1.05 : 0.9)
                    let shade: Float = ring == 0 ? 0.88 : 1
                    batch.sprite(.petal, x: hx + sin(a) * rr, y: hy - cos(a) * rr, hw: r * 0.26, hh: r * 0.55, rot: a,
                                 r: 1.0 * shade, g: 0.78 * shade, b: 0.14 * shade, a: al)
                }
            }
            batch.sprite(.ballBase, x: hx, y: hy, hw: r * 0.62, hh: r * 0.62, r: 0.42, g: 0.25, b: 0.09, a: al)
            batch.sprite(.ballTex, x: hx, y: hy, hw: r * 0.58, hh: r * 0.58, rot: Float(i), r: 0.2, g: 0.1, b: 0.03, a: al * 0.9)
        }
    }

    // MARK: - Autumn: pumpkins with a candle inside

    private func drawPumpkin(_ batch: Batch, x: Float, r: Float, face: Bool, al: Float) {
        let y = H - r * 0.82
        let orange: (Float, Float, Float) = (1.0, 0.52, 0.10)
        // Ribs, the outer ones darker and behind.
        for (dx, wk, shade) in [(-0.55, 0.62, 0.78), (0.55, 0.62, 0.78), (-0.28, 0.7, 0.9), (0.28, 0.7, 0.9), (0, 0.66, 1.0)] as [(Float, Float, Float)] {
            batch.sprite(.ballBase, x: x + dx * r, y: y, hw: r * wk, hh: r * 0.86,
                         r: orange.0 * shade, g: orange.1 * shade, b: orange.2 * shade, a: al)
        }
        // Stem and a curl of vine.
        batch.rect(x: x - r * 0.08, y: y - r * 1.06, w: r * 0.16, h: r * 0.26, r: 0.36, g: 0.30, b: 0.12, a: al)
        batch.line(x + r * 0.05, y - r * 0.95, x + r * 0.35, y - r * 1.12, width: 1.8, r: 0.30, g: 0.45, b: 0.16, a: al)
        guard face else { return }
        let flicker = 0.78 + 0.22 * sin(time * 9.0) * sin(time * 5.3 + 1)
        let lit: (Float, Float, Float) = (1.0, 0.82 * flicker + 0.1, 0.28 * flicker)
        func tri(_ ax: Float, _ ay: Float, _ bx: Float, _ by: Float, _ cx: Float, _ cy: Float) {
            batch.tri(batch.flat(x + ax * r, y + ay * r, lit.0, lit.1, lit.2, al),
                      batch.flat(x + bx * r, y + by * r, lit.0, lit.1, lit.2, al),
                      batch.flat(x + cx * r, y + cy * r, lit.0, lit.1, lit.2, al))
        }
        tri(-0.42, -0.08, -0.16, -0.08, -0.29, -0.34)    // eyes
        tri(0.16, -0.08, 0.42, -0.08, 0.29, -0.34)
        tri(-0.07, 0.08, 0.07, 0.08, 0, -0.06)           // nose
        // A jagged grin.
        let mouth: [(Float, Float)] = [(-0.5, 0.2), (-0.32, 0.36), (-0.2, 0.26), (-0.08, 0.4), (0.08, 0.28), (0.2, 0.4), (0.34, 0.27), (0.5, 0.2)]
        for k in 0..<(mouth.count - 1) {
            let a = mouth[k], b = mouth[k + 1]
            tri(a.0, a.1, b.0, b.1, (a.0 + b.0) / 2, 0.18 + (k % 2 == 0 ? 0.02 : 0.0))
        }
        let glow: Float = evening ? 0.6 : 0.35
        batch.sprite(.glow, x: x, y: y + r * 0.05, hw: r * 2.2, hh: r * 1.8, r: 1, g: 0.6, b: 0.2, a: al * glow * flicker, additive: true)
    }

    private func drawPumpkins(_ batch: Batch) {
        let al = treeAlpha
        guard al > 0.003 else { return }
        let h = treeH, w = treeW
        // A few leaves around them.
        for k in 0..<14 {
            let u = Float(k) / 13 * 2 - 1
            let c = Palette.leaves[k % Palette.leaves.count]
            batch.sprite(k % 2 == 0 ? .maple : .leaf, x: treeX + u * w * 0.85, y: H - 4, hw: 7, hh: 8, rot: Float(k) * 1.9,
                         r: c.0, g: c.1, b: c.2, a: al)
        }
        drawPumpkin(batch, x: treeX + w * 0.22, r: h * 0.12, face: false, al: al)
        drawPumpkin(batch, x: treeX - w * 0.2, r: h * 0.19, face: true, al: al)
    }

    // MARK: - Butterflies (spring and summer days)

    private struct Butterfly { var x: Float, baseY: Float, y: Float, vx: Float, phase: Float, tint: Int, size: Float }
    private static let wingColors: [(Float, Float, Float)] = [(1.0, 0.55, 0.12), (0.36, 0.62, 1.0), (1.0, 0.88, 0.30), (1.0, 1.0, 1.0), (0.85, 0.45, 0.95)]
    private var butterflies: [Butterfly] = []
    private var nextButterfly: Float = 2

    private func stepButterflies(dt: Float, cursorX: Float?, cursorY: Float) {
        let welcome = (season == .spring || season == .summer) && !evening
        if welcome && time > nextButterfly && butterflies.count < 3 {
            let fromLeft = rng.unit() < 0.5
            butterflies.append(Butterfly(x: fromLeft ? -20 : W + 20, baseY: rng.range(H * 0.25, H * 0.75), y: 0,
                                         vx: (fromLeft ? 1 : -1) * rng.range(45, 75), phase: rng.range(0, 6.28),
                                         tint: rng.int(Festive.wingColors.count), size: rng.range(17, 24)))
            nextButterfly = time + rng.range(6, 15)
        }
        var i = 0
        while i < butterflies.count {
            var b = butterflies[i]
            b.x += b.vx * dt
            b.y = b.baseY + 34 * sin(time * 1.2 + b.phase) + 10 * sin(time * 3.3 + b.phase * 2)
            // Shy: it flutters up and away from the cursor.
            if let cx = cursorX {
                let dx = b.x - cx, dy = b.y - cursorY
                if dx * dx + dy * dy < 120 * 120 {
                    b.baseY -= 140 * dt
                    b.x += (dx > 0 ? 1 : -1) * 60 * dt
                }
            }
            if b.x < -40 || b.x > W + 40 || b.baseY < -60 {
                butterflies.swapAt(i, butterflies.count - 1)
                butterflies.removeLast()
                continue
            }
            butterflies[i] = b
            i += 1
        }
    }

    private func drawButterflies(_ batch: Batch) {
        for b in butterflies {
            let c = Festive.wingColors[b.tint]
            let flap = abs(sin(time * 13 + b.phase))
            let hw = b.size * (0.2 + 0.8 * flap) * 0.5
            let tilt: Float = b.vx > 0 ? 0.12 : -0.12
            batch.sprite(.wing, x: b.x + hw, y: b.y, hw: hw, hh: b.size * 0.5, rot: tilt, r: c.0, g: c.1, b: c.2, a: 1)
            batch.sprite(.wing, x: b.x - hw, y: b.y, hw: hw, hh: b.size * 0.5, rot: tilt, r: c.0, g: c.1, b: c.2, a: 1, flipX: true)
            batch.sprite(.coal, x: b.x, y: b.y + 0.5, hw: b.size * 0.07, hh: b.size * 0.32)
            batch.line(b.x, b.y - b.size * 0.25, b.x - 2.5, b.y - b.size * 0.55, width: 0.8, r: 0.15, g: 0.12, b: 0.1, a: 0.9)
            batch.line(b.x, b.y - b.size * 0.25, b.x + 2.5, b.y - b.size * 0.55, width: 0.8, r: 0.15, g: 0.12, b: 0.1, a: 0.9)
        }
    }

    // MARK: - Fireflies (summer evenings)

    private struct Firefly { var x: Float, y: Float, vx: Float, vy: Float, phase: Float, speed: Float }
    private var fireflies: [Firefly] = []

    private func stepFireflies(dt: Float, cursorX: Float?, cursorY: Float) {
        let wanted = season == .summer && evening ? max(12, Int(W * H / 45_000)) : 0
        if fireflies.count < wanted && rng.unit() < dt * 3 {
            fireflies.append(Firefly(x: rng.range(0, W), y: rng.range(H * 0.35, H * 0.95), vx: 0, vy: 0,
                                     phase: rng.range(0, 6.28), speed: rng.range(1.2, 2.6)))
        } else if fireflies.count > wanted && rng.unit() < dt * 2 {
            fireflies.removeLast()
        }
        for i in fireflies.indices {
            var f = fireflies[i]
            f.vx += sin(time * 0.9 + f.phase * 3) * 40 * dt
            f.vy += cos(time * 0.7 + f.phase * 2) * 32 * dt
            // Curious: they drift towards the cursor and circle it.
            if let cx = cursorX {
                let dx = cx - f.x, dy = cursorY - f.y
                let d = max(1, (dx * dx + dy * dy).squareRoot())
                if d < 280 {
                    let pull: Float = d > 50 ? 26 : -20
                    f.vx += dx / d * pull * dt
                    f.vy += dy / d * pull * dt
                }
            }
            f.vx *= max(0, 1 - dt * 0.8)
            f.vy *= max(0, 1 - dt * 0.8)
            f.x += f.vx * dt
            f.y += f.vy * dt
            if f.x < 0 { f.vx += 20 * dt } else if f.x > W { f.vx -= 20 * dt }
            if f.y < H * 0.2 { f.vy += 20 * dt } else if f.y > H - 10 { f.vy -= 20 * dt }
            fireflies[i] = f
        }
    }

    private func drawFireflies(_ batch: Batch) {
        for f in fireflies {
            // They fly out in the dark, behind the windows.
            var behind = false
            for o in occluders where o.contains(f.x, f.y) { behind = true; break }
            if behind { continue }
            let s = max(0, sin(time * f.speed + f.phase))
            let glow = 0.12 + 0.88 * s * s
            batch.sprite(.glow, x: f.x, y: f.y, hw: 26, hh: 26, r: 0.72, g: 1.0, b: 0.30, a: glow, additive: true)
            batch.sprite(.glow, x: f.x, y: f.y, hw: 8, hh: 8, r: 0.9, g: 1.0, b: 0.5, a: glow, additive: true)
            batch.sprite(.dot, x: f.x, y: f.y, hw: 2.4, hh: 2.4, r: 1, g: 1, b: 0.75, a: 0.3 + 0.7 * glow)
        }
    }
}
