import AppKit
import Carbon
import Combine

enum MusicSource: String {
    case spotify = "com.spotify.client"
    case music = "com.apple.Music"

    var appName: String { self == .spotify ? "Spotify" : L("Музыка", "Music") }
    /// Name used in AppleScript.
    var scriptName: String { self == .spotify ? "Spotify" : "Music" }
    var isRunning: Bool { !NSRunningApplication.runningApplications(withBundleIdentifier: rawValue).isEmpty }
    var appURL: URL? { NSWorkspace.shared.urlForApplication(withBundleIdentifier: rawValue) }
    var icon: NSImage? { appURL.map { NSWorkspace.shared.icon(forFile: $0.path) } }
}

/// What is playing right now. Published for the island UI.
final class NowPlaying: ObservableObject {
    @Published var source: MusicSource?
    @Published var trackID = ""
    @Published var title = ""
    @Published var artist = ""
    @Published var album = ""
    @Published var isPlaying = false
    /// Seconds.
    @Published var duration: Double = 0
    @Published var basePosition: Double = 0
    @Published var baseTime = Date()
    @Published var artwork: NSImage?
    @Published var accent = NSColor(calibratedRed: 0.55, green: 0.76, blue: 1, alpha: 1)
    @Published var lastActive = Date.distantPast
    /// The user refused to let Snowdrift control this app.
    @Published var permissionDenied: MusicSource?

    var hasTrack: Bool { !title.isEmpty }

    func position(at date: Date = Date()) -> Double {
        guard duration > 0 else { return 0 }
        let p = isPlaying ? basePosition + date.timeIntervalSince(baseTime) : basePosition
        return min(max(0, p), duration)
    }
}

/// Follows Spotify and Apple Music and controls them.
///
/// Reading what plays uses the notifications both apps broadcast, so it needs no permission.
/// Buttons and covers use AppleScript; macOS asks once whether Snowdrift may control the app.
final class MusicService {
    static let shared = MusicService()

    let now = NowPlaying()
    /// Called on the main thread when a new track starts.
    var onTrackChange: (() -> Void)?
    var artworkFromInternet = true

    private let queue = DispatchQueue(label: "snowdrift.music")
    private var started = false

    func start() {
        guard !started else { return }
        started = true
        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(self, selector: #selector(spotifyChanged(_:)),
                        name: Notification.Name("com.spotify.client.PlaybackStateChanged"), object: nil)
        dnc.addObserver(self, selector: #selector(musicChanged(_:)),
                        name: Notification.Name("com.apple.Music.playerInfo"), object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(appQuit(_:)),
                                                          name: NSWorkspace.didTerminateApplicationNotification, object: nil)
        // Pick up music that was already playing, but only from apps we may already talk to,
        // so starting Snowdrift never pops up a permission question.
        for source in [MusicSource.spotify, .music] where source.isRunning {
            queue.async { [weak self] in
                guard let self, self.permission(source) == noErr else { return }
                DispatchQueue.main.async { self.refresh(source) }
            }
        }
    }

    // MARK: - Notifications

    @objc private func spotifyChanged(_ note: Notification) {
        let u = note.userInfo ?? [:]
        let state = u["Player State"] as? String ?? ""
        if state == "Stopped" {
            clear(.spotify)
            return
        }
        let title = u["Name"] as? String ?? ""
        let artist = u["Artist"] as? String ?? ""
        let id = (u["Track ID"] as? String) ?? (title + "|" + artist)
        let duration = ((u["Duration"] as? NSNumber)?.doubleValue ?? 0) / 1000
        let position = (u["Playback Position"] as? NSNumber)?.doubleValue
        apply(.spotify, id: id, title: title, artist: artist, album: u["Album"] as? String ?? "",
              playing: state == "Playing", duration: duration, position: position)
    }

    @objc private func musicChanged(_ note: Notification) {
        let u = note.userInfo ?? [:]
        let state = u["Player State"] as? String ?? ""
        if state == "Stopped" {
            clear(.music)
            return
        }
        let title = u["Name"] as? String ?? ""
        let artist = u["Artist"] as? String ?? ""
        let id = (u["PersistentID"] as? NSNumber).map { "\($0)" } ?? (title + "|" + artist)
        let duration = ((u["Total Time"] as? NSNumber)?.doubleValue ?? 0) / 1000
        apply(.music, id: id, title: title, artist: artist, album: u["Album"] as? String ?? "",
              playing: state == "Playing", duration: duration, position: nil)
        // Music doesn't say where in the track it is; ask if allowed.
        syncPosition()
    }

    @objc private func appQuit(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
              let id = app.bundleIdentifier, let source = MusicSource(rawValue: id) else { return }
        clear(source)
    }

    private func clear(_ source: MusicSource) {
        guard now.source == source else { return }
        now.isPlaying = false
        now.title = ""
        now.artist = ""
        now.album = ""
        now.trackID = ""
        now.artwork = nil
        now.source = nil
    }

    private func apply(_ source: MusicSource, id: String, title: String, artist: String, album: String,
                       playing: Bool, duration: Double, position: Double?) {
        // Another app pausing doesn't take over from the one that is playing.
        if let current = now.source, current != source, now.isPlaying, !playing { return }
        let changed = id != now.trackID || source != now.source
        now.source = source
        now.title = title
        now.artist = artist
        now.album = album
        now.duration = duration
        if let position {
            now.basePosition = position
        } else if changed {
            now.basePosition = 0
        } else {
            now.basePosition = now.position()
        }
        now.baseTime = Date()
        now.isPlaying = playing
        now.lastActive = Date()
        if changed {
            now.trackID = id
            now.artwork = nil
            now.accent = NSColor(calibratedRed: 0.55, green: 0.76, blue: 1, alpha: 1)
            loadArtwork(source, trackID: id)
            if playing { onTrackChange?() }
        }
    }

    // MARK: - Controls

    private var target: MusicSource? {
        if let s = now.source, s.isRunning { return s }
        if MusicSource.spotify.isRunning { return .spotify }
        if MusicSource.music.isRunning { return .music }
        return nil
    }

    func playPause() {
        guard let source = target else {
            open(MusicSource.spotify.appURL != nil ? .spotify : .music)
            return
        }
        // Answer the click at once; the app's own notification confirms it right after.
        if now.source == source {
            now.basePosition = now.position()
            now.baseTime = Date()
            now.isPlaying.toggle()
        }
        send(source, "playpause")
    }

    func next() {
        guard let source = target else { return }
        send(source, "next track")
    }

    func previous() {
        guard let source = target else { return }
        send(source, source == .spotify ? "previous track" : "back track")
    }

    func seek(fraction: Double) {
        guard let source = target, now.duration > 0 else { return }
        let seconds = max(0, min(now.duration - 1, fraction * now.duration))
        now.basePosition = seconds
        now.baseTime = Date()
        send(source, "set player position to \(String(format: "%.2f", seconds))")
    }

    func open(_ source: MusicSource) {
        guard let url = source.appURL else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }

    func openAutomationSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Asks the app where in the track it is (only while the island is open).
    func syncPosition() {
        guard let source = now.source, source.isRunning else { return }
        let id = now.trackID
        run(source, "return player position") { [weak self] result in
            guard let self, let result, self.now.trackID == id else { return }
            let p = result.doubleValue
            if p.isFinite, p >= 0 {
                self.now.basePosition = p
                self.now.baseTime = Date()
            }
        }
    }

    /// Same, but only if the user has already allowed it (never asks).
    func syncPositionIfAllowed() {
        guard let source = now.source, source.isRunning else { return }
        queue.async { [weak self] in
            guard let self, self.permission(source) == noErr else { return }
            DispatchQueue.main.async { self.syncPosition() }
        }
    }

    // MARK: - AppleScript

    private func send(_ source: MusicSource, _ command: String) {
        run(source, command, completion: nil)
    }

    private func refresh(_ source: MusicSource) {
        let script = source == .spotify
            ? "return {name of current track, artist of current track, album of current track, id of current track, (duration of current track) / 1000, player position, player state as string}"
            : "return {name of current track, artist of current track, album of current track, persistent ID of current track, duration of current track, player position, player state as string}"
        run(source, script) { [weak self] r in
            guard let self, let r, r.numberOfItems >= 7 else { return }
            let state = r.atIndex(7)?.stringValue ?? ""
            guard state == "playing" || state == "paused" else { return }
            self.apply(source, id: r.atIndex(4)?.stringValue ?? "", title: r.atIndex(1)?.stringValue ?? "",
                       artist: r.atIndex(2)?.stringValue ?? "", album: r.atIndex(3)?.stringValue ?? "",
                       playing: state == "playing", duration: r.atIndex(5)?.doubleValue ?? 0,
                       position: r.atIndex(6)?.doubleValue)
        }
    }

    private func loadArtwork(_ source: MusicSource, trackID: String) {
        if source == .spotify {
            guard artworkFromInternet else { return }
            run(source, "return artwork url of current track") { [weak self] r in
                guard let self, let s = r?.stringValue, let url = URL(string: s),
                      url.scheme == "https", let host = url.host, host.hasSuffix("scdn.co") || host.hasSuffix("spotifycdn.com")
                else { return }
                URLSession.shared.dataTask(with: url) { data, _, _ in
                    guard let data, let image = NSImage(data: data) else { return }
                    DispatchQueue.main.async { self.setArtwork(image, trackID: trackID) }
                }.resume()
            }
        } else {
            run(source, "return raw data of artwork 1 of current track") { [weak self] r in
                guard let self, let data = r?.data, let image = NSImage(data: data) else { return }
                self.setArtwork(image, trackID: trackID)
            }
        }
    }

    private func setArtwork(_ image: NSImage, trackID: String) {
        guard now.trackID == trackID else { return }
        now.artwork = image
        now.accent = MusicService.accent(of: image)
    }

    /// Runs `tell application … to <body>` off the main thread, only if the app is running
    /// (so a button never launches an app by surprise).
    private func run(_ source: MusicSource, _ body: String, completion: ((NSAppleEventDescriptor?) -> Void)?) {
        guard source.isRunning else { return }
        let text = "tell application \"\(source.scriptName)\"\n\(body)\nend tell"
        queue.async { [weak self] in
            var error: NSDictionary?
            let result = NSAppleScript(source: text)?.executeAndReturnError(&error)
            let code = (error?[NSAppleScript.errorNumber] as? Int) ?? 0
            DispatchQueue.main.async {
                if code == -1743 { self?.now.permissionDenied = source }
                if error == nil, self?.now.permissionDenied == source { self?.now.permissionDenied = nil }
                completion?(error == nil ? result : nil)
            }
        }
    }

    /// May we send Apple events to this app? Never asks the user.
    private func permission(_ source: MusicSource) -> OSStatus {
        let target = NSAppleEventDescriptor(bundleIdentifier: source.rawValue)
        guard let desc = target.aeDesc else { return OSStatus(procNotFound) }
        return AEDeterminePermissionToAutomateTarget(desc, typeWildCard, typeWildCard, false)
    }

    // MARK: - Colour

    /// A bright, saturated version of the cover's average colour, for glows and bars.
    static func accent(of image: NSImage) -> NSColor {
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return NSColor(calibratedRed: 0.55, green: 0.76, blue: 1, alpha: 1)
        }
        let n = 8
        var px = [UInt8](repeating: 0, count: n * n * 4)
        guard let ctx = CGContext(data: &px, width: n, height: n, bitsPerComponent: 8, bytesPerRow: n * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            return NSColor(calibratedRed: 0.55, green: 0.76, blue: 1, alpha: 1)
        }
        ctx.interpolationQuality = .medium
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: n, height: n))
        // Weight colourful pixels more than greys, so a black cover with a red logo turns red.
        var r = 0.0, g = 0.0, b = 0.0, wsum = 0.0
        for i in stride(from: 0, to: px.count, by: 4) {
            let pr = Double(px[i]) / 255, pg = Double(px[i + 1]) / 255, pb = Double(px[i + 2]) / 255
            let sat = max(pr, pg, pb) - min(pr, pg, pb)
            let w = 0.05 + sat * sat * 4
            r += pr * w; g += pg * w; b += pb * w; wsum += w
        }
        let base = NSColor(srgbRed: r / wsum, green: g / wsum, blue: b / wsum, alpha: 1)
        var h: CGFloat = 0, s: CGFloat = 0, v: CGFloat = 0, a: CGFloat = 0
        base.usingColorSpace(.sRGB)?.getHue(&h, saturation: &s, brightness: &v, alpha: &a)
        return NSColor(hue: h, saturation: min(0.85, max(0.35, s * 1.3)), brightness: max(0.82, v), alpha: 1)
    }
}
