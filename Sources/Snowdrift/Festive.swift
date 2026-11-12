import Foundation

/// New Year extras for one screen: fireworks, a Christmas tree and frosty corners.
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

    func step(dt: Float, showTree: Bool, occluders: [FRect]) {
        time += dt
        stepFireworks(dt: dt)
        var free = true
        for o in occluders where o.x0 < treeBox.x1 && o.x1 > treeBox.x0 && o.y0 < treeBox.y1 && o.y1 > treeBox.y0 {
            free = false
            break
        }
        let target: Float = showTree && free ? 1 : 0
        treeAlpha += (target - treeAlpha) * min(1, dt * 5)
        if abs(target - treeAlpha) < 0.003 { treeAlpha = target }
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
}
