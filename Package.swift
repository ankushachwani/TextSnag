// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TextSnag",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "TextSnag",
            path: "Sources/TextSnag",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("Vision"),
                .linkedFramework("Carbon"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("ServiceManagement"),
            ]
        )
    ]
)
