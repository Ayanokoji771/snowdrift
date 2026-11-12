import AppKit
import SwiftUI

// MARK: - Window

/// Borderless panel at the top of the screen, above the menu bar. It takes clicks only while
/// the cursor is over the island, so everything around it keeps working.
final class IslandPanel: NSPanel {
    init(frame: NSRect) {
        super.init(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.mainMenuWindow)) + 2)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        ignoresMouseEvents = true
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        isMovable = false
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class IslandHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

// MARK: - State and sizes

final class IslandState: ObservableObject {
    @Published var expanded = false
    /// The screen shows a full-screen app: no wings, the island only opens on hover.
    @Published var fullScreen = false
}

struct IslandMetrics {
    let notchWidth: CGFloat
    let notchHeight: CGFloat
    let hasNotch: Bool
    let wing: CGFloat = 46

    var expandedWidth: CGFloat { max(440, notchWidth + 260) }

    func expandedHeight(hasTrack: Bool) -> CGFloat { notchHeight + (hasTrack ? 152 : 104) }

    func compactWidth(wings: Bool) -> CGFloat { wings ? notchWidth + wing * 2 : notchWidth }
}

// MARK: - Controller

final class IslandController {
    let screen: NSScreen
    let metrics: IslandMetrics
    /// Centre of the notch, in global screen coordinates.
    let centerX: CGFloat
    private let panel: IslandPanel
    private let state = IslandState()
    private let service = MusicService.shared
    private var timer: Timer?
    private var hoverSince: Date?
    private var leftSince: Date?
    private var lastSync = Date.distantPast
    private let displayUUID: String?

    static let panelSize = NSSize(width: 640, height: 300)

    /// The built-in screen with the notch if there is one, otherwise the main screen.
    static func pickScreen() -> NSScreen? {
        NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 }) ?? NSScreen.screens.first
    }

    init(screen: NSScreen) {
        self.screen = screen
        let f = screen.frame
        if screen.safeAreaInsets.top > 0, let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
            let width = right.minX - left.maxX
            metrics = IslandMetrics(notchWidth: width, notchHeight: screen.safeAreaInsets.top, hasNotch: true)
            centerX = left.maxX + width / 2
        } else {
            let bar = max(24, f.maxY - screen.visibleFrame.maxY)
            metrics = IslandMetrics(notchWidth: 0, notchHeight: bar, hasNotch: false)
            centerX = f.midX
        }
        let size = IslandController.panelSize
        panel = IslandPanel(frame: NSRect(x: centerX - size.width / 2, y: f.maxY - size.height, width: size.width, height: size.height))
        if let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
           let uuid = CGDisplayCreateUUIDFromDisplayID(number)?.takeRetainedValue() {
            displayUUID = CFUUIDCreateString(nil, uuid) as String?
        } else {
            displayUUID = nil
        }

        let root = IslandView(now: service.now, state: state, m: metrics, service: service)
        let host = IslandHostingView(rootView: root)
        host.frame = NSRect(origin: .zero, size: size)
        panel.contentView = host
    }

    /// Bottom edge of the island, in points from the top of its screen (for the snow puff).
    var bottomFromTop: CGFloat {
        state.expanded ? metrics.expandedHeight(hasTrack: service.now.hasTrack) : metrics.notchHeight
    }

    func show(_ on: Bool) {
        if on {
            panel.orderFrontRegardless()
            if timer == nil {
                let t = Timer(timeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.tick() }
                RunLoop.main.add(t, forMode: .common)
                timer = t
            }
        } else {
            timer?.invalidate()
            timer = nil
            state.expanded = false
            panel.orderOut(nil)
        }
    }

    func close() {
        show(false)
        panel.close()
    }

    private var wingsVisible: Bool {
        let now = service.now
        guard now.hasTrack, !state.fullScreen else { return false }
        return now.isPlaying || Date().timeIntervalSince(now.lastActive) < 600
    }

    private func tick() {
        let now = Date()
        let m = metrics
        let f = screen.frame
        if let uuid = displayUUID {
            let fs = WindowTracker.shared.isFullScreenSpace(displayUUID: uuid, now: CACurrentMediaTime()) == true
            if fs != state.fullScreen { state.fullScreen = fs }
        }

        let mouse = NSEvent.mouseLocation
        let wings = wingsVisible
        let w = state.expanded ? m.expandedWidth : m.compactWidth(wings: wings)
        let h = state.expanded ? m.expandedHeight(hasTrack: service.now.hasTrack) : m.notchHeight
        var rect = NSRect(x: centerX - w / 2, y: f.maxY - h, width: w, height: h)
        // A little extra room so the island doesn't flicker shut at its edges.
        rect = state.expanded ? rect.insetBy(dx: -12, dy: -14) : rect.insetBy(dx: -4, dy: -2)
        // Without a notch and without music there is nothing to open.
        let active = m.hasNotch || wings || state.expanded
        let inside = active && w > 1 && rect.contains(mouse)

        if inside {
            leftSince = nil
            if hoverSince == nil { hoverSince = now }
            let delay = service.now.hasTrack ? 0.12 : 0.35
            if !state.expanded, let since = hoverSince, now.timeIntervalSince(since) > delay,
               NSEvent.pressedMouseButtons == 0 {
                state.expanded = true
            }
        } else {
            hoverSince = nil
            if state.expanded {
                if leftSince == nil { leftSince = now }
                if let since = leftSince, now.timeIntervalSince(since) > 0.3 { state.expanded = false }
            }
        }
        if panel.ignoresMouseEvents == inside { panel.ignoresMouseEvents = !inside }

        // Keep the progress bar honest while the island is open.
        if state.expanded, now.timeIntervalSince(lastSync) > 2 {
            lastSync = now
            service.syncPositionIfAllowed()
        }
    }
}

// MARK: - Views

private func frac(_ x: Double) -> Double { x - floor(x) }
private func hash(_ i: Int, _ k: Double) -> Double { frac(sin(Double(i) * 12.9898 + k * 78.233) * 43758.5453) }

/// Black body with a flat top that melts into the screen edge, flared top corners and round bottom corners.
struct IslandShape: Shape {
    var bottomRadius: CGFloat
    var flare: CGFloat

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(bottomRadius, flare) }
        set {
            bottomRadius = newValue.first
            flare = newValue.second
        }
    }

    func path(in rect: CGRect) -> Path {
        var p = Path()
        let w = rect.width, h = rect.height, f = flare
        let r = max(0, min(bottomRadius, (w - 2 * f) / 2, h - f))
        p.move(to: CGPoint(x: 0, y: 0))
        p.addQuadCurve(to: CGPoint(x: f, y: f), control: CGPoint(x: f, y: 0))
        p.addLine(to: CGPoint(x: f, y: h - r))
        p.addQuadCurve(to: CGPoint(x: f + r, y: h), control: CGPoint(x: f, y: h))
        p.addLine(to: CGPoint(x: w - f - r, y: h))
        p.addQuadCurve(to: CGPoint(x: w - f, y: h - r), control: CGPoint(x: w - f, y: h))
        p.addLine(to: CGPoint(x: w - f, y: f))
        p.addQuadCurve(to: CGPoint(x: w, y: 0), control: CGPoint(x: w - f, y: 0))
        p.closeSubpath()
        return p
    }
}

struct IslandView: View {
    @ObservedObject var now: NowPlaying
    @ObservedObject var state: IslandState
    let m: IslandMetrics
    let service: MusicService

    private var wings: Bool {
        now.hasTrack && !state.fullScreen && (now.isPlaying || Date().timeIntervalSince(now.lastActive) < 600)
    }

    private var size: CGSize {
        state.expanded ? CGSize(width: m.expandedWidth, height: m.expandedHeight(hasTrack: now.hasTrack))
                       : CGSize(width: m.compactWidth(wings: wings), height: m.notchHeight)
    }

    private var flare: CGFloat { state.expanded ? 14 : 7 }
    private var accent: Color { Color(nsColor: now.accent) }
    private let spring = Animation.spring(response: 0.42, dampingFraction: 0.78)

    var body: some View {
        let shape = IslandShape(bottomRadius: state.expanded ? 30 : 11, flare: flare)
        let visible = state.expanded || wings || m.hasNotch
        ZStack(alignment: .top) {
            shape.fill(LinearGradient(colors: [.black, Color(red: 0.02, green: 0.04, blue: 0.09)],
                                      startPoint: .top, endPoint: .bottom))
            if state.expanded {
                InnerSnow()
                    .clipShape(shape)
                    .transition(.opacity)
            }
            // Frosty rim, brighter towards the bottom.
            shape.stroke(LinearGradient(colors: [.white.opacity(0), .white.opacity(state.expanded ? 0.22 : 0.1)],
                                        startPoint: .top, endPoint: .bottom), lineWidth: 1)
            content
                .padding(.horizontal, flare)
        }
        .frame(width: size.width + flare * 2, height: size.height)
        .overlay(alignment: .bottom) {
            if state.expanded || wings {
                Icicles(count: state.expanded ? 16 : 7, maxLength: state.expanded ? 17 : 7, drips: state.expanded && now.isPlaying)
                    .frame(width: max(10, size.width - (state.expanded ? 64 : 26)), height: state.expanded ? 60 : 22)
                    .offset(y: state.expanded ? 60 : 22)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
        }
        .shadow(color: accent.opacity(state.expanded && now.isPlaying ? 0.4 : 0), radius: 22, y: 4)
        .shadow(color: .black.opacity(state.expanded ? 0.3 : 0), radius: 10, y: 5)
        .opacity(visible ? 1 : 0)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(spring, value: state.expanded)
        .animation(spring, value: wings)
        .animation(spring, value: now.hasTrack)
        .animation(.easeInOut(duration: 0.6), value: now.accent)
    }

    @ViewBuilder private var content: some View {
        if state.expanded {
            ExpandedIsland(now: now, m: m, service: service, accent: accent)
                .transition(.asymmetric(insertion: .opacity.animation(.easeOut(duration: 0.22).delay(0.12)),
                                        removal: .opacity.animation(.easeIn(duration: 0.1))))
        } else if wings {
            CompactIsland(now: now, m: m, accent: accent)
                .transition(.opacity.animation(.easeInOut(duration: 0.2)))
        }
    }
}

/// Wings beside the notch: cover on the left, equalizer on the right.
struct CompactIsland: View {
    @ObservedObject var now: NowPlaying
    let m: IslandMetrics
    let accent: Color

    var body: some View {
        HStack(spacing: 0) {
            Artwork(image: now.artwork, size: 22, radius: 6, accent: accent, snowCap: false)
                .frame(width: m.wing)
            Spacer(minLength: m.notchWidth)
            Equalizer(playing: now.isPlaying, color: accent, bars: 4, maxHeight: 14)
                .frame(width: m.wing)
        }
        .frame(height: m.notchHeight)
    }
}

struct ExpandedIsland: View {
    @ObservedObject var now: NowPlaying
    let m: IslandMetrics
    let service: MusicService
    let accent: Color

    var body: some View {
        VStack(spacing: 0) {
            // The row beside the notch.
            HStack(spacing: 6) {
                Image(systemName: "snowflake")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(accent)
                Text("Snowdrift")
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white.opacity(0.55))
                Spacer(minLength: m.notchWidth + 16)
                if let source = now.source {
                    if let icon = source.icon {
                        Image(nsImage: icon).resizable().frame(width: 14, height: 14)
                    }
                    Text(source.appName)
                        .font(.system(size: 11, weight: .medium, design: .rounded))
                        .foregroundStyle(.white.opacity(0.55))
                }
            }
            .padding(.horizontal, 20)
            .frame(height: m.notchHeight)

            if now.hasTrack {
                HStack(spacing: 14) {
                    Artwork(image: now.artwork, size: 64, radius: 14, accent: accent, snowCap: true)
                    VStack(alignment: .leading, spacing: 3) {
                        Marquee(text: now.title, font: .systemFont(ofSize: 15, weight: .semibold))
                            .foregroundStyle(.white)
                        Text(now.artist)
                            .font(.system(size: 13))
                            .foregroundStyle(.white.opacity(0.6))
                            .lineLimit(1)
                    }
                    Spacer(minLength: 6)
                    Equalizer(playing: now.isPlaying, color: accent, bars: 5, maxHeight: 24)
                }
                .padding(.horizontal, 22)
                .padding(.top, 10)

                ProgressRow(now: now, service: service, accent: accent)
                    .padding(.horizontal, 22)
                    .padding(.top, 10)

                ZStack {
                    HStack(spacing: 38) {
                        IslandButton(symbol: "backward.fill", size: 17) { service.previous() }
                        IslandButton(symbol: now.isPlaying ? "pause.fill" : "play.fill", size: 26) { service.playPause() }
                        IslandButton(symbol: "forward.fill", size: 17) { service.next() }
                    }
                    if let denied = now.permissionDenied {
                        HStack {
                            Spacer()
                            Button {
                                service.openAutomationSettings()
                            } label: {
                                Label(L("Разрешить \(denied.appName)", "Allow \(denied.appName)"), systemImage: "lock.open")
                                    .font(.system(size: 10.5, weight: .medium))
                                    .padding(.horizontal, 8)
                                    .padding(.vertical, 4)
                                    .background(Capsule().fill(.white.opacity(0.12)))
                                    .foregroundStyle(.white.opacity(0.8))
                            }
                            .buttonStyle(.plain)
                        }
                        .padding(.horizontal, 18)
                    }
                }
                .padding(.top, 4)
            } else {
                EmptyIsland(service: service)
            }
            Spacer(minLength: 0)
        }
    }
}

/// Nothing is playing: offer to open a player.
struct EmptyIsland: View {
    let service: MusicService

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 6) {
                Image(systemName: "music.note")
                Text(L("Сейчас ничего не играет", "Nothing is playing"))
            }
            .font(.system(size: 13, weight: .medium, design: .rounded))
            .foregroundStyle(.white.opacity(0.65))
            HStack(spacing: 10) {
                ForEach([MusicSource.spotify, .music], id: \.rawValue) { source in
                    if source.appURL != nil {
                        Button {
                            service.open(source)
                        } label: {
                            HStack(spacing: 6) {
                                if let icon = source.icon {
                                    Image(nsImage: icon).resizable().frame(width: 18, height: 18)
                                }
                                Text(source.appName)
                                    .font(.system(size: 12, weight: .semibold, design: .rounded))
                            }
                            .padding(.horizontal, 12)
                            .padding(.vertical, 6)
                            .background(Capsule().fill(.white.opacity(0.1)))
                            .overlay(Capsule().stroke(.white.opacity(0.14), lineWidth: 1))
                            .foregroundStyle(.white.opacity(0.9))
                        }
                        .buttonStyle(PressStyle())
                    }
                }
            }
        }
        .padding(.top, 26)
    }
}

/// Album cover with a frosty rim and, in the big view, a little cap of snow on top.
struct Artwork: View {
    let image: NSImage?
    let size: CGFloat
    let radius: CGFloat
    let accent: Color
    let snowCap: Bool

    var body: some View {
        ZStack {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                LinearGradient(colors: [accent.opacity(0.85), Color(red: 0.08, green: 0.12, blue: 0.3)],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
                Image(systemName: "snowflake")
                    .font(.system(size: size * 0.42, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.9))
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).stroke(.white.opacity(0.2), lineWidth: 1))
        .overlay(alignment: .top) {
            if snowCap {
                SnowCap()
                    .fill(LinearGradient(colors: [.white, Color(red: 0.86, green: 0.92, blue: 1)],
                                         startPoint: .top, endPoint: .bottom))
                    .frame(width: size + 6, height: size * 0.2)
                    .offset(y: -size * 0.09)
                    .shadow(color: .black.opacity(0.25), radius: 1.5, y: 1)
            }
        }
        .shadow(color: accent.opacity(snowCap ? 0.4 : 0.25), radius: snowCap ? 7 : 3)
        .animation(.easeInOut(duration: 0.35), value: image)
    }
}

/// A soft drift of snow, wider than what it sits on, with a lumpy top.
struct SnowCap: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        let w = rect.width, h = rect.height
        p.move(to: CGPoint(x: 0, y: h * 0.78))
        p.addCurve(to: CGPoint(x: w * 0.28, y: h * 0.2), control1: CGPoint(x: w * 0.02, y: h * 0.25), control2: CGPoint(x: w * 0.14, y: h * 0.18))
        p.addCurve(to: CGPoint(x: w * 0.6, y: h * 0.14), control1: CGPoint(x: w * 0.4, y: h * 0.22), control2: CGPoint(x: w * 0.48, y: 0))
        p.addCurve(to: CGPoint(x: w, y: h * 0.75), control1: CGPoint(x: w * 0.78, y: h * 0.26), control2: CGPoint(x: w * 0.98, y: h * 0.2))
        // Underside dips a little between two hanging lumps.
        p.addCurve(to: CGPoint(x: w * 0.7, y: h * 0.86), control1: CGPoint(x: w, y: h * 1.05), control2: CGPoint(x: w * 0.82, y: h * 1.1))
        p.addCurve(to: CGPoint(x: w * 0.3, y: h * 0.9), control1: CGPoint(x: w * 0.6, y: h * 0.7), control2: CGPoint(x: w * 0.42, y: h * 1.15))
        p.addCurve(to: CGPoint(x: 0, y: h * 0.78), control1: CGPoint(x: w * 0.18, y: h * 0.7), control2: CGPoint(x: w * 0.02, y: h * 1.05))
        p.closeSubpath()
        return p
    }
}

/// Bars with snowy tips that dance while music plays.
struct Equalizer: View {
    let playing: Bool
    let color: Color
    let bars: Int
    let maxHeight: CGFloat

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !playing)) { ctx in
            let t = ctx.date.timeIntervalSinceReferenceDate
            HStack(alignment: .center, spacing: 2.5) {
                ForEach(0..<bars, id: \.self) { i in
                    let d = Double(i)
                    let v = playing
                        ? 0.22 + 0.78 * abs(sin(t * (2.6 + d * 0.9) + d * 1.7)) * (0.55 + 0.45 * abs(sin(t * 1.3 + d * 2.1)))
                        : 0.16
                    Capsule()
                        .fill(LinearGradient(colors: [.white, color], startPoint: .top, endPoint: .bottom))
                        .frame(width: 3, height: max(3, maxHeight * v))
                        .shadow(color: color.opacity(playing ? 0.7 : 0), radius: 3)
                }
            }
            .frame(height: maxHeight)
            .animation(.easeOut(duration: 0.12), value: playing)
        }
    }
}

/// One line of text that slowly scrolls when it doesn't fit.
struct Marquee: View {
    let text: String
    let font: NSFont

    var body: some View {
        GeometryReader { geo in
            let textWidth = (text as NSString).size(withAttributes: [.font: font]).width
            if textWidth <= geo.size.width {
                Text(text).font(Font(font)).lineLimit(1)
            } else {
                let gap: CGFloat = 36
                let travel = Double(textWidth + gap)
                let period = travel / 28 + 2.5
                TimelineView(.animation(minimumInterval: 1.0 / 30)) { ctx in
                    let t = ctx.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: period)
                    let offset = t < 2.5 ? 0 : (t - 2.5) * 28
                    HStack(spacing: gap) {
                        Text(text).font(Font(font)).lineLimit(1).fixedSize()
                        Text(text).font(Font(font)).lineLimit(1).fixedSize()
                    }
                    .offset(x: -CGFloat(offset))
                }
                .frame(width: geo.size.width, alignment: .leading)
                .clipped()
                .mask(LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: .black, location: 0.04),
                                             .init(color: .black, location: 0.9), .init(color: .clear, location: 1)],
                                     startPoint: .leading, endPoint: .trailing))
            }
        }
        .frame(height: font.ascender - font.descender + 2)
    }
}

/// Elapsed time, an icy bar with a spinning snowflake, and time left. Click to jump.
struct ProgressRow: View {
    @ObservedObject var now: NowPlaying
    let service: MusicService
    let accent: Color

    private func format(_ s: Double) -> String {
        let t = max(0, Int(s.rounded(.down)))
        return String(format: "%d:%02d", t / 60, t % 60)
    }

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 20, paused: !now.isPlaying)) { ctx in
            let pos = now.position(at: ctx.date)
            let dur = now.duration
            let f = dur > 0 ? pos / dur : 0
            HStack(spacing: 8) {
                Text(format(pos))
                    .frame(width: 36, alignment: .trailing)
                GeometryReader { g in
                    let w = g.size.width
                    ZStack(alignment: .leading) {
                        Capsule().fill(.white.opacity(0.14)).frame(height: 4)
                        Capsule()
                            .fill(LinearGradient(colors: [accent.opacity(0.8), .white], startPoint: .leading, endPoint: .trailing))
                            .frame(width: max(4, w * f), height: 4)
                            .shadow(color: accent.opacity(0.8), radius: 3)
                        Image(systemName: "snowflake")
                            .font(.system(size: 11, weight: .bold))
                            .foregroundStyle(.white)
                            .shadow(color: accent, radius: 4)
                            .rotationEffect(.degrees(f * 720))
                            .offset(x: w * f - 6)
                    }
                    .frame(height: g.size.height)
                    .contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 0).onEnded { v in
                        service.seek(fraction: Double(max(0, min(1, v.location.x / max(1, w)))))
                    })
                }
                .frame(height: 14)
                Text("-" + format(max(0, dur - pos)))
                    .frame(width: 40, alignment: .leading)
            }
            .font(.system(size: 10.5, weight: .medium).monospacedDigit())
            .foregroundStyle(.white.opacity(0.55))
        }
    }
}

struct PressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.86 : 1)
            .opacity(configuration.isPressed ? 0.7 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.6), value: configuration.isPressed)
    }
}

struct IslandButton: View {
    let symbol: String
    let size: CGFloat
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(.white)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: 44, height: 36)
                .contentShape(Rectangle())
        }
        .buttonStyle(PressStyle())
    }
}

/// Slow snowfall inside the open island.
struct InnerSnow: View {
    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30)) { ctx in
            Canvas { c, size in
                let t = ctx.date.timeIntervalSinceReferenceDate
                for i in 0..<36 {
                    let speed = 7 + hash(i, 1) * 15
                    let r = 0.6 + hash(i, 2) * 1.5
                    let span = Double(size.height) + 10
                    let y = (hash(i, 3) * span + t * speed).truncatingRemainder(dividingBy: span) - 5
                    let x = hash(i, 4) * Double(size.width) + sin(t * (0.5 + hash(i, 5)) + Double(i)) * 7
                    let a = 0.1 + hash(i, 6) * 0.35
                    c.fill(Path(ellipseIn: CGRect(x: x - r, y: y - r, width: r * 2, height: r * 2)), with: .color(.white.opacity(a)))
                }
            }
        }
    }
}

/// Icicles hanging from the island's lower edge, with a moving glint and, while music plays, drips.
struct Icicles: View {
    let count: Int
    let maxLength: CGFloat
    let drips: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30)) { ctx in
            Canvas { c, size in
                let t = ctx.date.timeIntervalSinceReferenceDate
                for i in 0..<count {
                    let u = (Double(i) + 0.3 + hash(i, 7) * 0.4) / Double(count)
                    let x = u * Double(size.width)
                    let len = Double(maxLength) * (0.3 + 0.7 * hash(i, 8))
                    let w = 1.6 + 2.2 * hash(i, 9) * Double(maxLength) / 17
                    var p = Path()
                    p.move(to: CGPoint(x: x - w, y: -1))
                    p.addLine(to: CGPoint(x: x + w, y: -1))
                    p.addQuadCurve(to: CGPoint(x: x + 0.3, y: len), control: CGPoint(x: x + w * 0.5, y: len * 0.55))
                    p.addQuadCurve(to: CGPoint(x: x - w, y: -1), control: CGPoint(x: x - w * 0.4, y: len * 0.5))
                    c.fill(p, with: .linearGradient(Gradient(colors: [Color(red: 0.92, green: 0.97, blue: 1).opacity(0.95),
                                                                      Color(red: 0.62, green: 0.82, blue: 1).opacity(0.25)]),
                                                    startPoint: CGPoint(x: x, y: 0), endPoint: CGPoint(x: x, y: len)))
                    // A glint running along the icicles.
                    let glint = max(0, sin(t * 1.8 - u * 9))
                    if glint > 0.75 {
                        var g = Path()
                        g.move(to: CGPoint(x: x - w * 0.35, y: 0.5))
                        g.addLine(to: CGPoint(x: x - w * 0.05, y: len * 0.7))
                        c.stroke(g, with: .color(.white.opacity((glint - 0.75) * 3.2)), lineWidth: 0.9)
                    }
                    // Drops.
                    if drips && hash(i, 10) > 0.45 {
                        let period = 2.2 + hash(i, 11) * 3
                        let phase = (t / period + hash(i, 12)).truncatingRemainder(dividingBy: 1)
                        let grow = min(1, phase / 0.55)
                        if phase < 0.55 {
                            let r = 1.3 * grow
                            c.fill(Path(ellipseIn: CGRect(x: x - r + 0.3, y: len - r * 0.4, width: r * 2, height: r * 2.2)),
                                   with: .color(Color(red: 0.8, green: 0.92, blue: 1).opacity(0.9)))
                        } else {
                            let fall = (phase - 0.55) / 0.45
                            let y = len + fall * fall * Double(size.height - CGFloat(len))
                            c.fill(Path(ellipseIn: CGRect(x: x - 1, y: y, width: 2, height: 2.6)),
                                   with: .color(Color(red: 0.8, green: 0.92, blue: 1).opacity(0.9 * (1 - fall))))
                        }
                    }
                }
            }
        }
    }
}
