// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "MonitorAutoSwitch",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "mas",
            path: "Sources/mas",
            linkerSettings: [
                .linkedFramework("IOKit"),
                .linkedFramework("AppKit"),
            ]
        )
    ]
)
