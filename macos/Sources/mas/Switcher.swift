import Foundation

enum Target: String {
    case mac, pc
    var label: String { self == .mac ? "Mac" : "PC" }
}

enum Outcome {
    case alreadyThere, switched, sentUnverified, notFound, cancelled, failed(String)

    var ok: Bool {
        switch self { case .alreadyThere, .switched, .sentUnverified: return true; default: return false }
    }
    var text: String {
        switch self {
        case .alreadyThere: return "already on target"
        case .switched: return "switched (verified)"
        case .sentUnverified: return "command sent (not verified)"
        case .notFound: return "monitor not found / not reachable from this Mac"
        case .cancelled: return "cancelled by newer request"
        case .failed(let e): return "FAILED: \(e)"
        }
    }
}

/// Performs DDC input switches for all configured monitors. Serialises requests; a newer request cancels an older one.
final class Switcher {
    var config: Config
    private let queue = DispatchQueue(label: "mas.switcher")
    private let lock = NSLock()
    private var generation = 0

    init(config: Config) { self.config = config }

    private var currentGeneration: Int { lock.lock(); defer { lock.unlock() }; return generation }

    func switchAll(to target: Target, reason: String, completion: ((String) -> Void)? = nil) {
        lock.lock(); generation += 1; let gen = generation; lock.unlock()
        let config = self.config
        queue.async {
            let summary = self.run(target: target, reason: reason, config: config, gen: gen)
            if let completion { DispatchQueue.main.async { completion(summary) } }
        }
    }

    /// Synchronous variant for the CLI.
    @discardableResult
    func switchAllSync(to target: Target, reason: String) -> String {
        lock.lock(); generation += 1; let gen = generation; lock.unlock()
        return queue.sync { run(target: target, reason: reason, config: config, gen: gen) }
    }

    private func run(target: Target, reason: String, config: Config, gen: Int) -> String {
        let t0 = Date()
        Log.info("Switching monitors to \(target.label) (\(reason))")
        guard !config.monitors.isEmpty else {
            Log.error("No [[monitors]] configured; nothing to switch")
            return "no monitors configured"
        }
        var outcomes = [Outcome](repeating: .cancelled, count: config.monitors.count)
        let outLock = NSLock()
        DispatchQueue.concurrentPerform(iterations: config.monitors.count) { i in
            let m = config.monitors[i]
            let o = apply(m, target: target, config: config, gen: gen)
            outLock.lock(); outcomes[i] = o; outLock.unlock()
            let level = o.ok ? Log.info : Log.warn
            level("[\(m.name)] -> \(target.label) \(String(format: "0x%02X", m.input(for: target))): \(o.text)")
        }
        let okCount = outcomes.filter(\.ok).count
        let ms = Int(Date().timeIntervalSince(t0) * 1000)
        let summary = "\(target.label): \(okCount)/\(outcomes.count) ok in \(ms) ms"
        Log.info("Switch done — \(summary)")
        return summary
    }

    private func apply(_ m: MonitorConfig, target: Target, config: Config, gen: Int) -> Outcome {
        let want = m.input(for: target)
        let addr = m.effectiveI2CAddress
        var wroteOK = false
        var lastError = ""
        var everFound = false

        // attempt 0: idempotency read + first write; attempts 1..retries: read back (verify) then re-write if needed.
        for attempt in 0...config.retries {
            if attempt > 0 {
                let delays = config.retryDelaysMs.isEmpty ? [300] : config.retryDelaysMs
                usleep(useconds_t(delays[min(attempt - 1, delays.count - 1)] * 1000))
            }
            if currentGeneration != gen { return .cancelled }

            // Re-enumerate every attempt: monitors come and go during hot-plug.
            guard let d = DDCDisplay.discover().first(where: m.matches) else {
                Log.debug("[\(m.name)] attempt \(attempt): not found among displays")
                if wroteOK { return .sentUnverified }   // it likely dropped off after switching away from us
                continue
            }
            everFound = true

            if config.backend != .external {
                do {
                    let cur = try d.read(vcp: m.vcpCode, address: addr).current
                    Log.debug("[\(m.name)] read VCP \(hex(m.vcpCode)) = \(hex16(cur))")
                    if inputMatches(cur, want) { return attempt == 0 ? .alreadyThere : .switched }
                    if attempt > 0 { Log.warn("[\(m.name)] readback \(hex16(cur)) != \(hex16(want)), retrying") }
                } catch {
                    Log.debug("[\(m.name)] read failed: \(error)")
                    lastError = "\(error)"
                }
            }
            if attempt == config.retries { break }   // last pass is verification only
            if wroteOK && !config.verifyReadback { return .sentUnverified }

            do {
                try write(m, display: d, value: want, config: config)
                wroteOK = true
                Log.info("[\(m.name)] wrote VCP \(hex(m.vcpCode)) = \(hex16(want)) (attempt \(attempt + 1))")
                if !config.verifyReadback { return .sentUnverified }
                if config.backend == .external { return .sentUnverified }   // external tools can't read back reliably
            } catch {
                lastError = "\(error)"
                Log.warn("[\(m.name)] write attempt \(attempt + 1) failed: \(error)")
            }
        }
        if wroteOK { return .sentUnverified }
        if !everFound { return .notFound }
        return .failed(lastError)
    }

    private func write(_ m: MonitorConfig, display d: DDCDisplay, value: UInt16, config: Config) throws {
        switch config.backend {
        case .native:
            try d.write(vcp: m.vcpCode, value: value, address: m.effectiveI2CAddress)
        case .external:
            try ExternalDDC.run(config.externalCommand, monitor: m, display: d, value: value)
        case .auto:
            do {
                try d.write(vcp: m.vcpCode, value: value, address: m.effectiveI2CAddress)
            } catch {
                Log.warn("[\(m.name)] native DDC write failed (\(error)); trying external command")
                try ExternalDDC.run(config.externalCommand, monitor: m, display: d, value: value)
            }
        }
    }
}

/// Some monitors set garbage in the high byte of VCP 0x60; compare the low byte when the target fits in one.
func inputMatches(_ current: UInt16, _ want: UInt16) -> Bool {
    want <= 0xFF ? (current & 0xFF) == want : current == want
}

func hex(_ v: UInt8) -> String { String(format: "0x%02X", v) }
func hex16(_ v: UInt16) -> String { String(format: "0x%02X", v) }

/// Fallback: shell out to m1ddc / BetterDisplay CLI / anything else the user configures.
enum ExternalDDC {
    static let pathEnv = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

    static func run(_ template: String, monitor m: MonitorConfig, display d: DDCDisplay, value: UInt16) throws {
        var cmd = template
            .replacingOccurrences(of: "{product}", with: d.productName)
            .replacingOccurrences(of: "{manufacturer}", with: d.manufacturer)
            .replacingOccurrences(of: "{serial}", with: d.serial)
            .replacingOccurrences(of: "{vcp}", with: String(m.vcpCode))
            .replacingOccurrences(of: "{value}", with: String(value))
            .replacingOccurrences(of: "{m1ddc_cmd}", with: m.vcpCode == 0xF4 ? "input-alt" : "input")
        if cmd.contains("{m1ddc_display}") {
            guard let idx = m1ddcIndex(for: d) else { throw DDCError("m1ddc does not list \(d.productName)") }
            cmd = cmd.replacingOccurrences(of: "{m1ddc_display}", with: String(idx))
        }
        let (status, output) = shell(cmd)
        Log.debug("external: `\(cmd)` -> \(status) \(output.trimmingCharacters(in: .whitespacesAndNewlines))")
        if status != 0 || output.localizedCaseInsensitiveContains("failure") {
            throw DDCError("external command failed (\(status)): \(output.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
    }

    /// `m1ddc display list` prints lines like "[1] C27G4Z (UUID)".
    private static func m1ddcIndex(for d: DDCDisplay) -> Int? {
        let (status, out) = shell("m1ddc display list")
        guard status == 0 else { return nil }
        for line in out.split(separator: "\n") {
            guard line.hasPrefix("["), let close = line.firstIndex(of: "]"),
                  let n = Int(line[line.index(after: line.startIndex)..<close]) else { continue }
            if line.localizedCaseInsensitiveContains(d.productName) { return n }
        }
        return nil
    }

    static func shell(_ cmd: String) -> (Int32, String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", cmd]
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = pathEnv + ":" + (env["PATH"] ?? "")
        p.environment = env
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return (-1, "\(error)") }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(data: data, encoding: .utf8) ?? "")
    }
}
