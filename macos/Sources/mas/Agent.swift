import AppKit
import Foundation

/// Glue between the USB watcher and the switcher: debounce + switching rules + pause. Main-thread only.
final class Agent {
    private(set) var config: Config
    let switcher: Switcher
    private var watcher: USBWatcher?
    private var debounce: DispatchWorkItem?
    private var settledPresent: Bool?
    private(set) var lastResult = "none yet"
    var onStateChange: (() -> Void)?

    var paused = false {
        didSet { Log.info(paused ? "Automatic switching PAUSED" : "Automatic switching RESUMED"); onStateChange?() }
    }
    var sentinelPresent: Bool { watcher?.isPresent ?? false }

    init(config: Config) {
        self.config = config
        switcher = Switcher(config: config)
    }

    func start() {
        if config.sentinels.isEmpty {
            Log.warn("No sentinels configured — automatic switching is inactive. Run `mas list-usb` and set `sentinels` in \(Config.fileURL.path)")
        }
        Log.info("Config: \(config.monitors.count) monitor(s), debounce \(config.debounceMs) ms, retries \(config.retries), on_connect=\(config.onConnect), on_disconnect=\(config.onDisconnect), backend=\(config.backend.rawValue)")
        let cfg = config
        watcher = USBWatcher(isSentinel: { cfg.isSentinel(vid: $0, pid: $1) }) { [weak self] present, event in
            self?.usbChanged(present: present, event: event)
        }
        watcher?.start()
        settledPresent = watcher?.isPresent   // no switch at startup; only on transitions
        onStateChange?()

        let ws = NSWorkspace.shared.notificationCenter
        ws.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { _ in Log.info("System going to sleep") }
        ws.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Log.info("System woke; sentinel " + ((self?.sentinelPresent ?? false) ? "present" : "absent"))
            self?.onStateChange?()
        }
    }

    func reloadConfig() {
        do {
            let c = try Config.load()
            debounce?.cancel()
            watcher?.stop()
            config = c
            switcher.config = c
            Log.info("Config reloaded")
            start()
        } catch {
            Log.error("Config reload failed, keeping previous config: \(error)")
        }
    }

    func manualSwitch(_ target: Target) {
        Log.info("Manual switch requested: to \(target.label)")
        switcher.switchAll(to: target, reason: "manual") { [weak self] s in self?.finished(s) }
    }

    private func usbChanged(present: Bool, event: String) {
        Log.debug("USB event (\(event)); debouncing \(config.debounceMs) ms")
        debounce?.cancel()
        let item = DispatchWorkItem { [weak self] in self?.settle() }
        debounce = item
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(config.debounceMs), execute: item)
        onStateChange?()
    }

    private func settle() {
        let present = sentinelPresent
        defer { onStateChange?() }
        guard present != settledPresent else {
            Log.info("Settled sentinel state unchanged (\(present ? "present" : "absent")); no action")
            return
        }
        settledPresent = present
        if paused { Log.info("Sentinel now \(present ? "present" : "absent") but switching is paused; no action"); return }
        if present {
            guard config.onConnect else { Log.info("Sentinel connected; on_connect rule disabled, no action"); return }
            switcher.switchAll(to: .mac, reason: "sentinel connected to this Mac") { [weak self] s in self?.finished(s) }
        } else {
            guard config.onDisconnect else { Log.info("Sentinel disconnected; on_disconnect rule disabled, no action"); return }
            switcher.switchAll(to: .pc, reason: "sentinel disconnected from this Mac") { [weak self] s in self?.finished(s) }
        }
    }

    private func finished(_ summary: String) {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
        lastResult = "\(summary) at \(f.string(from: Date()))"
        onStateChange?()
    }
}

// MARK: - Menu bar UI

final class MenuBarController: NSObject, NSMenuDelegate {
    private let agent: Agent
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()

    init(agent: Agent) {
        self.agent = agent
        super.init()
        menu.delegate = self
        item.menu = menu
        agent.onStateChange = { [weak self] in self?.refreshIcon() }
        refreshIcon()
    }

    private func refreshIcon() {
        guard let button = item.button else { return }
        let symbol = agent.paused ? "pause.circle" : "display.2"
        if let img = NSImage(systemSymbolName: symbol, accessibilityDescription: "Monitor Auto-Switch") {
            img.isTemplate = true
            button.image = img
            button.title = ""
        } else {
            button.title = agent.paused ? "MAS ⏸" : "MAS"
        }
        button.toolTip = "Monitor Auto-Switch" + (agent.paused ? " (paused)" : "")
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let sentinel = agent.config.sentinels.isEmpty ? "Sentinel: not configured"
            : "Sentinel: " + (agent.sentinelPresent ? "connected to this Mac" : "not connected")
        menu.addItem(disabled(sentinel))
        menu.addItem(disabled("Last switch: \(agent.lastResult)"))
        if agent.paused { menu.addItem(disabled("Automatic switching is paused")) }
        menu.addItem(.separator())
        menu.addItem(action("Switch monitors to Mac now", #selector(toMac)))
        menu.addItem(action("Switch monitors to PC now", #selector(toPC)))
        let pause = action("Pause automatic switching", #selector(togglePause))
        pause.state = agent.paused ? .on : .off
        menu.addItem(pause)
        menu.addItem(.separator())
        menu.addItem(action("Open config", #selector(openConfig)))
        menu.addItem(action("Reload config", #selector(reloadConfig)))
        menu.addItem(action("Open log", #selector(openLog)))
        let login = action("Start at login", #selector(toggleLogin))
        login.state = LaunchAgent.isInstalled ? .on : .off
        menu.addItem(login)
        menu.addItem(.separator())
        menu.addItem(action("Quit", #selector(quit), key: "q"))
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.isEnabled = false
        return i
    }

    private func action(_ title: String, _ sel: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: title, action: sel, keyEquivalent: key)
        i.target = self
        return i
    }

    @objc private func toMac() { agent.manualSwitch(.mac) }
    @objc private func toPC() { agent.manualSwitch(.pc) }
    @objc private func togglePause() { agent.paused.toggle() }
    @objc private func openConfig() { NSWorkspace.shared.open(Config.fileURL) }
    @objc private func reloadConfig() { agent.reloadConfig() }
    @objc private func openLog() { Log.flush(); NSWorkspace.shared.open(Log.fileURL) }
    @objc private func toggleLogin() {
        do {
            if LaunchAgent.isInstalled { try LaunchAgent.uninstall(unload: false) } else { try LaunchAgent.install(load: false) }
        } catch { Log.error("Start-at-login toggle failed: \(error)") }
    }
    @objc private func quit() { Log.info("Quit from menu"); Log.flush(); exit(0) }
}

// MARK: - LaunchAgent (start at login, restart on crash)

enum LaunchAgent {
    static let label = "com.monitorautoswitch.agent"
    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var isInstalled: Bool { FileManager.default.fileExists(atPath: plistURL.path) }

    static var executablePath: String {
        URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().standardizedFileURL.path
    }

    static func install(load: Bool) throws {
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [executablePath, "agent"],
            "RunAtLoad": true,
            // Restart after crashes, but let "Quit" from the menu (exit 0) stay quit.
            "KeepAlive": ["SuccessfulExit": false],
            "ProcessType": "Interactive",
            "LimitLoadToSessionType": "Aqua",
            "StandardErrorPath": Log.directory.appendingPathComponent("stderr.log").path,
        ]
        try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: plistURL)
        Log.info("Installed LaunchAgent \(plistURL.path) -> \(executablePath)")
        if load {
            _ = ExternalDDC.shell("launchctl bootout gui/\(getuid())/\(label) 2>/dev/null")
            let (s, out) = ExternalDDC.shell("launchctl bootstrap gui/\(getuid()) '\(plistURL.path)'")
            if s != 0 { throw DDCError("launchctl bootstrap failed: \(out)") }
        }
    }

    static func uninstall(unload: Bool) throws {
        if unload { _ = ExternalDDC.shell("launchctl bootout gui/\(getuid())/\(label)") }
        if isInstalled { try FileManager.default.removeItem(at: plistURL) }
        Log.info("Removed LaunchAgent")
    }
}
