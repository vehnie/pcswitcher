import AppKit
import Foundation

let usage = """
mas — Monitor Auto-Switch (macOS agent)

Usage:
  mas agent                 run the menu bar agent (what the LaunchAgent starts)
  mas watch                 run in the foreground with verbose logging (no menu bar)
  mas list-usb              list connected USB devices with VID:PID
  mas list-monitors         list external monitors, EDID info, DDC reachability, current input
  mas get-input <mon>       read the current input (VCP 0x60 or the monitor's vcp_code)
  mas set-input <mon> <v>   write an input value, e.g. 0x11
  mas get-vcp <mon> <code>  read any VCP code, e.g. 0x10 (brightness) — handy for testing DDC
  mas to-mac | to-pc        switch all configured monitors now
  mas init-config [--force] write a starter config with the connected monitors filled in
  mas install-agent         start at login (LaunchAgent) and start it now
  mas uninstall-agent       remove the LaunchAgent and stop it

<mon> is a config name, a number from `list-monitors`, or the product name.
Config: \(Config.fileURL.path)
Log:    \(Log.fileURL.path)
"""

func fail(_ msg: String) -> Never {
    FileHandle.standardError.write("error: \(msg)\n".data(using: .utf8)!)
    Log.flush()
    exit(1)
}

func loadConfigOrDefault(createIfMissing: Bool) -> Config {
    let fm = FileManager.default
    if !fm.fileExists(atPath: Config.fileURL.path) {
        if createIfMissing {
            try? fm.createDirectory(at: Config.directory, withIntermediateDirectories: true)
            try? Config.template(for: DDCDisplay.discover()).write(to: Config.fileURL, atomically: true, encoding: .utf8)
            Log.warn("No config found; wrote a starter config to \(Config.fileURL.path)")
        } else {
            return Config()
        }
    }
    do { return try Config.load() } catch { fail("config: \(error)") }
}

/// Resolve <mon> to a display plus the matching monitor config (if any).
func resolveMonitor(_ arg: String, _ config: Config) -> (DDCDisplay, MonitorConfig?) {
    let displays = DDCDisplay.discover()
    if let mc = config.monitors.first(where: { $0.name.caseInsensitiveCompare(arg) == .orderedSame }) {
        guard let d = displays.first(where: mc.matches) else { fail("monitor '\(mc.name)' is configured but not connected") }
        return (d, mc)
    }
    let d: DDCDisplay?
    if let n = Int(arg), n >= 1, n <= displays.count { d = displays[n - 1] }
    else { d = displays.first { $0.productName.caseInsensitiveCompare(arg) == .orderedSame } }
    guard let d else { fail("no monitor '\(arg)'. Try `mas list-monitors`.") }
    return (d, config.monitors.first { $0.matches(d) })
}

func inputName(_ v: UInt16) -> String {
    switch v & 0xFF {
    case 0x0F: return "DisplayPort 1"
    case 0x10: return "DisplayPort 2"
    case 0x11: return "HDMI 1"
    case 0x12: return "HDMI 2"
    case 0x1B: return "USB-C"
    default: return "?"
    }
}

let args = Array(CommandLine.arguments.dropFirst())
let command = args.first ?? "help"

switch command {
case "agent":
    let config = loadConfigOrDefault(createIfMissing: true)
    Log.info("=== mas agent starting (pid \(getpid())) ===")
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let agent = Agent(config: config)
    let ui = MenuBarController(agent: agent)
    agent.start()
    withExtendedLifetime(ui) { app.run() }

case "watch":
    Log.verbose = true
    let config = loadConfigOrDefault(createIfMissing: true)
    Log.info("=== mas watch (Ctrl-C to stop) ===")
    let agent = Agent(config: config)
    agent.start()
    signal(SIGINT) { _ in Log.flush(); exit(0) }
    RunLoop.main.run()

case "list-usb":
    let config = loadConfigOrDefault(createIfMissing: false)
    let devices = USBDevice.all().sorted { $0.vidpid < $1.vidpid }
    print("VID:PID    Name / Vendor")
    for d in devices {
        let mark = config.isSentinel(vid: d.vid, pid: d.pid) ? "  <- sentinel" : ""
        print("\(d.vidpid)  \(d.name)\(d.vendor.isEmpty ? "" : " / \(d.vendor)")\(mark)")
    }

case "list-monitors":
    let config = loadConfigOrDefault(createIfMissing: false)
    let displays = DDCDisplay.discover()
    if displays.isEmpty { print("No external displays found.") }
    for (i, d) in displays.enumerated() {
        let mc = config.monitors.first { $0.matches(d) }
        print("[\(i + 1)] \(d.manufacturer) \(d.productName)  productID=\(d.productID)  serial=\(d.serial)\(d.alphanumericSerial.isEmpty ? "" : " / \(d.alphanumericSerial)")")
        print("    EDID UUID: \(d.edidUUID)")
        print("    config:    \(mc.map { "\"\($0.name)\" (mac \(hex16($0.macInput)), pc \(hex16($0.pcInput)), vcp \(hex($0.vcpCode)))" } ?? "no matching [[monitors]] entry")")
        let vcp = mc?.vcpCode ?? 0x60
        do {
            let v = try d.read(vcp: vcp, address: mc?.effectiveI2CAddress ?? 0x51)
            print("    DDC:       reachable, VCP \(hex(vcp)) = \(hex16(v.current)) (\(inputName(v.current)))")
        } catch {
            print("    DDC:       NOT reachable — \(error)")
        }
    }

case "get-input", "get-vcp":
    let need = command == "get-vcp" ? 3 : 2
    guard args.count >= need else { fail("usage: mas \(command) <mon>\(command == "get-vcp" ? " <code>" : "")") }
    let config = loadConfigOrDefault(createIfMissing: false)
    let (d, mc) = resolveMonitor(args[1], config)
    let vcp: UInt8
    if command == "get-vcp" {
        guard let c = parseInt(args[2]), c >= 0, c <= 0xFF else { fail("bad VCP code '\(args[2])'") }
        vcp = UInt8(c)
    } else {
        vcp = mc?.vcpCode ?? 0x60
    }
    do {
        let v = try d.read(vcp: vcp, address: mc?.effectiveI2CAddress ?? 0x51)
        print("\(d.displayName): VCP \(hex(vcp)) = \(hex16(v.current)) (\(v.current))  max=\(v.max)" + (command == "get-input" ? "  \(inputName(v.current))" : ""))
    } catch { fail("\(error)") }

case "set-input":
    guard args.count >= 3, let v = parseInt(args[2]), v >= 0, v <= 0xFFFF else { fail("usage: mas set-input <mon> <value>  (e.g. 0x11)") }
    let config = loadConfigOrDefault(createIfMissing: false)
    let (d, mc) = resolveMonitor(args[1], config)
    let vcp = mc?.vcpCode ?? 0x60
    do {
        if config.backend == .external {
            try ExternalDDC.run(config.externalCommand, monitor: mc ?? MonitorConfig(name: d.productName), display: d, value: UInt16(v))
        } else {
            try d.write(vcp: vcp, value: UInt16(v), address: mc?.effectiveI2CAddress ?? 0x51)
        }
        print("\(d.displayName): wrote VCP \(hex(vcp)) = \(hex16(UInt16(v)))")
    } catch { fail("\(error)") }

case "to-mac", "to-pc":
    Log.verbose = true
    let config = loadConfigOrDefault(createIfMissing: false)
    let summary = Switcher(config: config).switchAllSync(to: command == "to-mac" ? .mac : .pc, reason: "CLI")
    print(summary)

case "init-config":
    if FileManager.default.fileExists(atPath: Config.fileURL.path) && !args.contains("--force") {
        fail("\(Config.fileURL.path) exists (use --force to overwrite)")
    }
    try? FileManager.default.createDirectory(at: Config.directory, withIntermediateDirectories: true)
    do {
        try Config.template(for: DDCDisplay.discover()).write(to: Config.fileURL, atomically: true, encoding: .utf8)
        print("Wrote \(Config.fileURL.path)")
    } catch { fail("\(error)") }

case "install-agent":
    _ = loadConfigOrDefault(createIfMissing: true)
    do { try LaunchAgent.install(load: true); print("LaunchAgent installed and started: \(LaunchAgent.plistURL.path)") }
    catch { fail("\(error)") }

case "uninstall-agent":
    do { try LaunchAgent.uninstall(unload: true); print("LaunchAgent removed") } catch { fail("\(error)") }

case "help", "-h", "--help":
    print(usage)

default:
    print(usage)
    exit(2)
}
Log.flush()
