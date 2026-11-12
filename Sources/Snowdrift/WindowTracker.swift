import AppKit
import CoreGraphics

/// One window as the snow sees it: only its id and its frame. No titles, no contents.
struct WinInfo: Equatable {
    let id: Int
    /// Frame in global display coordinates, origin at the top-left of the main display, y down.
    let rect: CGRect
    /// Desktop widget rather than an app window.
    let isWidget: Bool
}

struct WindowSnapshot {
    var version = 0
    /// Front to back.
    var windows: [WinInfo] = []
    /// The Dock bar, if it is visible.
    var dock: CGRect?
}

/// Reads window frames from the system window list.
///
/// This uses `CGWindowListCopyWindowInfo` and reads only bounds, layer, alpha and owner pid.
/// Those need no Screen Recording or Accessibility permission, and the app asks for neither.
final class WindowTracker {
    static let shared = WindowTracker()

    private(set) var snapshot = WindowSnapshot()
    private var lastPoll = 0.0
    private var lastChange = 0.0
    private var lastDockPoll = -10.0
    private var dock: CGRect?
    private let ownPid = Int(ProcessInfo.processInfo.processIdentifier)
    private var dockPid: Int?
    private let dockLevel = Int(CGWindowLevelForKey(.dockWindow))

    private typealias MainConnFn = @convention(c) () -> Int32
    private typealias DockRectFn = @convention(c) (Int32, UnsafeMutablePointer<CGRect>, UnsafeMutablePointer<Int32>) -> Int32
    private typealias CurrentSpaceFn = @convention(c) (Int32, CFString) -> UInt64
    private typealias SpaceTypeFn = @convention(c) (Int32, UInt64) -> Int32
    private var mainConn: MainConnFn?
    private var dockRectFn: DockRectFn?
    private var currentSpaceFn: CurrentSpaceFn?
    private var spaceTypeFn: SpaceTypeFn?
    private var spaceCache: [String: (time: Double, full: Bool?)] = [:]

    private init() {
        // Read-only Dock geometry from the window server. Looked up by name so that a system
        // without this function simply falls back to an estimate.
        if let handle = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_LAZY),
           let conn = dlsym(handle, "SLSMainConnectionID"),
           let rect = dlsym(handle, "SLSGetDockRectWithReason") {
            mainConn = unsafeBitCast(conn, to: MainConnFn.self)
            dockRectFn = unsafeBitCast(rect, to: DockRectFn.self)
            if let cur = dlsym(handle, "SLSManagedDisplayGetCurrentSpace"), let type = dlsym(handle, "SLSSpaceGetType") {
                currentSpaceFn = unsafeBitCast(cur, to: CurrentSpaceFn.self)
                spaceTypeFn = unsafeBitCast(type, to: SpaceTypeFn.self)
            }
        }
    }

    /// Whether the Space currently shown on this display is a full-screen app's Space.
    /// Returns nil when the system can't tell; callers then fall back to window geometry.
    func isFullScreenSpace(displayUUID: String, now: Double) -> Bool? {
        if let c = spaceCache[displayUUID], now - c.time < 0.08 { return c.full }
        var result: Bool?
        if let mainConn, let currentSpaceFn, let spaceTypeFn {
            let conn = mainConn()
            let space = currentSpaceFn(conn, displayUUID as CFString)
            if space != 0 { result = spaceTypeFn(conn, space) == 4 }
        }
        spaceCache[displayUUID] = (now, result)
        return result
    }

    /// Returns the current window layout, polling the system only as often as needed:
    /// every frame while something is being dragged, a few times a second otherwise.
    func poll(now: Double) -> WindowSnapshot {
        let busy = NSEvent.pressedMouseButtons != 0 || now - lastChange < 0.7
        let interval = busy ? 0.008 : 0.25
        if now - lastPoll < interval { return snapshot }
        lastPoll = now

        if now - lastDockPoll > 0.5 {
            lastDockPoll = now
            dock = readDock()
        }

        let (windows, dockShown) = readWindows()
        // The system keeps reporting the Dock's place even while the Dock is hidden (full-screen
        // apps, games). The Dock's own window is on screen only while the Dock is visible, so
        // snow may settle on the Dock only then.
        var visibleDock: CGRect?
        if let dock, dockShown.contains(where: { $0.intersects(dock) }) { visibleDock = dock }
        if windows != snapshot.windows || visibleDock != snapshot.dock {
            snapshot.windows = windows
            snapshot.dock = visibleDock
            snapshot.version += 1
            lastChange = now
        }
        return snapshot
    }

    /// Which of these window ids still exist anywhere (other Spaces, minimized), as opposed to closed.
    func existing(of ids: Set<Int>) -> Set<Int> {
        guard !ids.isEmpty,
              let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else { return [] }
        var found = Set<Int>()
        for w in list {
            if let n = w[kCGWindowNumber as String] as? Int, ids.contains(n) { found.insert(n) }
        }
        return found
    }

    /// App windows front to back, plus the screens on which the Dock is currently shown.
    private func readWindows() -> ([WinInfo], [CGRect]) {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else { return ([], []) }
        if dockPid == nil || NSRunningApplication(processIdentifier: pid_t(dockPid!)) == nil {
            dockPid = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.dock").first.map { Int($0.processIdentifier) }
        }
        var out: [WinInfo] = []
        var dockShown: [CGRect] = []
        out.reserveCapacity(list.count)
        for w in list {
            guard let layer = w[kCGWindowLayer as String] as? Int,
                  let id = w[kCGWindowNumber as String] as? Int,
                  let pid = w[kCGWindowOwnerPID as String] as? Int, pid != ownPid,
                  let boundsDict = w[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: boundsDict) else { continue }
            let alpha = (w[kCGWindowAlpha as String] as? Double) ?? 1
            if alpha < 0.05 { continue }
            if pid == dockPid && layer == dockLevel {
                dockShown.append(rect)
                continue
            }
            if layer >= 0 && layer <= 10 {
                // Ordinary app windows and floating panels.
                if rect.width < 120 || rect.height < 60 { continue }
                out.append(WinInfo(id: id, rect: rect, isWidget: false))
            } else if layer < 0 {
                // Desktop widgets: small windows sitting on the wallpaper.
                if rect.width < 100 || rect.height < 100 || rect.width > 800 || rect.height > 800 { continue }
                out.append(WinInfo(id: id, rect: rect, isWidget: true))
            }
        }
        return (out, dockShown)
    }

    private func readDock() -> CGRect? {
        guard let primary = NSScreen.screens.first else { return nil }
        let primaryHeight = primary.frame.height

        var rect: CGRect?
        if let mainConn, let dockRectFn {
            var r = CGRect.zero
            var reason: Int32 = 0
            if dockRectFn(mainConn(), &r, &reason) == 0, r.width > 10, r.height > 10 { rect = r }
        }

        // Find the screen that has room reserved for the Dock. With auto-hide on there is none.
        for screen in NSScreen.screens {
            let f = screen.frame
            let v = screen.visibleFrame
            let bottom = v.minY - f.minY
            let left = v.minX - f.minX
            let right = f.maxX - v.maxX
            guard bottom > 8 || left > 8 || right > 8 else { continue }
            let screenRect = CGRect(x: f.minX, y: primaryHeight - f.maxY, width: f.width, height: f.height)
            if let rect, screenRect.intersects(rect) { return rect }
            if rect == nil, bottom > 8 {
                // Estimate: a centred bar sized from the Dock's own preferences, kept a little narrow.
                let dd = UserDefaults(suiteName: "com.apple.dock")
                let tile = CGFloat(dd?.double(forKey: "tilesize") ?? 48)
                let showRecents = (dd?.object(forKey: "show-recents") as? Bool) ?? true
                let apps: Int = dd?.array(forKey: "persistent-apps")?.count ?? 8
                let others: Int = dd?.array(forKey: "persistent-others")?.count ?? 1
                let recents: Int = showRecents ? (dd?.array(forKey: "recent-apps")?.count ?? 0) : 0
                let count: Int = apps + others + recents + 2
                let natural: CGFloat = CGFloat(count) * tile * 1.105 + 26
                let width: CGFloat = min(f.width - 40, natural * 0.92)
                return CGRect(x: screenRect.midX - width / 2, y: screenRect.maxY - bottom, width: width, height: bottom)
            }
        }
        return nil
    }
}
