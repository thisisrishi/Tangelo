// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Tangelo",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Tangelo",
            path: "Sources/Tangelo",
            swiftSettings: [.enableUpcomingFeature("BareSlashRegexLiterals")],
            linkerSettings: [
                .linkedFramework("AppKit"), .linkedFramework("AVFoundation"),
                .linkedFramework("PDFKit"), .linkedFramework("Vision"),
                .linkedFramework("CoreImage"), .linkedFramework("ServiceManagement"),
                .linkedFramework("QuickLookUI"), .linkedFramework("UniformTypeIdentifiers"),
            ]
        )
    ]
)
