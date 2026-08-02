// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "AgentPager",
    platforms: [
        .macOS(.v14),
    ],
    products: [
        .library(name: "AgentGridCore", targets: ["AgentGridCore"]),
        .executable(name: "AgentGridBridge", targets: ["AgentGridBridge"]),
        .executable(name: "AgentGridHooks", targets: ["AgentGridHooks"]),
    ],
    targets: [
        .target(name: "AgentGridCore"),
        .executableTarget(
            name: "AgentGridBridge",
            dependencies: ["AgentGridCore"],
            linkerSettings: [
                .unsafeFlags([
                    "-Xlinker", "-sectcreate",
                    "-Xlinker", "__TEXT",
                    "-Xlinker", "__info_plist",
                    "-Xlinker", "AppInfo.plist"
                ])
            ]
        ),
        .executableTarget(
            name: "AgentGridHooks",
            dependencies: ["AgentGridCore"]
        ),
        .testTarget(
            name: "AgentGridCoreTests",
            dependencies: ["AgentGridCore"]
        ),
    ]
)
