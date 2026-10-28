import Foundation
import IOKit
import IOKit.usb

struct USBDevice {
    let vid: Int
    let pid: Int
    let name: String
    let vendor: String
    let registryID: UInt64

    var vidpid: String { String(format: "%04x:%04x", vid, pid) }

    init?(_ service: io_service_t) {
        func prop(_ k: String) -> Any? { IORegistryEntryCreateCFProperty(service, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() }
        guard let vid = prop("idVendor") as? Int, let pid = prop("idProduct") as? Int else { return nil }
        self.vid = vid
        self.pid = pid
        name = (prop("USB Product Name") as? String) ?? (prop("kUSBProductString") as? String) ?? "?"
        vendor = (prop("USB Vendor Name") as? String) ?? (prop("kUSBVendorString") as? String) ?? ""
        var id: UInt64 = 0
        IORegistryEntryGetRegistryEntryID(service, &id)
        registryID = id
    }

    static func all() -> [USBDevice] {
        var it = io_iterator_t()
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOUSBHostDevice"), &it) == KERN_SUCCESS else { return [] }
        defer { IOObjectRelease(it) }
        var out: [USBDevice] = []
        while case let s = IOIteratorNext(it), s != 0 {
            if let d = USBDevice(s) { out.append(d) }
            IOObjectRelease(s)
        }
        return out
    }
}

/// Watches IOUSBHostDevice arrivals/removals and reports whether any sentinel device is present.
/// Callbacks are delivered on the main run loop.
final class USBWatcher {
    private let isSentinel: (Int, Int) -> Bool
    private let onChange: (_ present: Bool, _ event: String) -> Void
    private var port: IONotificationPortRef?
    private var addedIter = io_iterator_t()
    private var removedIter = io_iterator_t()
    private var present: [UInt64: String] = [:]   // registryID -> description

    var isPresent: Bool { !present.isEmpty }

    init(isSentinel: @escaping (Int, Int) -> Bool, onChange: @escaping (Bool, String) -> Void) {
        self.isSentinel = isSentinel
        self.onChange = onChange
    }

    func start() {
        port = IONotificationPortCreate(kIOMainPortDefault)
        CFRunLoopAddSource(CFRunLoopGetMain(), IONotificationPortGetRunLoopSource(port).takeUnretainedValue(), .defaultMode)
        let ctx = Unmanaged.passUnretained(self).toOpaque()

        IOServiceAddMatchingNotification(port, kIOFirstMatchNotification, IOServiceMatching("IOUSBHostDevice"), { ctx, it in
            Unmanaged<USBWatcher>.fromOpaque(ctx!).takeUnretainedValue().drain(it, added: true, initial: false)
        }, ctx, &addedIter)
        IOServiceAddMatchingNotification(port, kIOTerminatedNotification, IOServiceMatching("IOUSBHostDevice"), { ctx, it in
            Unmanaged<USBWatcher>.fromOpaque(ctx!).takeUnretainedValue().drain(it, added: false, initial: false)
        }, ctx, &removedIter)

        // Draining arms the notifications and gives us the state at startup.
        drain(addedIter, added: true, initial: true)
        drain(removedIter, added: false, initial: true)
        Log.info("USB watcher started; sentinel " + (isPresent ? "PRESENT: \(present.values.joined(separator: ", "))" : "ABSENT"))
    }

    func stop() {
        if addedIter != 0 { IOObjectRelease(addedIter); addedIter = 0 }
        if removedIter != 0 { IOObjectRelease(removedIter); removedIter = 0 }
        if let port { IONotificationPortDestroy(port) }
        port = nil
        present.removeAll()
    }

    private func drain(_ it: io_iterator_t, added: Bool, initial: Bool) {
        while case let s = IOIteratorNext(it), s != 0 {
            defer { IOObjectRelease(s) }
            var id: UInt64 = 0
            IORegistryEntryGetRegistryEntryID(s, &id)
            if added {
                guard let d = USBDevice(s), isSentinel(d.vid, d.pid) else { continue }
                present[id] = "\(d.vidpid) \(d.name)"
                if !initial {
                    Log.info("USB connected: \(d.vidpid) \(d.name) (sentinel)")
                    onChange(true, "connected \(d.vidpid)")
                }
            } else if let desc = present.removeValue(forKey: id) {
                Log.info("USB disconnected: \(desc) (sentinel); remaining sentinels: \(present.count)")
                onChange(!present.isEmpty, "disconnected \(desc)")
            }
        }
    }
}
