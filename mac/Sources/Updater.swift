import AppKit

/// Checks GitHub for a newer release and, when you agree, swaps the app for the new one:
/// download the -mac.zip, unpack it next to the running app, then a tiny shell script waits
/// for this process to quit, moves the new bundle into place and starts it.
final class Updater {
    private let api = URL(string: "https://api.github.com/repos/gsleraar-hue/bubbels/releases/latest")!
    private var timer: Timer?
    private var busy = false

    /// Called on the main thread when a newer version exists (tag, zip url).
    var onNewer: ((String, URL) -> Void)?

    static var current: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    func start() {
        // First look shortly after launch, then every six hours.
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) { [weak self] in self?.check(manual: false) }
        let t = Timer(timeInterval: 6 * 60 * 60, repeats: true) { [weak self] _ in self?.check(manual: false) }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func check(manual: Bool, upToDate: (() -> Void)? = nil, failed: ((String) -> Void)? = nil) {
        guard !busy else { return }
        busy = true
        var request = URLRequest(url: api)
        request.setValue("Bubbels/\(Updater.current)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: request) { [weak self] data, _, error in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.busy = false
                guard let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let tag = json["tag_name"] as? String else {
                    if manual { failed?(error?.localizedDescription ?? "no answer from GitHub") }
                    return
                }
                let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
                let assets = json["assets"] as? [[String: Any]] ?? []
                let zip = assets.compactMap { $0["browser_download_url"] as? String }
                    .first { $0.hasSuffix("-mac.zip") && $0.hasPrefix("https://github.com/") }
                if Updater.isNewer(version, than: Updater.current), let zip = zip, let url = URL(string: zip) {
                    self.onNewer?(version, url)
                } else if manual {
                    upToDate?()
                }
            }
        }.resume()
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    /// Download, unpack and hand over to the swap script. Calls `failed` on the main thread.
    func install(from url: URL, failed: @escaping (String) -> Void) {
        let appPath = Bundle.main.bundlePath
        let parent = (appPath as NSString).deletingLastPathComponent
        guard FileManager.default.isWritableFile(atPath: parent) else {
            failed(T("Bubbels cannot write to ", "Bubbels kan niet schrijven in ") + parent)
            return
        }
        URLSession.shared.downloadTask(with: url) { file, _, error in
            guard let file = file else {
                DispatchQueue.main.async { failed(error?.localizedDescription ?? "download failed") }
                return
            }
            let work = FileManager.default.temporaryDirectory.appendingPathComponent("bubbels-update-\(UUID().uuidString)")
            do {
                try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
                let zip = work.appendingPathComponent("Bubbels.zip")
                try FileManager.default.moveItem(at: file, to: zip)

                let unzip = Process()
                unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
                unzip.arguments = ["-x", "-k", zip.path, work.path]
                try unzip.run()
                unzip.waitUntilExit()
                let newApp = work.appendingPathComponent("Bubbels.app")
                guard unzip.terminationStatus == 0, FileManager.default.fileExists(atPath: newApp.path) else {
                    throw NSError(domain: "Bubbels", code: 1, userInfo: [NSLocalizedDescriptionKey: "could not unpack the update"])
                }

                // Wait for us to quit, swap the bundles, clear the download flag, start again.
                let pid = ProcessInfo.processInfo.processIdentifier
                let script = """
                while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done
                rm -rf "\(appPath)"
                mv "\(newApp.path)" "\(appPath)"
                xattr -dr com.apple.quarantine "\(appPath)" 2>/dev/null
                open "\(appPath)"
                rm -rf "\(work.path)"
                """
                let swap = Process()
                swap.executableURL = URL(fileURLWithPath: "/bin/sh")
                swap.arguments = ["-c", script]
                try swap.run()
                DispatchQueue.main.async { NSApp.terminate(nil) }
            } catch {
                DispatchQueue.main.async { failed(error.localizedDescription) }
            }
        }.resume()
    }
}
