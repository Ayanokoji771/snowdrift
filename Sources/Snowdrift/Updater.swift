import AppKit
import CryptoKit
import Foundation

/// Updates from the project's GitHub releases. The only thing sent is an ordinary request for the latest
/// release; the download is checked against the release's SHA-256 checksums and its code signature before
/// it replaces Snowdrift.app. Settings stay where they are.
final class Updater {
    static let shared = Updater()
    static let repo = "Ayanokoji771/snowdrift"
    static let assetName = "Snowdrift-macOS.zip"

    private(set) var latest: String?
    private(set) var pageURL = URL(string: "https://github.com/\(Updater.repo)/releases/latest")!
    private(set) var busy = false
    private var zipURL: URL?
    private var sumsURL: URL?

    static var current: String {
        if let p = ProcessInfo.processInfo.environment["SNOWDRIFT_PRETEND_VERSION"] { return p }
        return Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    var available: Bool {
        guard let latest, zipURL != nil, sumsURL != nil else { return false }
        return Updater.isNewer(latest, than: Updater.current)
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] { s.split(separator: ".").map { Int($0) ?? 0 } + [0, 0, 0] }
        let x = parts(a), y = parts(b)
        for i in 0..<3 where x[i] != y[i] { return x[i] > y[i] }
        return false
    }

    private let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.httpAdditionalHeaders = ["User-Agent": "Snowdrift-Updater"]
        c.timeoutIntervalForRequest = 60
        return URLSession(configuration: c)
    }()

    private struct Release {
        let version: String
        let zip: URL?
        let sums: URL?
        let page: URL?
    }

    /// Asks GitHub for the latest release; calls back on the main thread with whether the answer was read.
    func check(_ done: @escaping (Bool) -> Void) {
        let session = self.session
        Task.detached {
            let release = await Updater.fetch(session)
            await MainActor.run {
                if let r = release {
                    self.latest = r.version
                    self.zipURL = r.zip
                    self.sumsURL = r.sums
                    if let p = r.page { self.pageURL = p }
                }
                done(release != nil)
            }
        }
    }

    /// Follows the release page's redirect rather than asking the GitHub API: the API allows only 60 requests
    /// an hour per address, and many people share one (offices, mobile networks).
    private static func fetch(_ session: URLSession) async -> Release? {
        guard let latest = URL(string: "https://github.com/\(repo)/releases/latest") else { return nil }
        do {
            var page = URLRequest(url: latest)
            page.httpMethod = "HEAD"
            let (_, response) = try await session.data(for: page)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200, let final = http.url,
                  let range = final.path.range(of: "/releases/tag/") else { return nil }
            let tag = final.path[range.upperBound...].trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            guard !tag.isEmpty, let files = URL(string: "https://github.com/\(repo)/releases/download/\(tag)/") else { return nil }
            // Releases without checksums (1.0.0) can't be installed automatically.
            var sums = URLRequest(url: files.appendingPathComponent("SHA256SUMS.txt"))
            sums.httpMethod = "HEAD"
            let (_, sumsResponse) = try await session.data(for: sums)
            let hasSums = (sumsResponse as? HTTPURLResponse)?.statusCode == 200
            let version = tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag
            return Release(version: version, zip: files.appendingPathComponent(assetName),
                           sums: hasSums ? files.appendingPathComponent("SHA256SUMS.txt") : nil, page: final)
        } catch {
            NSLog("Snowdrift update check failed: \(error.localizedDescription)")
            return nil
        }
    }

    /// Downloads, checks and swaps in the new app at `target`. Calls back on the main thread with nil on
    /// success or what went wrong, in words for the person.
    func install(force: Bool = false, target: URL = Bundle.main.bundleURL, _ done: @escaping (String?) -> Void) {
        guard !busy else { return }
        guard force || available, let zipURL, let sumsURL else {
            done(L("В выпуске на GitHub нет нужного файла.", "The GitHub release is missing a file."))
            return
        }
        busy = true
        let session = self.session
        Task.detached {
            let error = await Updater.perform(zip: zipURL, sums: sumsURL, target: target, session: session)
            await MainActor.run {
                self.busy = false
                done(error)
            }
        }
    }

    private static func perform(zip: URL, sums: URL, target: URL, session: URLSession) async -> String? {
        let fm = FileManager.default
        guard fm.isWritableFile(atPath: target.deletingLastPathComponent().path), fm.isWritableFile(atPath: target.path) else {
            return L("Нет прав на запись в папку с Snowdrift. Скачайте новую версию со страницы выпуска.",
                     "Snowdrift's folder is not writable. Download the new version from the release page.")
        }
        do {
            // A scratch folder on the same disk, so the final swap is a single rename.
            let work = try fm.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: target, create: true)
            defer { try? fm.removeItem(at: work) }
            let (downloaded, response) = try await session.download(from: zip)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                return L("GitHub не отдал файл обновления.", "GitHub did not return the update file.")
            }
            let archive = work.appendingPathComponent(assetName)
            try fm.moveItem(at: downloaded, to: archive)
            let (sumsData, _) = try await session.data(from: sums)
            let expected = expectedHash(String(decoding: sumsData, as: UTF8.self), name: assetName)
            let actual = SHA256.hash(data: try Data(contentsOf: archive)).map { String(format: "%02x", $0) }.joined()
            guard let expected, expected.lowercased() == actual else {
                NSLog("Snowdrift update: checksum mismatch, expected \(expected ?? "-"), got \(actual)")
                return L("Скачанный файл не прошёл проверку контрольной суммы. Ничего не изменено.",
                         "The download failed its checksum test. Nothing was changed.")
            }
            let unpacked = work.appendingPathComponent("new")
            guard run("/usr/bin/ditto", ["-x", "-k", archive.path, unpacked.path]) == 0 else {
                return L("Не удалось распаковать обновление.", "Couldn't unpack the update.")
            }
            let fresh = unpacked.appendingPathComponent("Snowdrift.app")
            guard Bundle(url: fresh)?.bundleIdentifier == "io.github.snowdrift.app",
                  run("/usr/bin/codesign", ["--verify", "--deep", "--strict", fresh.path]) == 0 else {
                return L("Обновление не прошло проверку подписи. Ничего не изменено.",
                         "The update failed its signature check. Nothing was changed.")
            }
            _ = run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", fresh.path])
            _ = try fm.replaceItemAt(target, withItemAt: fresh)
            return nil
        } catch {
            return L("Не удалось обновиться: ", "Update failed: ") + error.localizedDescription
        }
    }

    /// Opens the new copy once this one has quit.
    static func relaunch(_ app: URL = Bundle.main.bundleURL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", app.path]
        try? p.run()
        NSApp.terminate(nil)
    }

    private static func expectedHash(_ sums: String, name: String) -> String? {
        for raw in sums.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let space = line.firstIndex(of: " ") else { continue }
            var file = line[space...].trimmingCharacters(in: .whitespaces)
            if file.hasPrefix("*") { file.removeFirst() }
            if file == name { return String(line[..<space]) }
        }
        return nil
    }

    @discardableResult
    private static func run(_ tool: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }

    /// `--update-test <copy of Snowdrift.app>`: the whole update on a copy, without restarting anything.
    static func runTest(target: URL) -> Bool {
        let u = Updater.shared
        var ok = false
        let done = DispatchSemaphore(value: 0)
        print("this build: \(current)")
        u.check { read in
            print("latest on GitHub: \(read ? (u.latest ?? "?") : "unreadable"), newer: \(u.available), page \(u.pageURL)")
            guard read else { done.signal(); return }
            u.install(force: true, target: target) { error in
                let installed = Bundle(url: target)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
                print("install: \(error ?? "ok"); the copy is now version \(installed ?? "?")")
                ok = error == nil && installed == u.latest
                done.signal()
            }
        }
        while done.wait(timeout: .now() + 0.05) == .timedOut {
            RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        }
        print(ok ? "PASS update" : "FAIL update")
        return ok
    }
}
