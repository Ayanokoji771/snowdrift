import Foundation

/// Tiny two-language helper: Russian when the system language is Russian, English otherwise.
enum Lang {
    static let isRu: Bool = (Locale.preferredLanguages.first ?? "en").hasPrefix("ru")
}

func L(_ ru: String, _ en: String) -> String { Lang.isRu ? ru : en }

/// All user settings. Stored locally in UserDefaults, never sent anywhere.
final class Settings {
    static let shared = Settings()
    static let changed = Notification.Name("SnowdriftSettingsChanged")

    private let d = UserDefaults.standard

    private init() {
        d.register(defaults: [
            "enabled": true,
            "intensity": 1,
            "wind": 1,
            "drifts": 2,
            "tool": 0,
            "garland": 2,
            "depth": true,
            "fullScreenMode": 1,
            "flakeSize": 1,
            "melt": false,
            "tree": true,
            "frost": false,
            "sparkle": true,
            "newYearFireworks": true,
            "island": true,
            "islandArtwork": true,
            "islandPuff": true,
            "checkUpdates": true,
        ])
        // Older builds had an on/off switch for full screen.
        if d.object(forKey: "fullScreenMode") == nil, d.object(forKey: "hideInFullScreen") != nil {
            d.set(d.bool(forKey: "hideInFullScreen") ? 1 : 0, forKey: "fullScreenMode")
        }
    }

    private func set(_ value: Any, _ key: String) {
        d.set(value, forKey: key)
        NotificationCenter.default.post(name: Settings.changed, object: nil)
    }

    /// Snow on / paused.
    var enabled: Bool {
        get { d.bool(forKey: "enabled") }
        set { set(newValue, "enabled") }
    }

    /// 0 light snow, 1 steady snowfall, 2 blizzard.
    var intensity: Int {
        get { min(2, max(0, d.integer(forKey: "intensity"))) }
        set { set(newValue, "intensity") }
    }

    /// -2 strong left ... 0 calm ... 2 strong right.
    var wind: Int {
        get { min(2, max(-2, d.integer(forKey: "wind"))) }
        set { set(newValue, "wind") }
    }

    /// 0 no drifts, 1 low, 2 medium, 3 high.
    var drifts: Int {
        get { min(3, max(0, d.integer(forKey: "drifts"))) }
        set { set(newValue, "drifts") }
    }

    /// 0 mitten (cursor sweeps snow), 1 snowballs (cursor rolls balls), 2 cursor leaves snow alone.
    var tool: Int {
        get { min(2, max(0, d.integer(forKey: "tool"))) }
        set { set(newValue, "tool") }
    }

    /// 0 off, 1 steady, 2 twinkle, 3 running lights.
    var garland: Int {
        get { min(3, max(0, d.integer(forKey: "garland"))) }
        set { set(newValue, "garland") }
    }

    /// Small far flakes fall behind windows.
    var depth: Bool {
        get { d.bool(forKey: "depth") }
        set { set(newValue, "depth") }
    }

    /// 0 snow stays over full-screen apps, 1 snow hides, 2 also hides over maximized windows.
    var fullScreenMode: Int {
        get { min(2, max(0, d.integer(forKey: "fullScreenMode"))) }
        set { set(newValue, "fullScreenMode") }
    }

    /// 0 small, 1 normal, 2 large.
    var flakeSize: Int {
        get { min(2, max(0, d.integer(forKey: "flakeSize"))) }
        set { set(newValue, "flakeSize") }
    }

    /// Thaw: drifts and snowmen slowly melt.
    var melt: Bool {
        get { d.bool(forKey: "melt") }
        set { set(newValue, "melt") }
    }

    /// Christmas tree in the bottom corner of the main screen.
    var tree: Bool {
        get { d.bool(forKey: "tree") }
        set { set(newValue, "tree") }
    }

    /// Frost patterns in the screen corners.
    var frost: Bool {
        get { d.bool(forKey: "frost") }
        set { set(newValue, "frost") }
    }

    /// Glints on the drifts.
    var sparkle: Bool {
        get { d.bool(forKey: "sparkle") }
        set { set(newValue, "sparkle") }
    }

    /// Fireworks at New Year's midnight.
    var newYearFireworks: Bool {
        get { d.bool(forKey: "newYearFireworks") }
        set { set(newValue, "newYearFireworks") }
    }

    /// 0 snow over every app, 1 snow only on the desktop (hidden over all apps except `shownApps`).
    var snowWhere: Int {
        get { min(1, max(0, d.integer(forKey: "snowWhere"))) }
        set { set(newValue, "snowWhere") }
    }

    /// In desktop-only mode, apps over which the snow still shows: bundle id -> display name.
    var shownApps: [String: String] {
        get { (d.dictionary(forKey: "shownApps") as? [String: String]) ?? [:] }
        set { set(newValue, "shownApps") }
    }

    /// The music island at the notch.
    var island: Bool {
        get { d.bool(forKey: "island") }
        set { set(newValue, "island") }
    }

    /// Spotify covers come from Spotify's servers (the only thing Snowdrift loads from the internet).
    var islandArtwork: Bool {
        get { d.bool(forKey: "islandArtwork") }
        set { set(newValue, "islandArtwork") }
    }

    /// The island breathes out snow when a new track starts.
    var islandPuff: Bool {
        get { d.bool(forKey: "islandPuff") }
        set { set(newValue, "islandPuff") }
    }

    /// Apps over which the snow hides: bundle id -> display name.
    var hiddenApps: [String: String] {
        get { (d.dictionary(forKey: "hiddenApps") as? [String: String]) ?? [:] }
        set { set(newValue, "hiddenApps") }
    }

    /// When a timed pause ends (seconds since 1970), or 0.
    var pauseUntil: Double {
        get { d.double(forKey: "pauseUntil") }
        set { d.set(newValue, forKey: "pauseUntil") }
    }

    /// How many snowflakes have settled, ever.
    var totalFlakes: Int {
        get { d.integer(forKey: "totalFlakes") }
        set { d.set(newValue, forKey: "totalFlakes") }
    }

    var welcomeShown: Bool {
        get { d.bool(forKey: "welcomeShown") }
        set { d.set(newValue, forKey: "welcomeShown") }
    }

    /// Ask GitHub once a day whether a new version is out.
    var checkUpdates: Bool {
        get { d.bool(forKey: "checkUpdates") }
        set { set(newValue, "checkUpdates") }
    }

    var lastUpdateCheck: Double {
        get { d.double(forKey: "lastUpdateCheck") }
        set { d.set(newValue, forKey: "lastUpdateCheck") }
    }

    /// The newest version the person was already told about.
    var notifiedVersion: String {
        get { d.string(forKey: "notifiedVersion") ?? "" }
        set { d.set(newValue, forKey: "notifiedVersion") }
    }
}

/// Small fast random generator for the simulation.
struct Rng {
    private var s: UInt64

    init(seed: UInt64 = UInt64.random(in: 1...UInt64.max)) { s = seed == 0 ? 0x9E3779B97F4A7C15 : seed }

    mutating func next() -> UInt64 {
        s ^= s << 13
        s ^= s >> 7
        s ^= s << 17
        return s
    }

    /// Uniform in 0..<1.
    mutating func unit() -> Float { Float(next() >> 40) / Float(1 << 24) }

    mutating func range(_ a: Float, _ b: Float) -> Float { a + (b - a) * unit() }

    mutating func int(_ n: Int) -> Int { n <= 0 ? 0 : Int(next() % UInt64(n)) }
}
