import Foundation

struct MonitorConfig {
    var name: String
    var manufacturer = ""
    var model = ""
    var serial = ""
    var vcpCode: UInt8 = 0x60
    var i2cAddress: UInt8? = nil      // defaults to 0x51, or 0x50 for LG's 0xF4
    var macInput: UInt16 = 0x11
    var pcInput: UInt16 = 0x0F
    /// false for monitors whose VCP 0x60 readout lies (e.g. AOC 24G1WG4 keeps reporting HDMI while on DP):
    /// always write, never skip as "already there", don't verify.
    var reliableReadback = true

    var effectiveI2CAddress: UInt8 { i2cAddress ?? (vcpCode == 0xF4 ? 0x50 : 0x51) }

    func input(for target: Target) -> UInt16 { target == .mac ? macInput : pcInput }

    func matches(_ d: DDCDisplay) -> Bool {
        if !manufacturer.isEmpty, manufacturer.caseInsensitiveCompare(d.manufacturer) != .orderedSame { return false }
        if !model.isEmpty {
            let byName = model.caseInsensitiveCompare(d.productName) == .orderedSame
            let byID = parseInt(model).map { $0 == d.productID } ?? false
            if !byName && !byID { return false }
        }
        if !serial.isEmpty, serial != d.serial, serial != d.alphanumericSerial { return false }
        return true
    }
}

enum DDCBackend: String {
    case auto        // native IOAVService, fall back to external command on failure
    case native
    case external    // always use the external command
}

struct Config {
    var sentinels: [(vid: Int, pid: Int)] = []
    var debounceMs = 500
    var retries = 3
    var retryDelaysMs = [300, 600, 1200]
    var verifyReadback = true
    var onConnect = true
    var onDisconnect = true
    var backend: DDCBackend = .auto
    /// Shell command used by the external backend. Placeholders: {product} {manufacturer} {serial} {vcp} {value} {m1ddc_cmd}
    var externalCommand = "m1ddc display {m1ddc_display} set {m1ddc_cmd} {value}"
    var monitors: [MonitorConfig] = []

    static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/MonitorAutoSwitch")
    }
    static var fileURL: URL { directory.appendingPathComponent("config.toml") }

    func isSentinel(vid: Int, pid: Int) -> Bool { sentinels.contains { $0.vid == vid && $0.pid == pid } }

    static func load() throws -> Config {
        let text = try String(contentsOf: fileURL, encoding: .utf8)
        return try parse(text)
    }

    static func parse(_ text: String) throws -> Config {
        let doc = try TOML.parse(text)
        var c = Config()
        for s in doc.root["sentinels"] as? [Any] ?? [] {
            guard let s = s as? String else { throw TOMLError("sentinels must be strings like \"046d:085c\"") }
            let parts = s.split(separator: ":")
            guard parts.count == 2, let v = Int(parts[0], radix: 16), let p = Int(parts[1], radix: 16) else {
                throw TOMLError("bad sentinel '\(s)', expected VID:PID in hex")
            }
            c.sentinels.append((v, p))
        }
        if let v = doc.root["debounce_ms"] as? Int { c.debounceMs = v }
        if let v = doc.root["retries"] as? Int { c.retries = max(1, v) }
        if let v = doc.root["retry_delays_ms"] as? [Any] { c.retryDelaysMs = v.compactMap { $0 as? Int } }
        if let v = doc.root["verify_readback"] as? Bool { c.verifyReadback = v }
        if let v = doc.root["on_connect"] as? Bool { c.onConnect = v }
        if let v = doc.root["on_disconnect"] as? Bool { c.onDisconnect = v }
        if let v = doc.root["ddc_backend"] as? String {
            guard let b = DDCBackend(rawValue: v) else { throw TOMLError("ddc_backend must be auto, native or external") }
            c.backend = b
        }
        if let v = doc.root["external_command"] as? String { c.externalCommand = v }

        for (i, t) in (doc.arrays["monitors"] ?? []).enumerated() {
            var m = MonitorConfig(name: t["name"] as? String ?? "Monitor \(i + 1)")
            if let match = t["match"] as? [String: Any] {
                m.manufacturer = match["manufacturer"] as? String ?? ""
                m.model = (match["model"] as? String) ?? (match["model"] as? Int).map(String.init) ?? ""
                m.serial = (match["serial"] as? String) ?? (match["serial"] as? Int).map(String.init) ?? ""
            }
            if let v = t["vcp_code"] as? Int { m.vcpCode = UInt8(truncatingIfNeeded: v) }
            if let v = t["i2c_address"] as? Int { m.i2cAddress = UInt8(truncatingIfNeeded: v) }
            if let v = t["mac_input"] as? Int { m.macInput = UInt16(truncatingIfNeeded: v) }
            if let v = t["pc_input"] as? Int { m.pcInput = UInt16(truncatingIfNeeded: v) }
            if let v = t["reliable_readback"] as? Bool { m.reliableReadback = v }
            c.monitors.append(m)
        }
        return c
    }

    /// Starter config pre-filled with the EDID data of the currently connected monitors.
    static func template(for displays: [DDCDisplay]) -> String {
        var s = """
        # MonitorAutoSwitch config (macOS agent). Same schema as the Windows agent.

        # Sentinel USB device(s) behind the USB switch (VID:PID, hex). Find them with `mas list-usb`.
        # The group counts as "present" if ANY of them is connected.
        sentinels = []

        debounce_ms     = 500
        retries         = 3
        retry_delays_ms = [300, 600, 1200]
        verify_readback = true

        # Which rules this agent applies
        on_connect    = true    # sentinel appears on this Mac   -> monitors to mac_input
        on_disconnect = true    # sentinel leaves this Mac       -> monitors to pc_input

        # "auto" = native DDC, fall back to external_command if it fails; "native"; "external"
        ddc_backend = "auto"
        # Placeholders: {product} {manufacturer} {serial} {vcp} {value} {m1ddc_display} {m1ddc_cmd}
        external_command = "m1ddc display {m1ddc_display} set {m1ddc_cmd} {value}"

        # Common VCP 0x60 values: 0x0F DP1, 0x10 DP2, 0x11 HDMI1, 0x12 HDMI2, 0x1B USB-C
        # Confirm yours with `mas get-input <monitor>` while each computer is shown.

        """
        let list = displays.isEmpty ? [nil] : displays.map { Optional($0) }
        for (i, d) in list.enumerated() {
            s += """

            [[monitors]]
            name      = "\(d?.productName ?? "Monitor \(i + 1)")"
            match     = { manufacturer = "\(d?.manufacturer ?? "")", model = "\(d?.productName ?? "")", serial = "\(d?.serial ?? "")" }
            vcp_code  = 0x60
            mac_input = 0x11    # HDMI 1
            pc_input  = 0x0F    # DisplayPort 1

            """
        }
        return s
    }
}

func parseInt(_ s: String) -> Int? {
    let t = s.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "_", with: "")
    if t.lowercased().hasPrefix("0x") { return Int(t.dropFirst(2), radix: 16) }
    return Int(t)
}

// MARK: - Minimal TOML subset (enough for this config): key = value, [[array.tables]], [tables],
// strings, ints (dec/hex), bools, arrays (may span lines), inline tables, # comments.

struct TOMLError: Error, CustomStringConvertible {
    let description: String
    init(_ d: String) { description = d }
}

enum TOML {
    struct Document {
        var root: [String: Any] = [:]
        var arrays: [String: [[String: Any]]] = [:]
    }

    static func parse(_ text: String) throws -> Document {
        var doc = Document()
        var currentArray: String? = nil
        var currentTable: String? = nil
        var pending = ""
        var lineNo = 0

        for raw in text.components(separatedBy: .newlines) {
            lineNo += 1
            let line = stripComment(raw).trimmingCharacters(in: .whitespaces)
            if pending.isEmpty && line.isEmpty { continue }
            if pending.isEmpty && line.hasPrefix("[[") {
                guard line.hasSuffix("]]") else { throw TOMLError("line \(lineNo): bad table header") }
                let name = String(line.dropFirst(2).dropLast(2)).trimmingCharacters(in: .whitespaces)
                doc.arrays[name, default: []].append([:])
                currentArray = name; currentTable = nil
                continue
            }
            if pending.isEmpty && line.hasPrefix("[") {
                currentTable = String(line.dropFirst().dropLast()).trimmingCharacters(in: .whitespaces)
                currentArray = nil
                continue
            }
            pending += (pending.isEmpty ? "" : " ") + line
            if bracketDepth(pending) > 0 { continue }   // multi-line array / inline table
            let stmt = pending
            pending = ""

            guard let eq = stmt.firstIndex(of: "=") else { throw TOMLError("line \(lineNo): expected key = value") }
            let key = stmt[..<eq].trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            var scanner = Scanner(String(stmt[stmt.index(after: eq)...]))
            let value: Any
            do { value = try scanner.value() } catch let e as TOMLError { throw TOMLError("line \(lineNo): \(e.description)") }

            if let a = currentArray {
                doc.arrays[a]![doc.arrays[a]!.count - 1][key] = value
            } else if let t = currentTable {
                var table = doc.root[t] as? [String: Any] ?? [:]
                table[key] = value
                doc.root[t] = table
            } else {
                doc.root[key] = value
            }
        }
        if !pending.isEmpty { throw TOMLError("unterminated array or inline table") }
        return doc
    }

    private static func stripComment(_ s: String) -> String {
        var inString = false
        var out = ""
        for ch in s {
            if ch == "\"" { inString.toggle() }
            if ch == "#" && !inString { break }
            out.append(ch)
        }
        return out
    }

    private static func bracketDepth(_ s: String) -> Int {
        var depth = 0, inString = false
        for ch in s {
            if ch == "\"" { inString.toggle() }
            if inString { continue }
            if ch == "[" || ch == "{" { depth += 1 }
            if ch == "]" || ch == "}" { depth -= 1 }
        }
        return depth
    }

    private struct Scanner {
        let chars: [Character]
        var i = 0
        init(_ s: String) { chars = Array(s) }

        mutating func skipWS() { while i < chars.count, chars[i].isWhitespace { i += 1 } }

        mutating func value() throws -> Any {
            skipWS()
            guard i < chars.count else { throw TOMLError("missing value") }
            switch chars[i] {
            case "\"":
                i += 1
                var s = ""
                while i < chars.count, chars[i] != "\"" {
                    if chars[i] == "\\", i + 1 < chars.count { i += 1 }
                    s.append(chars[i]); i += 1
                }
                guard i < chars.count else { throw TOMLError("unterminated string") }
                i += 1
                return s
            case "[":
                i += 1
                var arr: [Any] = []
                while true {
                    skipWS()
                    guard i < chars.count else { throw TOMLError("unterminated array") }
                    if chars[i] == "]" { i += 1; return arr }
                    arr.append(try value())
                    skipWS()
                    if i < chars.count, chars[i] == "," { i += 1 }
                }
            case "{":
                i += 1
                var t: [String: Any] = [:]
                while true {
                    skipWS()
                    guard i < chars.count else { throw TOMLError("unterminated inline table") }
                    if chars[i] == "}" { i += 1; return t }
                    var key = ""
                    while i < chars.count, chars[i] != "=" { key.append(chars[i]); i += 1 }
                    guard i < chars.count else { throw TOMLError("expected = in inline table") }
                    i += 1
                    t[key.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\""))] = try value()
                    skipWS()
                    if i < chars.count, chars[i] == "," { i += 1 }
                }
            default:
                var tok = ""
                while i < chars.count, !",]}".contains(chars[i]), !chars[i].isWhitespace { tok.append(chars[i]); i += 1 }
                if tok == "true" { return true }
                if tok == "false" { return false }
                if let n = parseInt(tok) { return n }
                throw TOMLError("cannot parse value '\(tok)'")
            }
        }
    }
}
