import AppKit
import ServiceManagement

let args = CommandLine.arguments

if let i = args.firstIndex(of: "--snapshot"), i + 1 < args.count {
    exit(Snapshot.run(path: args[i + 1]) ? 0 : 1)
}
if args.contains("--dump-menu") {
    _ = NSApplication.shared
    AppDelegate().debugDumpMenu()
    exit(0)
}
if let i = args.firstIndex(of: "--island-preview"), i + 1 < args.count {
    let ok = MainActor.assumeIsolated { IslandPreview.run(path: args[i + 1]) }
    exit(ok ? 0 : 1)
}
if let i = args.firstIndex(of: "--update-test"), i + 1 < args.count {
    _ = NSApplication.shared
    exit(Updater.runTest(target: URL(fileURLWithPath: args[i + 1])) ? 0 : 1)
}
if args.contains("--selftest") {
    exit(SelfTest.run() ? 0 : 1)
}
if let i = args.firstIndex(of: "--icon"), i + 1 < args.count {
    exit(IconMaker.write(path: args[i + 1]) ? 0 : 1)
}

if args.contains("--enable-login-item") || args.contains("--disable-login-item") {
    do {
        if args.contains("--enable-login-item") {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
        print("login item:", SMAppService.mainApp.status == .enabled ? "enabled" : "disabled")
        exit(0)
    } catch {
        print("login item error:", error.localizedDescription)
        exit(1)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
