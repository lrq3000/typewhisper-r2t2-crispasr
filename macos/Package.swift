// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "R2T2CrispASR",
    platforms: [.macOS(.v14)],
    products: [.library(name: "R2T2CrispASR", type: .dynamic, targets: ["R2T2CrispASR"])],
    dependencies: [.package(path: "../.deps/mac-sdk")],
    targets: [
        .target(name: "R2T2CrispASR", dependencies: [.product(name: "TypeWhisperPluginSDK", package: "mac-sdk")]),
        .testTarget(name: "R2T2CrispASRTests", dependencies: ["R2T2CrispASR"])
    ],
    swiftLanguageModes: [.v6]
)
