import AppKit
import MetalKit

/// A transparent, click-through window covering one screen.
final class OverlayPanel: NSPanel {
    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        // Just above the Dock, below the menu bar, menus and notifications.
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.dockWindow)) + 1)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        ignoresMouseEvents = true
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        isMovable = false
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        setFrame(screen.frame, display: false)
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class SnowView: MTKView {
    var onDown: (() -> Void)?
    var onUp: (() -> Void)?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { onDown?() }
    override func mouseUp(with event: NSEvent) { onUp?() }
    override func mouseDragged(with event: NSEvent) {}
}

/// Snow for one screen: window, view, simulation and render loop.
final class Overlay: NSObject, MTKViewDelegate {
    /// Set by the app delegate: the frontmost app is one the user hid the snow for.
    static var hiddenForFrontApp = false

    let screen: NSScreen
    let panel: OverlayPanel
    let view: SnowView
    let sim: Sim
    /// 0 snow stays over full-screen apps, 1 hides, 2 also hides over maximized windows.
    var fullScreenMode = 1
    private let renderer: Renderer
    private let batch: Batch
    private let buffers = FrameBuffers()
    private let displayUUID: String?
    private var lastTime: CFTimeInterval = 0
    private var lastVersion = -1
    private var lastApply: CFTimeInterval = 0
    private var shownEmpty = false
    private var capturing = false
    private var covered = false
    private var lastCoverCheck: CFTimeInterval = 0
    /// 0 hidden ... 1 fully shown; fades between the two.
    private var visibility: Float = 0
    private var debugFrames = 0
    private var debugStart: CFTimeInterval = 0
    private var debugNext: CFTimeInterval = 0
    private var lastSnap = WindowSnapshot()
    private var lastScreenRect = CGRect.zero

    init(screen: NSScreen, renderer: Renderer) {
        self.screen = screen
        self.renderer = renderer
        batch = renderer.makeBatch()
        let f = screen.frame
        let topInset = Float(max(0, f.maxY - screen.visibleFrame.maxY))
        sim = Sim(width: Float(f.width), height: Float(f.height), topInset: topInset)
        panel = OverlayPanel(screen: screen)
        view = SnowView(frame: CGRect(origin: .zero, size: f.size), device: renderer.device)
        if let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
           let uuid = CGDisplayCreateUUIDFromDisplayID(number)?.takeRetainedValue() {
            displayUUID = CFUUIDCreateString(nil, uuid) as String?
        } else {
            displayUUID = nil
        }
        super.init()

        view.colorPixelFormat = Renderer.pixelFormat
        view.clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
        view.framebufferOnly = true
        view.preferredFramesPerSecond = 60
        view.layer?.isOpaque = false
        if let layer = view.layer as? CAMetalLayer {
            layer.isOpaque = false
            layer.colorspace = CGColorSpace(name: CGColorSpace.sRGB)
        }
        view.delegate = self
        view.onDown = { [weak self] in self?.sim.grabHover() }
        view.onUp = { [weak self] in self?.sim.releaseGrab() }
        panel.contentView = view
    }

    func show(_ on: Bool) {
        if on {
            if view.isPaused {
                lastTime = 0
                visibility = 0
            }
            view.isPaused = false
            panel.orderFrontRegardless()
        } else {
            view.isPaused = true
            panel.orderOut(nil)
        }
    }

    func close() {
        view.isPaused = true
        view.delegate = nil
        panel.orderOut(nil)
        panel.close()
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

    /// Is a full-screen app (or, in mode 2, a maximized window) in front on this screen?
    private func checkCovered(_ snap: WindowSnapshot, _ screenRect: CGRect, now: Double) -> Bool {
        guard fullScreenMode > 0 else { return false }
        // The reliable signal: the system says this display shows a full-screen app's Space.
        // (On a Mac with a notch a full-screen window and a window stretched to fill the screen
        // have the same frame, so the frame alone can't tell them apart.)
        if let uuid = displayUUID, WindowTracker.shared.isFullScreenSpace(displayUUID: uuid, now: now) == true { return true }
        guard let front = snap.windows.first(where: { !$0.isWidget && $0.rect.intersects(screenRect) }) else { return false }
        let r = front.rect.insetBy(dx: -2, dy: -2)
        // Games and players that go full screen without a Space cover the menu bar too.
        if r.contains(screenRect) { return true }
        if fullScreenMode == 2 {
            let primaryHeight = NSScreen.screens.first?.frame.height ?? screen.frame.height
            let v = screen.visibleFrame
            let visible = CGRect(x: v.minX, y: primaryHeight - v.maxY, width: v.width, height: v.height)
            if r.contains(visible.insetBy(dx: 8, dy: 8)) { return true }
        }
        return false
    }

    func draw(in view: MTKView) {
        let now = CACurrentMediaTime()
        var dt = Float(now - lastTime)
        lastTime = now
        if dt <= 0 || dt > 0.1 { dt = 1 / 60 }

        let primaryHeight = NSScreen.screens.first?.frame.height ?? screen.frame.height
        let f = screen.frame
        let screenRect = CGRect(x: f.minX, y: primaryHeight - f.maxY, width: f.width, height: f.height)

        let tracker = WindowTracker.shared
        let snap = tracker.poll(now: now)
        if snap.version != lastVersion || sim.needsWindowRefresh {
            lastVersion = snap.version
            // While the snow is hidden, windows that move don't shake their drifts off.
            let elapsed: Float = visibility < 0.5 ? 10 : Float(now - lastApply)
            lastApply = now
            sim.applyWindows(snap, originX: Float(screenRect.minX), originY: Float(screenRect.minY),
                             elapsed: elapsed, existing: { tracker.existing(of: $0) })
            lastCoverCheck = 0
        }
        if now - lastCoverCheck > 0.1 {
            lastCoverCheck = now
            covered = checkCovered(snap, screenRect, now: now)
        }

        // Quick fade out over full-screen apps and hidden apps, quick fade back in afterwards.
        // The snow keeps falling while hidden, so it comes back at once and up to date.
        let target: Float = (covered || Overlay.hiddenForFrontApp) ? 0 : 1
        visibility += (target - visibility) * min(1, dt * 16)
        if abs(target - visibility) < 0.01 { visibility = target }
        let idle = visibility == 0
        let fps = idle ? 20 : (sim.config.lowPower ? 30 : 60)
        if view.preferredFramesPerSecond != fps { view.preferredFramesPerSecond = fps }

        let m = NSEvent.mouseLocation
        let inside = f.contains(m)
        let cx = Float(m.x - f.minX), cy = Float(f.maxY - m.y)

        sim.step(dt: dt, cursorX: cx, cursorY: cy, cursorInside: inside && visibility > 0.5)
        batch.clear()
        if !idle { sim.build(batch) }

        // Take mouse clicks only while the cursor is on a snowball, so it can be picked up.
        let want = visibility > 0.9 && (sim.hover != nil || sim.grabbedBall != nil)
        if want != capturing {
            capturing = want
            panel.ignoresMouseEvents = !want
            if want { NSCursor.openHand.set() } else { NSCursor.arrow.set() }
        }

        lastSnap = snap
        lastScreenRect = screenRect
        if LiveDebug.dir != nil { debugTick(now) }

        // While hidden, one empty frame is enough; skip drawing the rest to save power.
        if idle && shownEmpty { return }
        shownEmpty = idle

        guard let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable else { return }
        renderer.draw(batch: batch, buffers: buffers, pass: pass,
                      viewport: SIMD2(Float(view.bounds.width), Float(view.bounds.height)), present: drawable,
                      fade: visibility * visibility * (3 - 2 * visibility))
    }

    private func debugTick(_ now: CFTimeInterval) {
        if debugStart == 0 {
            debugStart = now
            debugNext = now + 20
        }
        debugFrames += 1
        guard now >= debugNext else { return }
        debugNext = now + 20
        let idx = NSScreen.screens.firstIndex(of: screen) ?? 0
        let o = lastScreenRect.origin
        let wins = lastSnap.windows.map { $0.rect.offsetBy(dx: -o.x, dy: -o.y) }
        let dock = lastSnap.dock?.offsetBy(dx: -o.x, dy: -o.y)
        LiveDebug.capture(renderer: renderer, sim: sim, screenIndex: idx, windows: wins, dock: dock,
                          frames: debugFrames, seconds: now - debugStart, covered: covered, visibility: visibility)
    }
}
