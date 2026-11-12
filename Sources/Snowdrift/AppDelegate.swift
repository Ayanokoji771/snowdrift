import AppKit
import Carbon
import Metal
import ServiceManagement

/// Menu item that runs a closure.
final class MenuAction: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, key: String = "", checked: Bool = false, _ handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: key)
        target = self
        state = checked ? .on : .off
    }

    required init(coder: NSCoder) { fatalError("not used") }

    @objc private func run() { handler() }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var renderer: Renderer?
    private var overlays: [Overlay] = []
    private var statusItem: NSStatusItem!
    private var hotKeys: [HotKey] = []
    private let settings = Settings.shared
    private var rebuildPending = false
    private var ticker: Timer?
    private var newYearShowRunning = false
    private var pendingFlakes = 0
    /// The app the user was working in (the menu bar menu doesn't change it).
    private var frontApp: NSRunningApplication?
    private var island: IslandController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let device = MTLCreateSystemDefaultDevice(), let renderer = Renderer(device: device) else {
            let alert = NSAlert()
            alert.messageText = L("Snowdrift не может запуститься", "Snowdrift can't start")
            alert.informativeText = L("Не удалось включить графику Metal на этом Mac.", "Metal graphics are not available on this Mac.")
            alert.runModal()
            NSApp.terminate(nil)
            return
        }
        self.renderer = renderer

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "snowflake", accessibilityDescription: "Snowdrift")
            button.image?.isTemplate = true
            button.toolTip = "Snowdrift"
        }
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        let nc = NotificationCenter.default
        nc.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
        nc.addObserver(self, selector: #selector(settingsChanged), name: Settings.changed, object: nil)
        nc.addObserver(self, selector: #selector(settingsChanged), name: Notification.Name.NSProcessInfoPowerStateDidChange, object: nil)

        // Global shortcuts: they work in any app, even when the menu bar is hidden.
        let mods = UInt32(controlKey | optionKey)
        let keys: [(Int, () -> Void)] = [
            (kVK_ANSI_S, { [weak self] in self?.toggleEnabled() }),
            (kVK_ANSI_A, { [weak self] in self?.toggleDesktopOnly() }),
            (kVK_ANSI_H, { [weak self] in self?.toggleFrontApp() }),
            (kVK_ANSI_D, { [weak self] in self?.shovel() }),
            (kVK_ANSI_F, { [weak self] in self?.fireworks() }),
        ]
        for (code, action) in keys {
            if let hk = HotKey(keyCode: UInt32(code), modifiers: mods, action: action) { hotKeys.append(hk) }
        }
        if LiveDebug.dir != nil { print("hotkeys registered: \(hotKeys.count) of \(keys.count)") }

        let wnc = NSWorkspace.shared.notificationCenter
        wnc.addObserver(self, selector: #selector(appActivated(_:)), name: NSWorkspace.didActivateApplicationNotification, object: nil)
        frontApp = NSWorkspace.shared.frontmostApplication
        updateHiddenForFrontApp()

        // Once a second: timed pause, New Year fireworks, snowflake counter.
        ticker = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.tick() }

        rebuildOverlays()

        MusicService.shared.onTrackChange = { [weak self] in self?.islandPuff() }
        MusicService.shared.start()

        if !settings.welcomeShown {
            settings.welcomeShown = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in self?.showHelp() }
        }
    }

    // MARK: - Overlays

    @objc private func screensChanged() {
        // Displays often report several changes in a row; rebuild once they settle.
        guard !rebuildPending else { return }
        rebuildPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in
            self?.rebuildPending = false
            self?.rebuildOverlays()
        }
    }

    private func rebuildOverlays() {
        guard let renderer else { return }
        overlays.forEach { $0.close() }
        overlays = NSScreen.screens.map { Overlay(screen: $0, renderer: renderer) }
        island?.close()
        island = IslandController.pickScreen().map { IslandController(screen: $0) }
        applySettings()
    }

    @objc private func settingsChanged() {
        DispatchQueue.main.async { [weak self] in self?.applySettings() }
    }

    private func applySettings() {
        let config = SimConfig(intensity: settings.intensity, wind: settings.wind, drifts: settings.drifts,
                               tool: settings.tool, garland: settings.garland, depth: settings.depth,
                               lowPower: ProcessInfo.processInfo.isLowPowerModeEnabled, flakeSize: settings.flakeSize,
                               melt: settings.melt, tree: settings.tree, frost: settings.frost, sparkle: settings.sparkle)
        updateHiddenForFrontApp()
        for (i, o) in overlays.enumerated() {
            var c = config
            // The tree stands only on the main screen.
            if i != 0 { c.tree = false }
            if o.sim.config != c { o.sim.config = c }
            o.fullScreenMode = settings.fullScreenMode
            o.sim.needsWindowRefresh = true
            o.show(settings.enabled)
        }
        MusicService.shared.artworkFromInternet = settings.islandArtwork
        island?.show(settings.island)
        updateStatusIcon()
    }

    /// New track: the island breathes a puff of snow onto the screen below it.
    private func islandPuff() {
        guard settings.islandPuff, settings.island, settings.enabled, let island,
              let overlay = overlays.first(where: { $0.screen == island.screen }) else { return }
        let x = Float(island.centerX - island.screen.frame.minX)
        overlay.sim.puff(x: x, y: Float(island.bottomFromTop) + 6, count: 90)
    }

    private func updateStatusIcon() {
        statusItem?.button?.appearsDisabled = !settings.enabled || Overlay.hiddenForFrontApp
    }

    // MARK: - State

    private func setEnabled(_ on: Bool) {
        settings.pauseUntil = 0
        settings.enabled = on
    }

    private func toggleEnabled() { setEnabled(!settings.enabled) }

    /// Snow only on the desktop: hidden over every app except the ones the user allowed.
    private func toggleDesktopOnly() {
        if !settings.enabled { setEnabled(true) }
        settings.snowWhere = settings.snowWhere == 1 ? 0 : 1
    }

    /// Hide (or show again) the snow over the app in front.
    private func toggleFrontApp() {
        guard let app = frontApp, let id = app.bundleIdentifier, id != "com.apple.finder" else { return }
        toggleApp(id: id, name: app.localizedName ?? id)
    }

    /// Is this app in the current list (hidden apps, or allowed apps in desktop-only mode)?
    private func appListed(_ id: String) -> Bool {
        settings.snowWhere == 1 ? settings.shownApps[id] != nil : settings.hiddenApps[id] != nil
    }

    private func toggleApp(id: String, name: String) {
        if settings.snowWhere == 1 {
            var list = settings.shownApps
            list[id] = list[id] == nil ? name : nil
            settings.shownApps = list
        } else {
            var list = settings.hiddenApps
            list[id] = list[id] == nil ? name : nil
            settings.hiddenApps = list
        }
    }

    private func menuIcon(_ image: NSImage?) -> NSImage? {
        guard let image = image?.copy() as? NSImage else { return nil }
        image.size = NSSize(width: 16, height: 16)
        return image
    }

    /// Every open app (plus apps already in the list), each with a checkmark.
    private func appsSubmenu(desktopOnly: Bool) -> NSMenuItem {
        let list = desktopOnly ? settings.shownApps : settings.hiddenApps
        var title = desktopOnly ? L("Показывать снег над приложениями", "Show snow over apps")
                                : L("Прятать снег над приложениями", "Hide snow over apps")
        if !list.isEmpty { title += " (\(list.count))" }
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu()
        info(sub, desktopOnly ? L("Отметьте, где снег всё равно нужен:", "Tick the apps that still get snow:")
                              : L("Отметьте, где снег не нужен:", "Tick the apps that get no snow:"))

        var entries: [String: (name: String, icon: NSImage?)] = [:]
        let own = Bundle.main.bundleIdentifier
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            guard let id = app.bundleIdentifier, id != own, id != "com.apple.finder" else { continue }
            entries[id] = (app.localizedName ?? id, app.icon)
        }
        for (id, name) in list where entries[id] == nil {
            let icon = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id).map { NSWorkspace.shared.icon(forFile: $0.path) }
            entries[id] = (name, icon)
        }
        for (id, e) in entries.sorted(by: { $0.value.name.localizedCaseInsensitiveCompare($1.value.name) == .orderedAscending }) {
            let row = MenuAction(e.name, checked: list[id] != nil) { [weak self] in self?.toggleApp(id: id, name: e.name) }
            row.image = menuIcon(e.icon)
            sub.addItem(row)
        }
        if !list.isEmpty {
            sub.addItem(.separator())
            sub.addItem(MenuAction(L("Снять все отметки", "Clear all")) { [weak self] in
                guard let self else { return }
                if desktopOnly { self.settings.shownApps = [:] } else { self.settings.hiddenApps = [:] }
            })
        }
        item.submenu = sub
        return item
    }

    private func shovel() {
        overlays.forEach { $0.sim.clearSnow() }
    }

    private func fireworks() {
        if !settings.enabled { setEnabled(true) }
        overlays.forEach { $0.sim.festive.launchFireworks(seconds: 25) }
    }

    /// Menu item with its global shortcut shown on the right (⌃⌥ + key).
    private func shortcut(_ item: NSMenuItem, _ key: String) -> NSMenuItem {
        item.keyEquivalent = key
        item.keyEquivalentModifierMask = [.control, .option]
        return item
    }

    @objc private func appActivated(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
              app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        frontApp = app
        updateHiddenForFrontApp()
    }

    private func updateHiddenForFrontApp() {
        let id = frontApp?.bundleIdentifier ?? ""
        if settings.snowWhere == 1 {
            // Desktop only: the Finder (the desktop itself) and allowed apps keep the snow.
            Overlay.hiddenForFrontApp = !(id.isEmpty || id == "com.apple.finder" || settings.shownApps[id] != nil)
        } else {
            Overlay.hiddenForFrontApp = !id.isEmpty && settings.hiddenApps[id] != nil
        }
        updateStatusIcon()
    }

    private func tick() {
        // Timed pause.
        let until = settings.pauseUntil
        if until > 0 && Date().timeIntervalSince1970 >= until {
            setEnabled(true)
        }

        // Snowflake counter.
        for o in overlays { pendingFlakes += o.sim.takeLanded() }
        if pendingFlakes > 500 {
            settings.totalFlakes += pendingFlakes
            pendingFlakes = 0
        }

        // Fireworks from 23:59:50 on December 31 until 00:10 on January 1.
        guard settings.newYearFireworks else { return }
        let c = Calendar.current.dateComponents([.month, .day, .hour, .minute, .second], from: Date())
        let lastSeconds = c.month == 12 && c.day == 31 && c.hour == 23 && c.minute == 59 && (c.second ?? 0) >= 50
        let firstMinutes = c.month == 1 && c.day == 1 && c.hour == 0 && (c.minute ?? 99) < 10
        if (lastSeconds || firstMinutes) && settings.enabled {
            if !newYearShowRunning {
                newYearShowRunning = true
                let left = Float(600 - ((c.minute ?? 0) * 60 + (c.second ?? 0)) % 3600)
                overlays.forEach { $0.sim.festive.launchFireworks(seconds: max(30, min(610, left + 10))) }
            }
        } else {
            newYearShowRunning = false
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        for o in overlays { pendingFlakes += o.sim.takeLanded() }
        settings.totalFlakes += pendingFlakes
    }

    // MARK: - Menu

    private func info(_ menu: NSMenu, _ title: String) {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        menu.addItem(item)
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let s = settings

        info(menu, "Snowdrift")
        info(menu, newYearCountdown())
        var landed = s.totalFlakes + pendingFlakes
        for o in overlays { landed += o.sim.peekLanded }
        if landed > 0 {
            let fmt = NumberFormatter()
            fmt.numberStyle = .decimal
            let n = fmt.string(from: NSNumber(value: landed)) ?? "\(landed)"
            info(menu, L("❄︎ Улеглось снежинок: \(n)", "❄︎ Snowflakes settled: \(n)"))
        }
        menu.addItem(.separator())

        menu.addItem(shortcut(MenuAction(L("Снег включён", "Snow on"), checked: s.enabled) { [weak self] in
            self?.toggleEnabled()
        }, "s"))
        if s.enabled {
            menu.addItem(MenuAction(L("Пауза на 1 час", "Pause for 1 hour")) { [weak self] in
                self?.setEnabled(false)
                self?.settings.pauseUntil = Date().timeIntervalSince1970 + 3600
            })
        } else if s.pauseUntil > 0 {
            let f = DateFormatter()
            f.timeStyle = .short
            info(menu, L("Снег вернётся в ", "Snow returns at ") + f.string(from: Date(timeIntervalSince1970: s.pauseUntil)))
        }
        menu.addItem(.separator())

        // Where the snow shows.
        let desktopOnly = s.snowWhere == 1
        menu.addItem(shortcut(MenuAction(L("Прятать снег над всеми приложениями", "Hide snow over all apps"),
                                         checked: desktopOnly) { [weak self] in self?.toggleDesktopOnly() }, "a"))
        if desktopOnly {
            info(menu, L("   Снег идёт только на рабочем столе", "   Snow falls only on the desktop"))
        }
        // The app in front gets its own quick item (⌃⌥H does the same from anywhere).
        if let app = frontApp, let id = app.bundleIdentifier, id != Bundle.main.bundleIdentifier, id != "com.apple.finder" {
            let name = app.localizedName ?? id
            let title = desktopOnly ? L("Показывать снег над «\(name)»", "Show snow over “\(name)”")
                                    : L("Прятать снег над «\(name)»", "Hide snow over “\(name)”")
            let item = MenuAction(title, checked: appListed(id)) { [weak self] in self?.toggleApp(id: id, name: name) }
            item.image = menuIcon(app.icon)
            menu.addItem(shortcut(item, "h"))
        }
        menu.addItem(appsSubmenu(desktopOnly: desktopOnly))
        menu.addItem(choice(L("Полноэкранный режим", "Full screen"),
                            [L("Снег остаётся", "Snow stays"),
                             L("Снег прячется", "Snow hides"),
                             L("Прячется и над развёрнутыми окнами", "Hides over maximized windows too")],
                            s.fullScreenMode) { s.fullScreenMode = $0 })
        menu.addItem(.separator())

        menu.addItem(choice(L("Снегопад", "Snowfall"),
                            [L("Лёгкий снег", "Light snow"), L("Ровный снегопад", "Steady snowfall"), L("Метель", "Blizzard")],
                            s.intensity) { s.intensity = $0 })
        menu.addItem(choice(L("Снежинки", "Snowflakes"),
                            [L("Мелкие", "Small"), L("Обычные", "Normal"), L("Крупные", "Large")],
                            s.flakeSize) { s.flakeSize = $0 })
        menu.addItem(choice(L("Ветер", "Wind"),
                            [L("← Сильный", "← Strong"), L("← Слабый", "← Light"), L("Штиль", "Calm"),
                             L("Слабый →", "Light →"), L("Сильный →", "Strong →")],
                            s.wind + 2) { s.wind = $0 - 2 })
        menu.addItem(choice(L("Сугробы", "Drifts"),
                            [L("Без сугробов", "No drifts"), L("Низкие", "Low"), L("Средние", "Medium"), L("Высокие", "High")],
                            s.drifts) { s.drifts = $0 })
        menu.addItem(choice(L("Курсор", "Cursor"),
                            [L("Варежка — сметает снег", "Mitten — sweeps snow"),
                             L("Снежки — катает шары", "Snowballs — rolls balls"),
                             L("Не трогает снег", "Leaves snow alone")],
                            s.tool) { s.tool = $0 })
        menu.addItem(.separator())

        // Music island.
        let islandItem = NSMenuItem(title: L("Снежный остров 🎵", "Snow island 🎵"), action: nil, keyEquivalent: "")
        let im = NSMenu()
        info(im, L("Музыка у «чёлки»: наведите курсор", "Music at the notch: hover over it"))
        im.addItem(MenuAction(L("Показывать остров с музыкой", "Show the music island"), checked: s.island) { s.island.toggle() })
        im.addItem(MenuAction(L("Обложки Spotify (из интернета)", "Spotify covers (from the internet)"),
                              checked: s.islandArtwork) { s.islandArtwork.toggle() })
        im.addItem(MenuAction(L("Снежный выдох при смене трека", "Snow puff on a new track"),
                              checked: s.islandPuff) { s.islandPuff.toggle() })
        im.addItem(.separator())
        im.addItem(MenuAction(L("Разрешить управление музыкой…", "Allow music control…")) {
            MusicService.shared.openAutomationSettings()
        })
        islandItem.submenu = im
        menu.addItem(islandItem)

        // New Year.
        let festive = NSMenuItem(title: L("Новогоднее 🎄", "Holiday 🎄"), action: nil, keyEquivalent: "")
        let fm = NSMenu()
        fm.addItem(shortcut(MenuAction(L("Запустить салют 🎆", "Launch fireworks 🎆")) { [weak self] in
            self?.fireworks()
        }, "f"))
        fm.addItem(MenuAction(L("Салют в новогоднюю полночь", "Fireworks at New Year's midnight"),
                              checked: s.newYearFireworks) { s.newYearFireworks.toggle() })
        fm.addItem(.separator())
        fm.addItem(MenuAction(L("Ёлка в углу экрана", "Christmas tree in the corner"), checked: s.tree) { s.tree.toggle() })
        fm.addItem(choice(L("Гирлянда", "Garland"),
                          [L("Выключена", "Off"), L("Горит", "Steady"), L("Мерцает", "Twinkle"), L("Бегущие огни", "Chasing")],
                          s.garland) { s.garland = $0 })
        fm.addItem(MenuAction(L("Иней по углам экрана", "Frost in the screen corners"), checked: s.frost) { s.frost.toggle() })
        fm.addItem(MenuAction(L("Искрящийся снег", "Sparkling snow"), checked: s.sparkle) { s.sparkle.toggle() })
        festive.submenu = fm
        menu.addItem(festive)
        menu.addItem(MenuAction(L("Оттепель — снег понемногу тает", "Thaw — snow slowly melts"), checked: s.melt) { s.melt.toggle() })
        menu.addItem(MenuAction(L("Дальний снег за окнами", "Distant snow behind windows"), checked: s.depth) { s.depth.toggle() })
        menu.addItem(.separator())

        menu.addItem(shortcut(MenuAction(L("Убрать весь снег", "Shovel all snow")) { [weak self] in
            self?.shovel()
        }, "d"))
        if overlays.contains(where: { $0.sim.hasBalls }) {
            menu.addItem(MenuAction(L("Убрать снежки и снеговиков", "Remove snowballs and snowmen")) { [weak self] in
                self?.overlays.forEach { $0.sim.clearBalls() }
            })
        }
        menu.addItem(MenuAction(L("Как слепить снеговика…", "How to build a snowman…")) { [weak self] in self?.showHelp() })
        menu.addItem(.separator())

        let login = SMAppService.mainApp.status == .enabled
        menu.addItem(MenuAction(L("Запускать при входе в систему", "Open at login"), checked: login) { [weak self] in
            self?.toggleLogin()
        })
        menu.addItem(MenuAction(L("О Snowdrift", "About Snowdrift")) { [weak self] in self?.showAbout() })
        menu.addItem(MenuAction(L("Выйти", "Quit"), key: "q") { NSApp.terminate(nil) })
    }

    /// `--dump-menu`: prints the menu as text (for checking its layout without clicking).
    func debugDumpMenu() {
        frontApp = NSWorkspace.shared.frontmostApplication
        let menu = NSMenu()
        menuNeedsUpdate(menu)
        func dump(_ m: NSMenu, _ indent: String) {
            for item in m.items {
                if item.isSeparatorItem { print(indent + "———"); continue }
                var line = indent + (item.state == .on ? "✓ " : "  ") + item.title
                if !item.keyEquivalent.isEmpty { line += "   [⌃⌥" + item.keyEquivalent.uppercased() + "]" }
                if item.image != nil { line += "   (icon)" }
                if !item.isEnabled && item.submenu == nil && item.action == nil { line += "   (info)" }
                print(line)
                if let sub = item.submenu { dump(sub, indent + "      ") }
            }
        }
        dump(menu, "")
    }

    private func choice(_ title: String, _ options: [String], _ selected: Int, _ set: @escaping (Int) -> Void) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (i, name) in options.enumerated() {
            sub.addItem(MenuAction(name, checked: i == selected) { set(i) })
        }
        item.submenu = sub
        return item
    }

    private func newYearCountdown() -> String {
        let cal = Calendar.current
        let now = Date()
        let comps = cal.dateComponents([.year, .month, .day], from: now)
        if comps.month == 1 && (comps.day ?? 9) <= 7 { return L("🎄 С Новым годом!", "🎄 Happy New Year!") }
        let year = (comps.year ?? 2026) + 1
        guard let ny = cal.date(from: DateComponents(year: year, month: 1, day: 1)),
              let days = cal.dateComponents([.day], from: cal.startOfDay(for: now), to: ny).day else { return "🎄" }
        if !Lang.isRu { return "🎄 \(days) day\(days == 1 ? "" : "s") until New Year" }
        let d10 = days % 10, d100 = days % 100
        let word = (d10 == 1 && d100 != 11) ? "день" : ((2...4).contains(d10) && !(12...14).contains(d100) ? "дня" : "дней")
        return "🎄 До Нового года \(days) \(word)"
    }

    private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = L("Не получилось изменить автозапуск", "Couldn't change Open at login")
            alert.informativeText = L("Переместите Snowdrift в папку «Программы» и попробуйте снова.\n\n",
                                      "Move Snowdrift to the Applications folder and try again.\n\n") + error.localizedDescription
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
        }
    }

    private func showHelp() {
        let alert = NSAlert()
        alert.messageText = L("Пусть идёт снег ❄️", "Let it snow ❄️")
        alert.informativeText = L("""
        Снег ложится на окна, Dock и низ экрана. Сдвиньте окно — сугроб поедет вместе с ним, резко дёрните — снег слетит.

        Варежка: проведите курсором по сугробу, и снег разлетится.

        Снеговик: в меню выберите «Курсор → Снежки» и поводите курсором по сугробу, пока не скатается снежок. Толкайте его курсором — он катится и растёт. Возьмите снежок мышкой и положите на другой, побольше. Два шара — уже снеговик, три — снеговик с ведром или цилиндром.

        Быстрые клавиши работают в любом приложении:
        ⌃⌥S — включить или выключить снег
        ⌃⌥A — прятать снег над всеми приложениями (снег только на рабочем столе)
        ⌃⌥H — спрятать или вернуть снег над текущим приложением
        ⌃⌥D — убрать весь снег
        ⌃⌥F — салют

        Над полноэкранными приложениями снег прячется сам (меню → «Полноэкранный режим»).

        В меню «Новогоднее 🎄» — салют, ёлка, гирлянда и иней. В новогоднюю полночь салют запустится сам.

        Снежный остров 🎵: когда играет Spotify или «Музыка», наведите курсор на «чёлку» — появятся обложка, прогресс и кнопки.

        Клики проходят сквозь снег. Snowdrift не видит содержимое окон — только их границы.
        """, """
        Snow settles on windows, the Dock and the bottom of the screen. Move a window and its drift rides along; yank it and the snow flies off.

        Mitten: sweep the cursor through a drift and the snow scatters.

        Snowman: choose Cursor → Snowballs in the menu and sweep the cursor through a drift until a snowball forms. Push it with the cursor to roll it bigger. Pick a snowball up with the mouse and drop it on a bigger one. Two balls make a snowman; three get a bucket or a top hat.

        Shortcuts work in any app:
        ⌃⌥S — snow on or off
        ⌃⌥A — hide snow over all apps (snow only on the desktop)
        ⌃⌥H — hide or bring back snow over the current app
        ⌃⌥D — shovel all snow
        ⌃⌥F — fireworks

        Snow hides over full-screen apps on its own (menu → Full screen).

        The Holiday 🎄 menu has fireworks, a tree, a garland and frost. At New Year's midnight the fireworks start on their own.

        Snow island 🎵: while Spotify or Music plays, hover over the notch for the cover, progress and buttons.

        Clicks go through the snow. Snowdrift never sees what is inside your windows, only where they are.
        """)
        alert.addButton(withTitle: L("Понятно", "Got it"))
        if settings.tool != 1 { alert.addButton(withTitle: L("Включить снежки", "Turn on snowballs")) }
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertSecondButtonReturn { settings.tool = 1 }
    }

    private func showAbout() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        let alert = NSAlert()
        alert.messageText = "Snowdrift \(version)"
        alert.informativeText = L("""
        Снег на рабочем столе Mac. Бесплатно и с открытым кодом (MIT).

        Без аккаунтов, ключей, рекламы и аналитики. В интернет — только за обложками Spotify (можно выключить). Настройки хранятся только на этом Mac.
        """, """
        Snow on your Mac desktop. Free and open source (MIT).

        No accounts, keys, ads or analytics. The only download is Spotify cover art (can be turned off). Settings stay on this Mac.
        """)
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
