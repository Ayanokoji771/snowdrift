import Foundation

/// Width of one snow column, in points.
let colW: Float = 2

struct FRect {
    var x0: Float, y0: Float, x1: Float, y1: Float
    @inline(__always) func contains(_ x: Float, _ y: Float) -> Bool { x >= x0 && x < x1 && y >= y0 && y < y1 }
}

struct SimConfig: Equatable {
    var intensity = 1
    var wind = 1
    var drifts = 2
    var tool = 0
    var garland = 2
    var depth = true
    var lowPower = false
    var flakeSize = 1
    var melt = false
    var tree = true
    var frost = false
    var sparkle = true
    /// Season.rawValue: 0 winter, 1 spring, 2 summer, 3 autumn.
    var season = 0
    /// Evening or night (fireflies).
    var evening = false
}

struct Flake {
    var x: Float, y: Float, vx: Float
    var size: Float, alpha: Float
    var layer: UInt8          // 0 far, 1 middle, 2 near
    var kind: UInt8           // 0 soft dot, 1...3 crystal, 4 petal, 5 leaf, 6 maple leaf, 7 dandelion fluff
    var phase: Float, swayAmp: Float, swayFreq: Float
    var rot: Float, spin: Float
    var fall: Float
    var vol: Float
    var tint: UInt8 = 0
    /// Tumbling: the item turns edge-on and back.
    var flip: Float = 0, flipSpeed: Float = 0
}

struct Debris {
    var x: Float, y: Float, vx: Float, vy: Float
    var size: Float, vol: Float
    var canLand: Bool
    /// 0 snow, 1 petal, 2 leaf.
    var kind: UInt8 = 0
    var tint: UInt8 = 0
    var rot: Float = 0, spin: Float = 0
}

/// Colours of the seasons.
enum Palette {
    static let petals: [(Float, Float, Float)] = [(1.0, 0.80, 0.87), (1.0, 0.70, 0.81), (0.99, 0.89, 0.93), (0.98, 0.62, 0.76)]
    static let leaves: [(Float, Float, Float)] = [
        (0.96, 0.56, 0.12), (0.88, 0.30, 0.10), (0.98, 0.76, 0.18), (0.72, 0.20, 0.08), (0.84, 0.46, 0.14), (0.62, 0.40, 0.12),
    ]
}

/// Something snow can lie on: the top edge of a window, the Dock, or the bottom of the screen.
final class Surface {
    enum Kind { case window, dock, floor }
    let id: Int
    let kind: Kind
    var x0: Float
    var y: Float
    var h: [Float]
    var vis: [Bool]
    var cap: Float = 0
    var onScreen = true
    var spillL: Float = 0
    var spillR: Float = 0

    init(id: Int, kind: Kind, x0: Float, y: Float, n: Int) {
        self.id = id
        self.kind = kind
        self.x0 = x0
        self.y = y
        h = [Float](repeating: 0, count: n)
        vis = [Bool](repeating: true, count: n)
    }

    var x1: Float { x0 + Float(h.count) * colW }
}

final class Ball {
    var x: Float, y: Float
    var vx: Float = 0, vy: Float = 0
    var area: Float { didSet { r = (area / .pi).squareRoot() } }
    private(set) var r: Float
    var angle: Float = 0
    var support: Int?
    var relX: Float = 0
    var dormant = false
    var grabbed = false
    var hidden = false
    var style: UInt32
    weak var below: Ball?
    var above: Ball?

    init(x: Float, y: Float, area: Float, style: UInt32) {
        self.x = x
        self.y = y
        self.area = area
        r = (area / .pi).squareRoot()
        self.style = style
    }

    var root: Ball {
        var b = self
        while let n = b.below { b = n }
        return b
    }

    var top: Ball {
        var b = self
        while let n = b.above { b = n }
        return b
    }

    /// This ball and everything stacked on it.
    var chain: [Ball] {
        var out = [self]
        var b = self
        while let n = b.above {
            out.append(n)
            b = n
        }
        return out
    }
}

/// The snow on one screen. Coordinates are points, origin top-left, y down.
final class Sim {
    private(set) var W: Float
    private(set) var H: Float
    /// Bottom of the menu bar.
    private(set) var topInset: Float

    var config = SimConfig() {
        didSet {
            if config.season != oldValue.season {
                // A new season: what lies around blows away, snowmen melt, the new season starts falling.
                clearSnow(puff: true, kind: Sim.debrisKind(oldValue.season))
                clearBalls()
                needsWindowRefresh = true
            }
            if config.drifts != oldValue.drifts {
                if config.drifts == 0 { clearSnow(puff: false) }
                needsWindowRefresh = true
            }
        }
    }

    var season: Season { Season(rawValue: config.season) ?? .winter }

    /// What the mitten and the wind blow around: snow, petals or leaves.
    private static func debrisKind(_ season: Int) -> UInt8 { season == 1 ? 1 : (season == 3 ? 2 : 0) }
    var needsWindowRefresh = true

    private var rng = Rng()
    private(set) var time: Float = 0
    private var wind: Float = 0

    private var flakes: [Flake] = []
    private var debris: [Debris] = []
    private var surfaces: [Int: Surface] = [:]
    private var active: [Surface] = []
    private var occluders: [FRect] = []
    private(set) var balls: [Ball] = []

    // Cursor.
    private var cursorX: Float = -1000, cursorY: Float = -1000
    private var cursorVX: Float = 0, cursorVY: Float = 0
    private var cursorValid = false
    private var carveAcc: Float = 0
    private var pendingBall: Float = 0
    private weak var activeBall: Ball?
    private(set) weak var hover: Ball?
    private(set) weak var grabbedBall: Ball?
    private var grabDX: Float = 0, grabDY: Float = 0

    private var landed = 0
    let festive: Festive

    // Garland.
    private struct Bulb { var x: Float, y: Float, color: Int, phase: Float, speed: Float }
    private var bulbs: [Bulb] = []
    private var wire: [(Float, Float)] = []

    static let maxBallRadius: Float = 34
    private static let floorID = -1
    private static let dockID = -2
    private static let edgeInset: Float = 12

    init(width: Float, height: Float, topInset: Float) {
        W = width
        H = height
        self.topInset = topInset
        festive = Festive(width: width, height: height)
        buildGarland()
        let floor = Surface(id: Sim.floorID, kind: .floor, x0: 0, y: H, n: Int(W / colW))
        surfaces[floor.id] = floor
        active = [floor]
    }

    // MARK: - Settings-derived numbers

    /// Snow piles high; petals lie in a thin layer, leaves in heaps; summer fluff doesn't settle.
    private var driftCap: Float { [0, 13, 22, 36][config.drifts] * [1, 0.4, 0, 0.62][config.season] }

    private var targetCount: Int {
        let base: Float = [150, 430, 1150][config.intensity]
        let area = (W * H) / (1512 * 982)
        let perSeason: Float = [1, 0.34, 0.13, 0.15][config.season]
        return max(4, Int(base * area * perSeason * (config.lowPower ? 0.6 : 1)))
    }

    private var speedFactor: Float { [0.85, 1.0, 1.45][config.intensity] }
    private var depositGain: Float { [1.35, 1.0, 0.8][config.intensity] }

    // MARK: - Windows

    /// Updates the surfaces from the current window layout. `originX/Y` is this screen's top-left
    /// corner in global coordinates. `existing` tells which window ids are still alive somewhere.
    func applyWindows(_ snap: WindowSnapshot, originX: Float, originY: Float, elapsed: Float,
                      existing: (Set<Int>) -> Set<Int>) {
        needsWindowRefresh = false
        // Real time since the last update; a big value means "don't shake snow off moved windows".
        let pollDt = max(1.0 / 120, elapsed)

        var wins: [(id: Int, r: FRect)] = []
        wins.reserveCapacity(snap.windows.count)
        for w in snap.windows {
            let r = FRect(x0: Float(w.rect.minX) - originX, y0: Float(w.rect.minY) - originY,
                          x1: Float(w.rect.maxX) - originX, y1: Float(w.rect.maxY) - originY)
            if r.x1 <= 0 || r.x0 >= W || r.y1 <= 0 || r.y0 >= H { continue }
            wins.append((w.id, r))
        }
        var dock: FRect?
        if let d = snap.dock {
            let r = FRect(x0: Float(d.minX) - originX, y0: Float(d.minY) - originY,
                          x1: Float(d.maxX) - originX, y1: Float(d.maxY) - originY)
            if r.x1 > 0 && r.x0 < W && r.y1 > 0 && r.y0 < H { dock = r }
        }
        occluders = wins.map { $0.r }
        if let dock { occluders.append(dock) }

        let cap = driftCap
        var seen = Set<Int>()

        for (zi, w) in wins.enumerated() {
            let y = w.r.y0
            let x0 = w.r.x0 + Sim.edgeInset, x1 = w.r.x1 - Sim.edgeInset
            guard y > topInset + 3, y < H - 6, x1 - x0 >= 40 else { continue }
            let s = upsert(id: w.id, kind: .window, x0: x0, y: y, n: Int((x1 - x0) / colW), pollDt: pollDt)
            seen.insert(w.id)
            s.cap = max(0, min(cap, y - topInset - 2))
            resetVisibility(s)
            // Hide the parts of the edge that sit behind a window in front, or behind the Dock.
            for f in wins[0..<zi] where f.r.y0 <= y - 2 && f.r.y1 > y - 2 { hide(s, f.r.x0 - 1, f.r.x1 + 1) }
            if let dock, dock.y0 <= y - 2, dock.y1 > y - 2 { hide(s, dock.x0, dock.x1) }
        }

        // Windows that vanished: closed ones drop their snow, hidden ones keep it for later.
        var missing = Set<Int>()
        var dormant = Set<Int>()
        for (id, s) in surfaces where s.kind == .window {
            if s.onScreen && !seen.contains(id) { missing.insert(id) }
            if !s.onScreen && !seen.contains(id) { dormant.insert(id) }
        }
        if !missing.isEmpty {
            let alive = existing(missing.union(dormant))
            for id in missing {
                guard let s = surfaces[id] else { continue }
                if alive.contains(id) {
                    s.onScreen = false
                    for b in balls where b.support == id { b.dormant = true }
                } else {
                    avalanche(s, from: 0, to: s.h.count)
                    surfaces[id] = nil
                    for b in balls where b.support == id { b.support = nil }
                }
            }
            for id in dormant where !alive.contains(id) {
                surfaces[id] = nil
                balls.removeAll { $0.root.support == id && $0.root.dormant }
            }
        }

        // Dock.
        if let dock, dock.x1 - dock.x0 > 60, dock.y0 > topInset + 20 {
            let x0 = dock.x0 + 10, x1 = dock.x1 - 10
            let s = upsert(id: Sim.dockID, kind: .dock, x0: x0, y: dock.y0 + 1, n: Int((x1 - x0) / colW), pollDt: pollDt)
            s.cap = min(cap, 18)
            resetVisibility(s)
        } else if let s = surfaces[Sim.dockID], s.onScreen {
            // The Dock hid (full-screen app, game): keep its snow for when it comes back.
            s.onScreen = false
            for b in balls where b.root.support == Sim.dockID { b.root.dormant = true }
        }

        // Floor.
        if let floor = surfaces[Sim.floorID] {
            floor.cap = cap * 1.3
            resetVisibility(floor)
            if let dock, dock.y1 >= H - 2 { hide(floor, dock.x0, dock.x1) }
        }

        active = surfaces.values.filter { $0.onScreen }.sorted { $0.y < $1.y }
    }

    private func resetVisibility(_ s: Surface) {
        let n = s.h.count
        if s.vis.count != n { s.vis = [Bool](repeating: true, count: n) }
        for i in 0..<n {
            let x = s.x0 + (Float(i) + 0.5) * colW
            s.vis[i] = x >= 0 && x < W
        }
    }

    private func hide(_ s: Surface, _ xa: Float, _ xb: Float) {
        let n = s.h.count
        let a = max(0, Int(((xa - s.x0) / colW).rounded(.down)))
        let b = min(n, Int(((xb - s.x0) / colW).rounded(.up)))
        if a < b { for i in a..<b { s.vis[i] = false } }
    }

    private func upsert(id: Int, kind: Surface.Kind, x0: Float, y: Float, n: Int, pollDt: Float) -> Surface {
        guard let s = surfaces[id] else {
            let s = Surface(id: id, kind: kind, x0: x0, y: y, n: n)
            surfaces[id] = s
            return s
        }
        let wasOn = s.onScreen
        var resized = false
        if s.h.count != n {
            resized = true
            let oldN = s.h.count
            let anchorRight = abs((x0 + Float(n) * colW) - s.x1) < 2.5 && abs(x0 - s.x0) > 1
            var nh = [Float](repeating: 0, count: n)
            let keep = min(n, oldN)
            if anchorRight {
                for i in 0..<keep { nh[n - 1 - i] = s.h[oldN - 1 - i] }
                if wasOn && oldN > n { avalanche(s, from: 0, to: oldN - n) }
            } else {
                for i in 0..<keep { nh[i] = s.h[i] }
                if wasOn && oldN > n { avalanche(s, from: n, to: oldN) }
            }
            s.h = nh
            s.vis = [Bool](repeating: true, count: n)
        }
        let dx = x0 - s.x0, dy = y - s.y
        if wasOn && !resized && (dx != 0 || dy != 0) {
            // A window yanked fast leaves part of its snow hanging in the air.
            let speed = (dx * dx + dy * dy).squareRoot() / pollDt
            if speed > 1500 {
                let frac = min(0.6, 0.1 + (speed - 1500) / 5000)
                var acc: Float = 0
                for i in 0..<s.h.count where s.vis[i] && s.h[i] > 0.5 {
                    let take = s.h[i] * frac
                    s.h[i] -= take
                    acc += take * colW
                    if acc >= 8 {
                        emit(x: s.x0 + Float(i) * colW, y: s.y - s.h[i] - 1, vx: rng.range(-20, 20), vy: rng.range(-30, 20), vol: acc)
                        acc = 0
                    }
                }
            }
            for b in balls where b.support == id && b.below == nil && !b.grabbed {
                b.x += dx
                b.y += dy
            }
        }
        s.x0 = x0
        s.y = y
        s.onScreen = true
        if !wasOn {
            for b in balls where b.support == id && b.dormant {
                b.dormant = false
                b.x = x0 + b.relX
                b.y = y - b.r - heightAt(s, b.x)
            }
        }
        return s
    }

    private func heightAt(_ s: Surface, _ x: Float) -> Float {
        let c = Int((x - s.x0) / colW)
        return c >= 0 && c < s.h.count ? s.h[c] : 0
    }

    /// Turns the snow of some columns into falling clumps.
    private func avalanche(_ s: Surface, from: Int, to: Int) {
        guard s.onScreen else { return }
        var acc: Float = 0
        for i in from..<to where i < s.vis.count && s.vis[i] && s.h[i] > 0.3 {
            acc += s.h[i] * colW
            if acc >= 9 {
                emit(x: s.x0 + Float(i) * colW, y: s.y - s.h[i] * rng.unit(), vx: rng.range(-25, 25), vy: rng.range(-20, 40), vol: acc)
                acc = 0
            }
        }
    }

    // MARK: - Public actions

    var peekLanded: Int { landed }

    /// A puff of snow breathed out at (x, y): the flakes drift down and settle like any other.
    func puff(x: Float, y: Float, count: Int) {
        for _ in 0..<count {
            var f = makeFlake(anywhere: false)
            if f.layer == 0 {
                f.layer = 1
                f.size = max(f.size, 1.5)
                f.alpha = max(f.alpha, 0.8)
                f.vol = 12
            }
            f.x = x + rng.range(-70, 70)
            f.y = y + rng.range(-4, 14)
            f.vx = rng.range(-110, 110)
            flakes.append(f)
        }
    }

    /// Snowflakes that settled since the last call.
    func takeLanded() -> Int {
        defer { landed = 0 }
        return landed
    }

    /// Thaw: drifts sink, snowballs shrink and finally melt away.
    private func melt(dt: Float) {
        for s in active {
            s.h.withUnsafeMutableBufferPointer { h in
                for i in h.indices where h[i] > 0 {
                    h[i] = max(0, h[i] - dt * (0.12 + h[i] * 0.012))
                }
            }
        }
        var gone: [Ball] = []
        for b in balls where !b.grabbed && !b.dormant {
            b.area *= max(0, 1 - dt * 0.012)
            if b.r < 4.5 { gone.append(b) }
        }
        for b in gone {
            if let up = b.above {
                up.below = nil
                b.above = nil
            }
            if let down = b.below {
                down.above = nil
                b.below = nil
            }
            balls.removeAll { $0 === b }
        }
    }

    /// The shovel: all snow puffs up and is gone.
    func clearSnow(puff: Bool = true, kind: UInt8? = nil) {
        for s in surfaces.values {
            if puff && s.onScreen {
                var acc: Float = 0
                for i in 0..<s.h.count where s.vis[i] && s.h[i] > 0.3 {
                    acc += s.h[i] * colW
                    if acc >= 14 {
                        emit(x: s.x0 + Float(i) * colW, y: s.y - s.h[i] * rng.unit(), vx: rng.range(-90, 90),
                             vy: rng.range(-190, -40), vol: 8, canLand: false, kind: kind)
                        acc = 0
                    }
                }
            }
            for i in 0..<s.h.count { s.h[i] = 0 }
        }
    }

    func clearBalls() {
        for b in balls where b.below == nil && !b.dormant { burst(b, remove: false) }
        balls.removeAll()
        hover = nil
        grabbedBall = nil
        activeBall = nil
    }

    var hasBalls: Bool { !balls.isEmpty }

    func grabHover() {
        guard let b = hover, grabbedBall == nil else { return }
        if let below = b.below {
            below.above = nil
            b.below = nil
        }
        b.grabbed = true
        b.support = nil
        b.vx = 0
        b.vy = 0
        grabDX = b.x - cursorX
        grabDY = b.y - cursorY
        grabbedBall = b
    }

    func releaseGrab() {
        guard let b = grabbedBall else { return }
        b.grabbed = false
        grabbedBall = nil
        let speed = (b.vx * b.vx + b.vy * b.vy).squareRoot()
        if speed > 1500 {
            b.vx *= 1500 / speed
            b.vy *= 1500 / speed
        }
        _ = tryStack(b)
    }

    // MARK: - Step

    func step(dt: Float, cursorX cx: Float, cursorY cy: Float, cursorInside: Bool) {
        time += dt
        let gust = (25 * sin(time * 0.37) + 15 * sin(time * 0.91 + 2)) * [0.6, 1.0, 2.2][config.intensity]
        wind = [-170, -70, 0, 70, 170][config.wind + 2] * [0.8, 1.0, 1.5][config.intensity] + gust

        // Cursor motion.
        let hadCursor = cursorValid
        let px = cursorX, py = cursorY
        cursorValid = cursorInside
        cursorX = cx
        cursorY = cy
        if hadCursor && cursorInside {
            let k = min(1, dt * 18)
            cursorVX += ((cx - px) / dt - cursorVX) * k
            cursorVY += ((cy - py) / dt - cursorVY) * k
        } else {
            cursorVX = 0
            cursorVY = 0
        }
        let cursorSpeed = (cursorVX * cursorVX + cursorVY * cursorVY).squareRoot()

        if hadCursor && cursorInside && config.tool != 2 && grabbedBall == nil {
            let dist = ((cx - px) * (cx - px) + (cy - py) * (cy - py)).squareRoot()
            if dist > 0.2 {
                let steps = min(24, max(1, Int(dist / 6) + 1))
                for i in 1...steps {
                    let t = Float(i) / Float(steps)
                    carve(cx: px + (cx - px) * t, cy: py + (cy - py) * t, radius: 13)
                }
            }
        }
        if cursorSpeed < 5 { pendingBall *= max(0, 1 - dt * 1.5) }

        stepFlakes(dt: dt, cursorSpeed: cursorSpeed)
        stepDebris(dt: dt)
        relax()
        stepBalls(dt: dt, cursorSpeed: cursorSpeed)
        festive.step(dt: dt, season: season, showDecor: config.tree, evening: config.evening,
                     cursorX: cursorInside ? cx : nil, cursorY: cy, occluders: occluders)
        if config.melt { melt(dt: dt) }

        // What is under the cursor.
        hover = nil
        if cursorInside && grabbedBall == nil {
            for b in balls where !b.root.hidden && !b.root.dormant {
                let dx = b.x - cx, dy = b.y - cy
                if dx * dx + dy * dy < (b.r + 2) * (b.r + 2) { hover = b }
            }
        }
    }

    private func makeFlake(anywhere: Bool) -> Flake {
        let pick = rng.unit()
        let layer: UInt8 = pick < 0.30 ? 0 : (pick < 0.76 ? 1 : 2)
        let scale: Float = [0.72, 1.0, 1.4][config.flakeSize]
        var f = Flake(x: rng.range(-40, W + 40), y: anywhere ? rng.range(-20, H) : -rng.range(6, 60), vx: wind * 0.5,
                      size: 1, alpha: 1, layer: layer, kind: 0,
                      phase: rng.range(0, 6.28), swayAmp: rng.range(6, 22), swayFreq: rng.range(0.6, 1.8),
                      rot: rng.range(0, 6.28), spin: rng.range(-1.2, 1.2), fall: 60, vol: 0)
        // Per layer: (size range, alpha range, fall range, volume) — far, middle, near.
        func pickLayer(_ sizes: [(Float, Float)], _ alphas: [(Float, Float)], _ falls: [(Float, Float)], _ vols: [Float]) {
            let l = Int(layer)
            f.size = rng.range(sizes[l].0, sizes[l].1) * scale
            f.alpha = rng.range(alphas[l].0, alphas[l].1)
            f.fall = rng.range(falls[l].0, falls[l].1)
            f.vol = vols[l]
        }
        switch season {
        case .winter:
            pickLayer([(0.7, 1.25), (1.3, 2.3), (2.3, 4.4)], [(0.35, 0.6), (0.7, 0.95), (0.85, 1)], [(28, 48), (50, 85), (85, 135)], [0, 12, 14])
            if layer == 2 && f.size > 3.0 * scale { f.kind = UInt8(1 + rng.int(3)) }
        case .spring:
            pickLayer([(2.0, 2.8), (3.2, 4.6), (4.8, 6.6)], [(0.45, 0.65), (0.85, 0.95), (0.95, 1)], [(16, 24), (24, 38), (34, 50)], [0, 5, 6])
            f.kind = 4
            f.tint = UInt8(rng.int(Palette.petals.count))
            f.swayAmp = rng.range(14, 38)
            f.swayFreq = rng.range(0.5, 1.3)
            f.spin = rng.range(-2.4, 2.4)
            f.flipSpeed = rng.range(1.5, 4)
        case .summer:
            pickLayer([(3.4, 4.6), (5.4, 7.2), (8.0, 10.5)], [(0.45, 0.6), (0.75, 0.9), (0.9, 1)], [(8, 14), (12, 20), (16, 26)], [0, 0, 0])
            f.kind = 7
            f.swayAmp = rng.range(18, 44)
            f.swayFreq = rng.range(0.25, 0.7)
            f.spin = rng.range(-0.25, 0.25)
            f.rot = rng.range(-0.3, 0.3)
        case .autumn:
            pickLayer([(3.6, 5.0), (5.6, 8.0), (8.6, 11.5)], [(0.5, 0.7), (0.9, 1), (1, 1)], [(22, 32), (32, 50), (46, 68)], [0, 22, 26])
            f.kind = rng.unit() < 0.5 ? 5 : 6
            f.tint = UInt8(rng.int(Palette.leaves.count))
            f.swayAmp = rng.range(30, 70)
            f.swayFreq = rng.range(0.6, 1.25)
            f.spin = rng.range(-0.6, 0.6)
            f.flipSpeed = rng.range(0.8, 2.6)
        }
        if !anywhere && season == .summer && rng.unit() < 0.4 {
            // Fluff also drifts in from the sides.
            f.x = wind >= 0 ? -30 : W + 30
            f.y = rng.range(H * 0.1, H * 0.8)
        }
        return f
    }

    private func stepFlakes(dt: Float, cursorSpeed: Float) {
        let target = targetCount
        if flakes.isEmpty {
            flakes.reserveCapacity(target + 64)
            for _ in 0..<target { flakes.append(makeFlake(anywhere: true)) }
        } else if flakes.count < target {
            for _ in 0..<min(12, target - flakes.count) { flakes.append(makeFlake(anywhere: false)) }
        }

        let sf = speedFactor, gain = depositGain
        let k = min(1, dt * 2.5)
        let push = cursorValid && cursorSpeed > 200
        var i = 0
        while i < flakes.count {
            var f = flakes[i]
            let layerWind: Float = f.layer == 0 ? 0.55 : (f.layer == 1 ? 0.85 : 1.15)
            let targetVX = wind * layerWind + sin(time * f.swayFreq + f.phase) * f.swayAmp
            f.vx += (targetVX - f.vx) * k
            if push {
                let dx = f.x - cursorX, dy = f.y - cursorY
                let d2 = dx * dx + dy * dy
                if d2 < 3600 {
                    let w = (1 - d2 / 3600) * min(1, dt * 6)
                    f.vx += cursorVX * 0.5 * w
                    f.y += cursorVY * 0.35 * w * dt
                }
            }
            var vy: Float
            switch f.kind {
            case 5, 6:
                // A leaf swings like a pendulum: it tilts with the swing and slows at either end.
                let swing = sin(time * f.swayFreq + f.phase)
                vy = f.fall * sf * (0.45 + 0.55 * abs(cos(time * f.swayFreq + f.phase)))
                f.rot = f.spin * time * 0.3 + swing * 0.7 + f.phase
            case 7:
                // Fluff rides the air: now and then it even rises.
                vy = f.fall * sf * (0.45 + 0.9 * sin(time * 0.23 + f.phase))
                f.rot += f.spin * dt
            default:
                vy = f.fall * sf * (1 + 0.15 * sin(time * 0.9 + f.phase * 2))
                f.rot += f.spin * dt
            }
            f.flip += f.flipSpeed * dt
            let prevY = f.y
            f.x += f.vx * dt
            f.y += vy * dt
            if f.x < -40 { f.x += W + 80 } else if f.x > W + 40 { f.x -= W + 80 }

            var gone = f.y > H + 12 || f.y < -80
            if !gone && f.layer > 0 && f.vol > 0 && tryLand(x: f.x, y: f.y, prevY: prevY, vol: f.vol * gain, nearOnly: f.layer == 2) {
                gone = true
                landed += 1
            }
            if gone {
                if flakes.count > target {
                    flakes.swapAt(i, flakes.count - 1)
                    flakes.removeLast()
                    continue
                }
                f = makeFlake(anywhere: false)
            }
            flakes[i] = f
            i += 1
        }
    }

    /// Lets a flake settle if it has just crossed the top of a drift. Returns true if it stuck.
    private func tryLand(x: Float, y: Float, prevY: Float, vol: Float, nearOnly: Bool) -> Bool {
        for s in active {
            if nearOnly && s.kind == .window { continue }
            if x < s.x0 || prevY > s.y + 1 || y < s.y - s.cap * 1.2 - 3 { continue }
            let col = Int((x - s.x0) / colW)
            if col >= s.h.count || !s.vis[col] || s.cap <= 0.5 { continue }
            let top = s.y - s.h[col]
            if y >= top && prevY <= top + 1.5 {
                let fill = s.h[col] / s.cap
                if rng.unit() > 1 - fill * fill { continue }
                deposit(s, col, vol)
                return true
            }
        }
        return false
    }

    private static let kernel: [Float] = {
        let raw: [Float] = (0..<13).map { i in
            let d = Float(i - 6) / 3.2
            return exp(-d * d)
        }
        let sum = raw.reduce(0, +)
        return raw.map { $0 / sum }
    }()

    private func deposit(_ s: Surface, _ col: Int, _ vol: Float) {
        let n = s.h.count
        let limit = s.cap * 1.1
        for k in 0..<Sim.kernel.count {
            let c = col + k - Sim.kernel.count / 2
            if c < 0 || c >= n || !s.vis[c] { continue }
            s.h[c] = min(limit, s.h[c] + vol * Sim.kernel[k] / colW)
        }
    }

    private func emit(x: Float, y: Float, vx: Float, vy: Float, vol: Float, canLand: Bool = true, kind: UInt8? = nil) {
        guard debris.count < 5000 else { return }
        let k = kind ?? Sim.debrisKind(config.season)
        let tints = k == 1 ? Palette.petals.count : Palette.leaves.count
        debris.append(Debris(x: x, y: y, vx: vx, vy: vy, size: min(3.2, 0.8 + vol.squareRoot() * 0.32), vol: vol, canLand: canLand,
                             kind: k, tint: UInt8(rng.int(tints)), rot: rng.range(0, 6.28), spin: rng.range(-6, 6)))
    }

    private func stepDebris(dt: Float) {
        var i = 0
        while i < debris.count {
            var d = debris[i]
            // Snow drops like snow; petals and leaves flutter down.
            let gravity: Float = d.kind == 0 ? 620 : (d.kind == 1 ? 140 : 230)
            let drag = max(0, 1 - dt * (d.kind == 0 ? 1.1 : 2.2))
            d.vy += gravity * dt
            d.vx += (wind * 0.4 - d.vx) * min(1, dt * 0.8)
            d.vx *= drag
            d.vy *= drag
            d.rot += d.spin * dt
            let prevY = d.y
            d.x += d.vx * dt
            d.y += d.vy * dt
            var gone = d.y > H + 12 || d.x < -60 || d.x > W + 60
            if !gone && d.canLand && d.vy > 0 && tryLand(x: d.x, y: d.y, prevY: prevY, vol: d.vol, nearOnly: false) { gone = true }
            if gone {
                debris.swapAt(i, debris.count - 1)
                debris.removeLast()
                continue
            }
            debris[i] = d
            i += 1
        }
    }

    /// Lets steep piles slump into natural mounds; snow slides off the ends of a window.
    private func relax() {
        let maxD: Float = 0.6 * colW
        let rate: Float = 0.3
        for s in active {
            let n = s.h.count
            if n < 2 { continue }
            s.h.withUnsafeMutableBufferPointer { h in
                for i in 0..<(n - 1) {
                    let d = h[i] - h[i + 1]
                    if d > maxD {
                        let m = (d - maxD) * rate
                        h[i] -= m; h[i + 1] += m
                    } else if d < -maxD {
                        let m = (-d - maxD) * rate
                        h[i] += m; h[i + 1] -= m
                    }
                }
                var i = n - 2
                while i >= 0 {
                    let d = h[i] - h[i + 1]
                    if d > maxD {
                        let m = (d - maxD) * rate
                        h[i] -= m; h[i + 1] += m
                    } else if d < -maxD {
                        let m = (-d - maxD) * rate
                        h[i] += m; h[i + 1] -= m
                    }
                    i -= 1
                }
            }
            if s.kind == .floor { continue }
            if s.h[0] > maxD {
                let m = (s.h[0] - maxD) * rate
                s.h[0] -= m
                s.spillL += m * colW
                if s.spillL >= 5 {
                    if s.vis[0] { emit(x: s.x0 - 1, y: s.y - s.h[0] * 0.5, vx: rng.range(-35, -8), vy: rng.range(-10, 20), vol: s.spillL) }
                    s.spillL = 0
                }
            }
            if s.h[n - 1] > maxD {
                let m = (s.h[n - 1] - maxD) * rate
                s.h[n - 1] -= m
                s.spillR += m * colW
                if s.spillR >= 5 {
                    if s.vis[n - 1] { emit(x: s.x1 + 1, y: s.y - s.h[n - 1] * 0.5, vx: rng.range(8, 35), vy: rng.range(-10, 20), vol: s.spillR) }
                    s.spillR = 0
                }
            }
        }
    }

    // MARK: - Cursor in the snow

    private func carve(cx: Float, cy: Float, radius R: Float) {
        var removed: Float = 0
        for s in active {
            if cx + R < s.x0 || cx - R > s.x1 || cy + R < s.y - s.cap * 1.2 - 1 || cy - R > s.y + 2 { continue }
            let a = max(0, Int((cx - R - s.x0) / colW))
            let b = min(s.h.count - 1, Int((cx + R - s.x0) / colW))
            if a > b { continue }
            for c in a...b where s.vis[c] && s.h[c] > 0 {
                let dx = s.x0 + (Float(c) + 0.5) * colW - cx
                let q = R * R - dx * dx
                if q <= 0 { continue }
                let reach = cy + q.squareRoot()
                let top = s.y - s.h[c]
                if reach <= top { continue }
                let nh = max(0, s.y - reach)
                if nh < s.h[c] {
                    removed += (s.h[c] - nh) * colW
                    s.h[c] = nh
                }
            }
        }
        guard removed > 0 else { return }

        if config.tool == 1 && season == .winter {
            // Snowball mode: the swept snow is packed into a ball.
            let packed = removed * 0.55
            if let b = activeBall, b.above == nil, b.below == nil, !b.dormant,
               (b.x - cx) * (b.x - cx) + (b.y - cy) * (b.y - cy) < (b.r + 55) * (b.r + 55) {
                if b.r < Sim.maxBallRadius { b.area += packed }
            } else {
                pendingBall += packed
                if pendingBall >= 60 {
                    let dir: Float = cursorVX >= 0 ? 1 : -1
                    let b = Ball(x: min(W - 6, max(6, cx + dir * (R + 5))), y: cy, area: pendingBall, style: UInt32(truncatingIfNeeded: rng.next()))
                    b.vx = cursorVX * 0.3
                    balls.append(b)
                    activeBall = b
                    pendingBall = 0
                }
            }
            return
        }

        carveAcc += removed
        while carveAcc >= 5 {
            carveAcc -= 5
            emit(x: cx + rng.range(-R, R) * 0.7, y: cy + rng.range(-4, 4),
                 vx: cursorVX * 0.45 + rng.range(-70, 70), vy: cursorVY * 0.3 - rng.range(40, 170), vol: 5)
        }
    }

    // MARK: - Snowballs and snowmen

    private func ground(x: Float, prevBottom: Float, r: Float) -> (Float, Surface)? {
        var best: (Float, Surface)?
        for s in active {
            if x < s.x0 || x >= s.x1 || s.y < prevBottom - 10 { continue }
            let col = Int((x - s.x0) / colW)
            if col >= s.h.count { continue }
            if s.kind != .floor && !s.vis[col] { continue }
            let hh = s.h[col]
            let top = s.y - hh + min(hh * 0.6, r * 0.3)
            if best == nil || top < best!.0 { best = (top, s) }
        }
        return best
    }

    private func chainLength(_ root: Ball) -> Int {
        var n = 1
        var b = root
        while let a = b.above {
            n += 1
            b = a
        }
        return n
    }

    private func tryStack(_ a: Ball) -> Bool {
        guard a.below == nil else { return false }
        let aLen = chainLength(a)
        for t in balls where t.above == nil && t !== a && t.root !== a {
            let tr = t.root
            if tr.dormant || tr.grabbed || tr.hidden { continue }
            let dx = a.x - t.x, dy = a.y - t.y
            let reach = a.r + t.r + 2
            if dx * dx + dy * dy > reach * reach { continue }
            if dy > -0.3 * t.r || abs(dx) > 0.6 * t.r || a.r > t.r * 1.15 { continue }
            if chainLength(tr) + aLen > 3 { continue }
            a.below = t
            t.above = a
            a.vx = 0
            a.vy = 0
            a.support = nil
            return true
        }
        return false
    }

    private func burst(_ root: Ball, remove: Bool = true) {
        let members = root.chain
        for b in members {
            let n = min(110, Int(b.area / 9) + 6)
            let vol = b.area / 0.55 / Float(n)
            for _ in 0..<n {
                let a = rng.range(0, 6.28), d = rng.unit().squareRoot() * b.r
                let sp = rng.range(40, 260)
                emit(x: b.x + cos(a) * d, y: b.y + sin(a) * d, vx: cos(a) * sp + b.vx * 0.3, vy: sin(a) * sp - 90, vol: vol)
            }
        }
        if remove { balls.removeAll { m in members.contains { $0 === m } } }
    }

    private func stepBalls(dt: Float, cursorSpeed: Float) {
        guard !balls.isEmpty else { return }
        var burstList: [Ball] = []

        for a in balls where a.below == nil {
            if a.dormant { continue }
            if a.grabbed {
                let tx = min(W - a.r, max(a.r, cursorX + grabDX))
                let ty = min(H - a.r * 0.5, max(topInset + a.r, cursorY + grabDY))
                let k = min(1, dt * 14)
                a.vx += ((tx - a.x) / dt - a.vx) * k
                a.vy += ((ty - a.y) / dt - a.vy) * k
                a.x = tx
                a.y = ty
                a.hidden = false
                continue
            }

            let prevBottom = a.y + a.r
            a.vy += 1400 * dt
            a.x += a.vx * dt
            a.y += a.vy * dt
            if a.x < a.r { a.x = a.r; a.vx = abs(a.vx) * 0.3 }
            if a.x > W - a.r { a.x = W - a.r; a.vx = -abs(a.vx) * 0.3 }

            // Cursor shoves loose balls.
            if cursorValid && config.tool != 2 && cursorSpeed > 150 && a.above == nil {
                let dx = a.x - cursorX, dy = a.y - cursorY
                let d = (dx * dx + dy * dy).squareRoot()
                let reach = a.r + 12
                if d < reach && d > a.r * 0.45 {
                    let nx = dx / d, ny = dy / d
                    a.x += nx * (reach - d)
                    a.y += ny * (reach - d) * 0.5
                    let vn = cursorVX * nx + cursorVY * ny
                    let bn = a.vx * nx + a.vy * ny
                    if vn > bn {
                        a.vx += nx * (vn - bn)
                        a.vy += ny * (vn - bn) * 0.5
                    }
                    let sp = (a.vx * a.vx + a.vy * a.vy).squareRoot()
                    if sp > 1100 { a.vx *= 1100 / sp; a.vy *= 1100 / sp }
                    activeBall = a
                }
            }

            // Other balls.
            for b in balls where b !== a && b.root !== a {
                let br = b.root
                if br.dormant || br.grabbed || br.hidden { continue }
                let dx = a.x - b.x, dy = a.y - b.y
                let reach = a.r + b.r
                let d2 = dx * dx + dy * dy
                if d2 >= reach * reach || d2 < 0.0001 { continue }
                if b.above == nil && a.vy >= 0 && tryStack(a) { break }
                let d = d2.squareRoot()
                let nx = dx / d, ny = dy / d
                let overlap = reach - d
                if b.below == nil && b.above == nil {
                    a.x += nx * overlap * 0.5; a.y += ny * overlap * 0.5
                    b.x -= nx * overlap * 0.5
                } else {
                    a.x += nx * overlap; a.y += ny * overlap
                }
                let vn = a.vx * nx + a.vy * ny
                if vn < 0 {
                    a.vx -= nx * vn * 1.15
                    a.vy -= ny * vn * 1.15
                }
            }
            if a.below != nil { continue }

            // Ground.
            if let (gy, s) = ground(x: a.x, prevBottom: prevBottom, r: a.r), a.y + a.r >= gy {
                if a.vy > 1000 {
                    burstList.append(a)
                    continue
                }
                a.y = gy - a.r
                a.vy = a.vy > 140 ? -a.vy * 0.18 : 0
                a.vx *= max(0, 1 - dt * 2.4)
                if abs(a.vx) < 4 { a.vx = 0 }
                a.support = s.id
                a.relX = a.x - s.x0
                a.angle += a.vx / max(3, a.r) * dt
                let col = Int((a.x - s.x0) / colW)
                a.hidden = s.kind != .floor && col >= 0 && col < s.vis.count && !s.vis[col]
                // A rolling ball packs the snow it rolls over.
                if abs(a.vx) > 10 && a.above == nil && a.r < Sim.maxBallRadius && !a.hidden {
                    let ca = max(0, Int((a.x - a.r * 0.7 - s.x0) / colW))
                    let cb = min(s.h.count - 1, Int((a.x + a.r * 0.7 - s.x0) / colW))
                    if ca <= cb {
                        let f = min(1, dt * 12)
                        for c in ca...cb where s.vis[c] && s.h[c] > 0.4 {
                            let take = s.h[c] * f
                            s.h[c] -= take
                            a.area += take * colW * 0.55
                        }
                    }
                }
            } else {
                a.support = nil
                a.hidden = false
                a.angle += a.vx / max(3, a.r) * dt * 0.5
                if a.y - a.r > H + 80 { burstList.append(a) }
            }
        }

        for b in burstList { burst(b) }

        // Stacked balls ride on the one below.
        for root in balls where root.below == nil {
            var lower = root
            while let up = lower.above {
                up.x += (lower.x - up.x) * min(1, dt * 22)
                up.y = lower.y - (lower.r + up.r) * 0.86
                up.vx = lower.vx
                up.vy = lower.vy
                up.angle = 0
                lower = up
            }
        }
    }

    // MARK: - Garland

    private static let bulbColors: [(Float, Float, Float)] = [
        (1.0, 0.24, 0.22), (1.0, 0.78, 0.22), (0.28, 0.92, 0.40), (0.30, 0.60, 1.0), (1.0, 0.40, 0.85),
    ]

    private func buildGarland() {
        bulbs.removeAll()
        wire.removeAll()
        let spans = max(2, Int((W / 150).rounded()))
        let spanW = W / Float(spans)
        let y0 = topInset + 1.5
        let sag: Float = 13
        var index = 0
        for sIdx in 0..<spans {
            let xa = Float(sIdx) * spanW
            for k in 0...12 {
                let t = Float(k) / 12
                wire.append((xa + t * spanW, y0 + sag * 4 * t * (1 - t)))
            }
            for k in 0..<5 {
                let t = (Float(k) + 0.5) / 5
                bulbs.append(Bulb(x: xa + t * spanW, y: y0 + sag * 4 * t * (1 - t), color: index % Sim.bulbColors.count,
                                  phase: rng.range(0, 6.28), speed: rng.range(1.2, 3.4)))
                index += 1
            }
        }
    }

    private func drawGarland(_ batch: Batch) {
        guard config.garland != 0, wire.count > 1 else { return }
        for i in 0..<(wire.count - 1) {
            let a = wire[i], b = wire[i + 1]
            if b.0 < a.0 { continue }
            batch.line(a.0, a.1, b.0, b.1, width: 1.3, r: 0.07, g: 0.22, b: 0.11, a: 0.95)
        }
        for (i, bulb) in bulbs.enumerated() {
            var br: Float = 1
            switch config.garland {
            case 2: br = 0.4 + 0.6 * (0.5 + 0.5 * sin(time * bulb.speed + bulb.phase))
            case 3:
                let c = 0.5 + 0.5 * cos(2 * .pi * (Float(i) / 4 - time * 0.8))
                br = 0.22 + 0.78 * c * c
            default: break
            }
            let col = Sim.bulbColors[bulb.color]
            batch.sprite(.glow, x: bulb.x, y: bulb.y + 7, hw: 17, hh: 17, r: col.0, g: col.1, b: col.2, a: 0.5 * br, additive: true)
            batch.rect(x: bulb.x - 1.7, y: bulb.y - 1.2, w: 3.4, h: 3.4, r: 0.06, g: 0.18, b: 0.09, a: 1)
            let k = 0.45 + 0.55 * br
            batch.sprite(.bulb, x: bulb.x, y: bulb.y + 7.2, hw: 3.7, hh: 5.5, r: col.0 * k, g: col.1 * k, b: col.2 * k, a: 1)
            batch.sprite(.dot, x: bulb.x - 0.9, y: bulb.y + 5.4, hw: 1.5, hh: 1.5, a: 0.75 * br)
        }
    }

    // MARK: - Drawing

    func build(_ batch: Batch) {
        // Far flakes first: they are behind the windows.
        let depth = config.depth
        for f in flakes where f.layer == 0 {
            if depth {
                var behind = false
                for o in occluders where o.contains(f.x, f.y) { behind = true; break }
                if behind { continue }
            }
            drawFlake(f, batch)
        }

        festive.drawFireworks(batch)
        let winter = season == .winter
        if winter { drawGarland(batch) }
        drawBalls(batch)
        drawDrifts(batch)
        festive.drawDecor(batch)

        for d in debris {
            switch d.kind {
            case 1:
                let c = Palette.petals[Int(d.tint) % Palette.petals.count]
                let hw = 2.6 + d.size * 0.6
                batch.sprite(.petal, x: d.x, y: d.y, hw: hw * max(0.3, abs(cos(d.rot * 0.7))), hh: hw * 1.15, rot: d.rot,
                             r: c.0, g: c.1, b: c.2, a: 0.95)
            case 2:
                let c = Palette.leaves[Int(d.tint) % Palette.leaves.count]
                let hw = 4 + d.size * 1.2
                batch.sprite(d.tint % 2 == 0 ? .maple : .leaf, x: d.x, y: d.y, hw: hw * max(0.3, abs(cos(d.rot * 0.6))), hh: hw,
                             rot: d.rot, r: c.0, g: c.1, b: c.2, a: 1)
            default:
                let hw = d.size * 1.5
                batch.sprite(.dot, x: d.x, y: d.y, hw: hw, hh: hw, a: 0.95)
            }
        }
        for f in flakes where f.layer != 0 { drawFlake(f, batch) }
        festive.drawCreatures(batch)
        if config.frost && winter { festive.drawFrost(batch) }
    }

    private func drawFlake(_ f: Flake, _ batch: Batch) {
        switch f.kind {
        case 0:
            let hw = f.size * 1.5
            batch.sprite(.dot, x: f.x, y: f.y, hw: hw, hh: hw, a: f.alpha)
        case 1, 2, 3:
            let hw = f.size * 1.9
            let sprite: Sprite = f.kind == 1 ? .crystal1 : (f.kind == 2 ? .crystal2 : .crystal3)
            batch.sprite(sprite, x: f.x, y: f.y, hw: hw, hh: hw, rot: f.rot, a: f.alpha)
        case 4:
            let c = Palette.petals[Int(f.tint) % Palette.petals.count]
            batch.sprite(.petal, x: f.x, y: f.y, hw: f.size * max(0.22, abs(cos(f.flip))), hh: f.size * 1.15, rot: f.rot,
                         r: c.0, g: c.1, b: c.2, a: f.alpha)
        case 5, 6:
            let c = Palette.leaves[Int(f.tint) % Palette.leaves.count]
            // Far leaves are paler, as if seen through the air.
            let mist: Float = f.layer == 0 ? 0.35 : 0
            batch.sprite(f.kind == 6 ? .maple : .leaf, x: f.x, y: f.y, hw: f.size * max(0.3, abs(cos(f.flip))), hh: f.size,
                         rot: f.rot, r: c.0 + (0.9 - c.0) * mist, g: c.1 + (0.85 - c.1) * mist, b: c.2 + (0.8 - c.2) * mist, a: f.alpha)
        default:
            batch.sprite(.fluff, x: f.x, y: f.y, hw: f.size, hh: f.size, rot: f.rot, a: f.alpha)
        }
    }

    private static let smooth: [Float] = [1, 3, 6, 8, 6, 3, 1]

    private func drawDrifts(_ batch: Batch) {
        switch season {
        case .winter: drawSnowDrifts(batch)
        case .spring, .autumn: drawLitter(batch)
        case .summer: break
        }
    }

    /// Smoothed, tapered height of a pile at column i (the same shape the snow drifts use).
    @inline(__always)
    private func pileHeight(_ s: Surface, _ i: Int) -> Float {
        let n = s.h.count
        var acc: Float = 0, wsum: Float = 0
        for k in -3...3 {
            let j = i + k
            if j < 0 || j >= n || !s.vis[j] { continue }
            let w = Sim.smooth[k + 3]
            acc += s.h[j] * w
            wsum += w
        }
        let edge = min(i, n - 1 - i)
        let taper: Float = s.kind == .floor ? 1 : min(1, 0.45 + Float(edge) * 0.11)
        return (wsum > 0 ? acc / wsum : s.h[i]) * taper
    }

    /// Petals and leaves: a soft coloured bed with the pieces themselves lying on top.
    private func drawLitter(_ batch: Batch) {
        let autumn = season == .autumn
        for s in active {
            let n = s.h.count
            if n < 3 { continue }
            let base = s.kind == .floor ? s.y : s.y + 1
            var prevH: Float = 0
            var prevOK = false
            for i in 0..<n {
                if !s.vis[i] { prevOK = false; continue }
                let hh = pileHeight(s, i)
                if prevOK && (hh > 0.25 || prevH > 0.25) {
                    let xb = s.x0 + (Float(i) + 0.5) * colW, xa = xb - colW
                    let aa = min(1, prevH / 1.5) * 0.92, ab = min(1, hh / 1.5) * 0.92
                    // Autumn: a heap of brown and orange; spring: a pale pink bed.
                    let top: (Float, Float, Float) = autumn ? (0.78, 0.42, 0.14) : (1.0, 0.86, 0.90)
                    let low: (Float, Float, Float) = autumn ? (0.42, 0.21, 0.08) : (0.93, 0.70, 0.78)
                    batch.quad(batch.flat(xa, s.y - prevH, top.0, top.1, top.2, aa), batch.flat(xb, s.y - hh, top.0, top.1, top.2, ab),
                               batch.flat(xa, base, low.0, low.1, low.2, aa), batch.flat(xb, base, low.0, low.1, low.2, ab))
                }
                prevH = hh
                prevOK = true
            }
            // The pieces on top: a few per column, placed by a hash so they stay put from frame to frame.
            let stride = autumn ? 3 : 2
            var i = 1
            while i < n - 1 {
                defer { i += stride }
                if !s.vis[i] { continue }
                let hh = pileHeight(s, i)
                if hh < 0.9 { continue }
                let layers = autumn ? min(4, Int(hh / 3.2) + 1) : 1
                for k in 0..<layers {
                    let hsh = UInt32(truncatingIfNeeded: (i &* 7919) ^ (s.id &* 104_729) ^ (k &* 1_299_709))
                    let jitter = Float(hsh % 100) / 100 - 0.5
                    let x = s.x0 + (Float(i) + 0.5 + jitter * 1.6) * colW
                    let y = s.y - hh + 1.2 + Float(k) * 3.4
                    let rot = Float(hsh % 628) / 100
                    if autumn {
                        let c = Palette.leaves[Int(hsh >> 8) % Palette.leaves.count]
                        let shade: Float = 1 - Float(k) * 0.14
                        let size: Float = 4.6 + Float((hsh >> 4) % 30) / 10
                        batch.sprite((hsh >> 12) % 2 == 0 ? .maple : .leaf, x: x, y: y, hw: size * 0.9, hh: size, rot: rot,
                                     r: c.0 * shade, g: c.1 * shade, b: c.2 * shade, a: 1)
                    } else {
                        let c = Palette.petals[Int(hsh >> 8) % Palette.petals.count]
                        batch.sprite(.petal, x: x, y: y, hw: 2.4, hh: 2.8, rot: rot, r: c.0, g: c.1, b: c.2, a: 0.95)
                    }
                }
            }
        }
    }

    private func drawSnowDrifts(_ batch: Batch) {
        for s in active {
            let n = s.h.count
            if n < 3 { continue }
            let base = s.kind == .floor ? s.y : s.y + 1
            var prevH: Float = 0
            var prevOK = false
            for i in 0..<n {
                if !s.vis[i] {
                    prevOK = false
                    continue
                }
                var acc: Float = 0, wsum: Float = 0
                for k in -3...3 {
                    let j = i + k
                    if j < 0 || j >= n || !s.vis[j] { continue }
                    let w = Sim.smooth[k + 3]
                    acc += s.h[j] * w
                    wsum += w
                }
                // Keep the very ends rounded instead of chopped.
                let edge = min(i, n - 1 - i)
                let taper: Float = s.kind == .floor ? 1 : min(1, 0.45 + Float(edge) * 0.11)
                let hh = (wsum > 0 ? acc / wsum : s.h[i]) * taper
                if prevOK && (hh > 0.25 || prevH > 0.25) {
                    let xb = s.x0 + (Float(i) + 0.5) * colW
                    let xa = xb - colW
                    let ya = s.y - prevH, yb = s.y - hh
                    let aa = min(1, prevH / 1.2) * 0.97, ab = min(1, hh / 1.2) * 0.97
                    let sa = min(1, prevH / 16), sb = min(1, hh / 16)
                    // Body: white on top, a cool shadow towards the base of deep snow.
                    batch.quad(batch.flat(xa, ya, 1, 1, 1, aa), batch.flat(xb, yb, 1, 1, 1, ab),
                               batch.flat(xa, base, 1 - 0.19 * sa, 1 - 0.12 * sa, 1 - 0.03 * sa, aa),
                               batch.flat(xb, base, 1 - 0.19 * sb, 1 - 0.12 * sb, 1 - 0.03 * sb, ab))
                    // Soft top edge.
                    batch.quad(batch.flat(xa, ya - 1.2, 1, 1, 1, 0), batch.flat(xb, yb - 1.2, 1, 1, 1, 0),
                               batch.flat(xa, ya, 1, 1, 1, aa), batch.flat(xb, yb, 1, 1, 1, ab))
                }
                // Now and then a crystal on the surface catches the light.
                if config.sparkle && hh > 3 && (i &* 7919 &+ s.id &* 31) % 29 == 0 {
                    let phase = Float((i &* 131 &+ s.id &* 17) % 628) / 100
                    let tw = sin(time * 1.1 + phase)
                    if tw > 0.9 {
                        let g = (tw - 0.9) / 0.1
                        let xg = s.x0 + (Float(i) + 0.5) * colW, yg = s.y - hh + 1.8
                        batch.sprite(.glow, x: xg, y: yg, hw: 6, hh: 6, r: 0.85, g: 0.93, b: 1, a: g * 0.6, additive: true)
                        batch.sprite(.crystal3, x: xg, y: yg, hw: 3.4 * g + 0.6, hh: 3.4 * g + 0.6, rot: time * 0.8 + phase,
                                     a: g, additive: true)
                    }
                }
                prevH = hh
                prevOK = true
            }
        }
    }

    private static let scarfColors: [(Float, Float, Float)] = [
        (0.90, 0.20, 0.22), (0.20, 0.55, 0.95), (0.18, 0.70, 0.42), (0.98, 0.72, 0.15), (0.72, 0.36, 0.90),
    ]

    private func drawBalls(_ batch: Batch) {
        for root in balls where root.below == nil && !root.dormant && !root.hidden {
            let chain = root.chain
            if chain.count >= 2 { drawArms(chain, batch) }
            for b in chain {
                if b === hover || b === grabbedBall {
                    batch.sprite(.glow, x: b.x, y: b.y, hw: b.r * 2.1, hh: b.r * 2.1, a: 0.45, additive: true)
                }
                let hw = b.r * 1.03
                batch.sprite(.ballBase, x: b.x, y: b.y, hw: hw, hh: hw)
                batch.sprite(.ballTex, x: b.x, y: b.y, hw: hw, hh: hw, rot: b.angle, a: 0.9)
            }
            if chain.count >= 2 { drawFace(chain, batch) }
        }
    }

    private func drawArms(_ chain: [Ball], _ batch: Batch) {
        let body = chain[chain.count - 2]
        let br = body.r
        let hw = br * 0.78, hh = br * 0.39
        let ay = body.y - br * 0.12
        batch.sprite(.twig, x: body.x + br * 0.78 + hw * 0.92, y: ay - hh * 0.24, hw: hw, hh: hh)
        batch.sprite(.twig, x: body.x - br * 0.78 - hw * 0.92, y: ay - hh * 0.24, hw: hw, hh: hh, flipX: true)
    }

    private func drawFace(_ chain: [Ball], _ batch: Batch) {
        let head = chain[chain.count - 1]
        let body = chain[chain.count - 2]
        let style = chain[0].style
        let dir: Float = (style & 1) == 0 ? 1 : -1
        let hr = head.r, br = body.r

        // Buttons.
        let buttons: [Float] = chain.count >= 3 ? [-0.4, 0, 0.4] : [-0.1, 0.3]
        for k in buttons {
            batch.sprite(.coal, x: body.x, y: body.y + k * br, hw: br * 0.085, hh: br * 0.085)
        }

        // Scarf.
        let sc = Sim.scarfColors[Int(style >> 1) % Sim.scarfColors.count]
        let shh = hr * 0.62
        batch.sprite(.scarf, x: head.x, y: head.y + hr * 0.80 + shh * 0.36, hw: hr * 1.08, hh: shh,
                     r: sc.0, g: sc.1, b: sc.2, flipX: dir < 0)

        // Eyes, smile, carrot.
        let fx = head.x + dir * hr * 0.06
        for side in [-1, 1] as [Float] {
            batch.sprite(.coal, x: fx + side * hr * 0.31, y: head.y - hr * 0.20, hw: hr * 0.115, hh: hr * 0.115)
        }
        for k in -2...2 {
            let kf = Float(k)
            batch.sprite(.coal, x: fx + kf * hr * 0.14, y: head.y + hr * 0.50 - kf * kf * hr * 0.04, hw: hr * 0.05, hh: hr * 0.05)
        }
        let len = hr * 0.95
        batch.sprite(.carrot, x: fx + dir * len * 0.5, y: head.y + hr * 0.10, hw: len * 0.5, hh: len * 0.2,
                     rot: dir * 0.10, flipX: dir < 0)

        // Hat.
        let hat: Sprite = (style >> 5) % 3 == 1 ? .topHat : .bucket
        batch.sprite(hat, x: head.x - dir * hr * 0.14, y: head.y - hr * 1.22, hw: hr * 0.95, hh: hr * 0.95, rot: -dir * 0.16)
    }

    // MARK: - Test hooks (used by the snapshot mode)

    func debugAddBall(x: Float, y: Float, radius: Float) -> Ball {
        let b = Ball(x: x, y: y, area: .pi * radius * radius, style: UInt32(truncatingIfNeeded: rng.next()))
        balls.append(b)
        return b
    }

    var debugStats: (flakes: Int, debris: Int, surfaces: Int, snow: Float) {
        var total: Float = 0
        for s in surfaces.values { for v in s.h { total += v * colW } }
        return (flakes.count, debris.count, active.count, total)
    }

    /// Snow on the Dock and whether the Dock is currently a surface.
    var debugDock: (snow: Float, active: Bool) {
        guard let d = surfaces[Sim.dockID] else { return (0, false) }
        return (d.h.reduce(0, +) * colW, d.onScreen)
    }

    var debugDebris: Int { debris.count }

    func debugMaxHeight() -> Float {
        var m: Float = 0
        for s in surfaces.values { for v in s.h { m = max(m, v) } }
        return m
    }

    func debugHasNaN() -> Bool {
        for f in flakes where !(f.x.isFinite && f.y.isFinite) { return true }
        for b in balls where !(b.x.isFinite && b.y.isFinite && b.r.isFinite) { return true }
        for s in surfaces.values { for v in s.h where !v.isFinite { return true } }
        return false
    }
}
