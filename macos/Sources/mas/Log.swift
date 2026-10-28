import Foundation

/// Rotating file logger. Also echoes to stderr when `verbose` is set (CLI / `mas watch`).
enum Log {
    static var verbose = false
    static let maxBytes = 2 * 1024 * 1024
    static let keepFiles = 3

    static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/MonitorAutoSwitch")
    }
    static var fileURL: URL { directory.appendingPathComponent("mas.log") }

    private static let queue = DispatchQueue(label: "mas.log")
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    static func info(_ msg: String) { write("INFO ", msg) }
    static func warn(_ msg: String) { write("WARN ", msg) }
    static func error(_ msg: String) { write("ERROR", msg) }
    static func debug(_ msg: String) { write("DEBUG", msg) }

    private static func write(_ level: String, _ msg: String) {
        let line = "\(formatter.string(from: Date())) [\(level)] \(msg)\n"
        if verbose { FileHandle.standardError.write(line.data(using: .utf8)!) }
        queue.async {
            let fm = FileManager.default
            try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
            rotateIfNeeded()
            if let h = try? FileHandle(forWritingTo: fileURL) {
                h.seekToEndOfFile()
                h.write(line.data(using: .utf8)!)
                try? h.close()
            } else {
                try? line.data(using: .utf8)!.write(to: fileURL)
            }
        }
    }

    private static func rotateIfNeeded() {
        let fm = FileManager.default
        guard let size = (try? fm.attributesOfItem(atPath: fileURL.path))?[.size] as? Int, size > maxBytes else { return }
        for i in stride(from: keepFiles - 1, through: 1, by: -1) {
            let src = directory.appendingPathComponent(i == 1 ? "mas.log" : "mas.\(i - 1).log")
            let dst = directory.appendingPathComponent("mas.\(i).log")
            try? fm.removeItem(at: dst)
            try? fm.moveItem(at: src, to: dst)
        }
    }

    /// Block until queued writes hit disk (used before exiting the CLI).
    static func flush() { queue.sync {} }
}
