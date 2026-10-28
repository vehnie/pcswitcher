import Foundation
import IOKit

// DDC/CI on Apple Silicon via the private IOAVService API (same approach as m1ddc / MonitorControl).

private typealias IOAVServiceCreateWithServiceFn = @convention(c) (CFAllocator?, io_service_t) -> Unmanaged<CFTypeRef>?
private typealias IOAVServiceI2CFn = @convention(c) (CFTypeRef, UInt32, UInt32, UnsafeMutableRawPointer, UInt32) -> IOReturn

private enum AVServiceAPI {
    static let handle = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW)
    static let create: IOAVServiceCreateWithServiceFn? = load("IOAVServiceCreateWithService")
    static let readI2C: IOAVServiceI2CFn? = load("IOAVServiceReadI2C")
    static let writeI2C: IOAVServiceI2CFn? = load("IOAVServiceWriteI2C")

    static func load<T>(_ name: String) -> T? {
        guard let handle, let sym = dlsym(handle, name) else { return nil }
        return unsafeBitCast(sym, to: T.self)
    }

    static var available: Bool { create != nil && readI2C != nil && writeI2C != nil }
}

struct DDCError: Error, CustomStringConvertible {
    let description: String
    init(_ d: String) { description = d }
}

/// An external display reachable (maybe) over DDC/CI, identified by its EDID attributes.
final class DDCDisplay {
    let manufacturer: String
    let productName: String
    let productID: Int
    let serial: String
    let alphanumericSerial: String
    let edidUUID: String
    let registryID: UInt64
    private let service: CFTypeRef?

    init(manufacturer: String, productName: String, productID: Int, serial: String,
         alphanumericSerial: String, edidUUID: String, registryID: UInt64, service: CFTypeRef?) {
        self.manufacturer = manufacturer
        self.productName = productName
        self.productID = productID
        self.serial = serial
        self.alphanumericSerial = alphanumericSerial
        self.edidUUID = edidUUID
        self.registryID = registryID
        self.service = service
    }

    var displayName: String { "\(manufacturer) \(productName)" + (serial.isEmpty || serial == "0" ? "" : " #\(serial)") }
    var hasService: Bool { service != nil }

    private static let chipAddress: UInt32 = 0x37

    /// Set VCP feature. Packet: [0x84, 0x03, vcp, hi, lo, checksum].
    /// `address` is the DDC sub-address: 0x51 normally, 0x50 for LG's vendor-specific 0xF4 input switch.
    func write(vcp: UInt8, value: UInt16, address: UInt8 = 0x51) throws {
        guard let service, let writeI2C = AVServiceAPI.writeI2C else { throw DDCError("no IOAVService for \(displayName)") }
        var packet: [UInt8] = [0x84, 0x03, vcp, UInt8(value >> 8), UInt8(value & 0xFF), 0]
        packet[5] = packet[0..<5].reduce(0x6E ^ address, ^)
        var lastErr: IOReturn = kIOReturnSuccess
        // Writing twice is what MonitorControl does; some monitors drop the first packet.
        for _ in 0..<2 {
            usleep(10_000)
            lastErr = packet.withUnsafeMutableBytes { writeI2C(service, Self.chipAddress, UInt32(address), $0.baseAddress!, UInt32($0.count)) }
        }
        if lastErr != kIOReturnSuccess { throw DDCError(String(format: "IOAVServiceWriteI2C failed: 0x%08x", lastErr)) }
    }

    /// Get VCP feature. Returns (current, max).
    func read(vcp: UInt8, address: UInt8 = 0x51, attempts: Int = 4) throws -> (current: UInt16, max: UInt16) {
        guard let service, let writeI2C = AVServiceAPI.writeI2C, let readI2C = AVServiceAPI.readI2C else {
            throw DDCError("no IOAVService for \(displayName)")
        }
        var request: [UInt8] = [0x82, 0x01, vcp, 0]
        request[3] = request[0..<3].reduce(0x6E ^ address, ^)
        var lastProblem = "no reply"
        for attempt in 0..<attempts {
            if attempt > 0 { usleep(20_000) }
            var ok = true
            for _ in 0..<2 {
                usleep(10_000)
                let r = request.withUnsafeMutableBytes { writeI2C(service, Self.chipAddress, UInt32(address), $0.baseAddress!, UInt32($0.count)) }
                if r != kIOReturnSuccess { ok = false; lastProblem = String(format: "write request failed: 0x%08x", r) }
            }
            if !ok { continue }
            usleep(50_000)
            var reply = [UInt8](repeating: 0, count: 11)
            let r = reply.withUnsafeMutableBytes { readI2C(service, Self.chipAddress, 0, $0.baseAddress!, UInt32($0.count)) }
            if r != kIOReturnSuccess { lastProblem = String(format: "IOAVServiceReadI2C failed: 0x%08x", r); continue }
            let checksum = reply[0..<10].reduce(UInt8(0x50), ^)
            guard reply[2] == 0x02, reply[4] == vcp else {
                lastProblem = "unexpected reply " + reply.map { String(format: "%02x", $0) }.joined(separator: " ")
                continue
            }
            guard checksum == reply[10] else { lastProblem = "bad reply checksum"; continue }
            guard reply[3] == 0x00 else { throw DDCError(String(format: "monitor reports VCP 0x%02x unsupported", vcp)) }
            let max = UInt16(reply[6]) << 8 | UInt16(reply[7])
            let cur = UInt16(reply[8]) << 8 | UInt16(reply[9])
            return (cur, max)
        }
        throw DDCError("DDC read of VCP \(String(format: "0x%02x", vcp)) failed on \(displayName): \(lastProblem)")
    }

    // MARK: - Discovery

    /// Walks the IORegistry. Each framebuffer (AppleCLCD2 / IOMobileFramebufferShim) carrying an EDID is followed
    /// in the service plane by the DCPAVServiceProxy that drives its port; pair them up in order.
    static func discover() -> [DDCDisplay] {
        guard AVServiceAPI.available else {
            Log.error("IOAVService private API not available on this macOS version")
            return []
        }
        var result: [DDCDisplay] = []
        var iterator = io_iterator_t()
        let root = IORegistryGetRootEntry(kIOMainPortDefault)
        defer { IOObjectRelease(root) }
        guard IORegistryEntryCreateIterator(root, kIOServicePlane, IOOptionBits(kIORegistryIterateRecursively), &iterator) == KERN_SUCCESS else {
            return []
        }
        defer { IOObjectRelease(iterator) }

        var pending: [String: Any]? = nil
        var pendingUUID = ""
        var pendingID: UInt64 = 0
        while case let entry = IOIteratorNext(iterator), entry != 0 {
            defer { IOObjectRelease(entry) }
            let className = (IOObjectCopyClass(entry)?.takeRetainedValue() as String?) ?? ""
            if className == "AppleCLCD2" || className == "IOMobileFramebufferShim" {
                if let uuid = property(entry, "EDID UUID") as? String,
                   let attrs = property(entry, "DisplayAttributes") as? [String: Any] {
                    pending = attrs
                    pendingUUID = uuid
                    var id: UInt64 = 0
                    IORegistryEntryGetRegistryEntryID(entry, &id)
                    pendingID = id
                } else {
                    pending = nil
                }
            } else if className == "DCPAVServiceProxy", let attrs = pending {
                guard (property(entry, "Location") as? String) == "External" else { continue }
                let product = attrs["ProductAttributes"] as? [String: Any] ?? [:]
                let service = AVServiceAPI.create?(kCFAllocatorDefault, entry)?.takeRetainedValue()
                result.append(DDCDisplay(
                    manufacturer: product["ManufacturerID"] as? String ?? "",
                    productName: product["ProductName"] as? String ?? "",
                    productID: product["ProductID"] as? Int ?? 0,
                    serial: (product["SerialNumber"] as? Int).map(String.init) ?? "",
                    alphanumericSerial: product["AlphanumericSerialNumber"] as? String ?? "",
                    edidUUID: pendingUUID,
                    registryID: pendingID,
                    service: service))
                pending = nil
            }
        }
        return result
    }

    private static func property(_ entry: io_registry_entry_t, _ key: String) -> Any? {
        IORegistryEntryCreateCFProperty(entry, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
    }
}
