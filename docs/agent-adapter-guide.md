# Agent Adapter 开发指南

> 本指南面向"为 AgentPager 增加新 Agent 支持"的开发者。配合 [extensibility-refactor.md](./extensibility-refactor.md) 阅读。

---

## 目录

1. [前置条件](#一前置条件)
2. [最小改动清单](#二最小改动清单)
3. [步骤详解](#三步骤详解)
4. [测试要求](#四测试要求)
5. [完整示例：Claude Code Adapter](#五完整示例claude-code-adapter)

---

## 一、前置条件

在开始之前，请确认你已经具备：

- [x] 已阅读 [extensibility-refactor.md](./extensibility-refactor.md)
- [x] 已确认目标 Agent 的 Hook / Plugin 协议（参考 [multi-agent-support.md](./multi-agent-support.md)）
- [x] 已确认目标 Agent 的配置文件路径（如 `~/.claude/settings.json`）

---

## 二、最小改动清单

新增一个 Agent（如 Claude Code）需要的改动：

| # | 文件 | 操作 | 备注 |
|---|------|------|------|
| 1 | `windows/src/AgentPager.Core/WireModels.cs` | 编辑 | `AgentSource` 枚举加 1 个值 + `WireName` switch 加 1 个 case |
| 2 | `macos/Sources/AgentGridCore/Domain.swift` | 编辑 | `AgentSource` 枚举加 1 个 case |
| 3 | `android/.../domain/Models.kt` | 编辑 | `AgentSource` 枚举加 1 个常量 + `@SerialName` |
| 4 | `windows/src/AgentPager.Core/<Agent>AgentAdapter.cs` | **新建** | Windows 端 adapter 实现 |
| 5 | `macos/Sources/AgentGridCore/<Agent>AgentAdapter.swift` | **新建** | macOS 端 adapter 实现 |
| 6 | `windows/src/AgentPager.Bridge/BridgeRuntime.cs` | 编辑 | `Start()` 中调用 `AgentRegistry.Register(new <Agent>AgentAdapter(...))` |
| 7 | `macos/.../AgentGridBridge/BridgeModel.swift` | 编辑 | macOS 端注册 |
| 8 | 资源文件 | 新建 | 8-bit 风格的 Agent icon（128×128 PNG） |

UI 层（`BridgeViews.swift` / `TrayController.cs` / `MainWindow.xaml.cs` / `AgentGridScreen.kt` / `PixelCoreSurfaceView.kt`）**无需手动改动**——它们会自动遍历 `AgentRegistry.All` 生成 UI。

---

## 三、步骤详解

### Step 1: 扩展 `AgentSource` 枚举（三端同步）

**Windows** (`WireModels.cs`):
```csharp
public enum AgentSource
{
    CodexDesktop,
    CodexCLI,
    ClaudeCode,    // 新增
}
```

并更新 `WireName` 方法：
```csharp
nameof(AgentSource.ClaudeCode) => "claudeCode",
```

**macOS** (`Domain.swift`):
```swift
public enum AgentSource: String, Codable, Sendable {
    case codexDesktop
    case codexCLI
    case claudeCode    // 新增（注意 rawValue 就是 wire name）
}
```

**Android** (`Models.kt`):
```kotlin
enum class AgentSource {
    @SerialName("codexDesktop") CODEX_DESKTOP,
    @SerialName("codexCLI") CODEX_CLI,
    @SerialName("claudeCode") CLAUDE_CODE,    // 新增
}
```

### Step 2: 创建 Windows Adapter

参考 `AgentAdapterTemplate.cs`（位于 `windows/src/AgentPager.Core/AgentAdapterTemplate.cs`，由 `#if AGENT_ADAPTER_TEMPLATE` 包裹，不进入发布产物）。

最小实现骨架：

```csharp
public sealed class ClaudeCodeAgentAdapter : IAgentAdapter
{
    public AgentSource Source => AgentSource.ClaudeCode;

    public AgentDescriptor Descriptor => new(
        Source: Source,
        DisplayName: "Claude Code",
        ShortName: "CLD",
        BrandColorArgb: 0xFFD97757,
        IconKey: "ic_agent_claude"
    );

    public HookTransportKind Transport => HookTransportKind.CommandHook;

    public bool IsInstalled() { /* 读 ~/.claude/settings.json */ }
    public HookInstallResult Install(string exePath) { /* 合并 hook 配置 */ }
    public HookInstallResult Uninstall() { /* 移除标记的 hook 组 */ }

    // 可选：override ScanRollout / LoadUsage / ReadTitle
}
```

### Step 3: 在 BridgeRuntime 注册

```csharp
// BridgeRuntime.cs
private void RegisterAgents()
{
    AgentRegistry.Register(new CodexAgentAdapter(_codexHome));
    AgentRegistry.Register(new ClaudeCodeAgentAdapter());    // 新增
}
```

### Step 4: 创建 macOS Adapter

镜像 Windows 实现，遵循 Swift 协议：

```swift
public final class ClaudeCodeAgentAdapter: AgentAdapter {
    public let source: AgentSource = .claudeCode
    public let descriptor = AgentDescriptor(...)
    public let transport: HookTransportKind = .commandHook

    public func isInstalled() -> Bool { /* ... */ }
    public func install(bridgeExecutablePath: String) throws -> HookInstallResult { /* ... */ }
    public func uninstall() throws -> HookInstallResult { /* ... */ }
}
```

并在 `BridgeModel.swift` 中注册：

```swift
AgentRegistry.register(CodexAgentAdapter())
AgentRegistry.register(ClaudeCodeAgentAdapter())
```

### Step 5: 添加 UI 资源

- 8-bit 风格的 PNG icon（建议 128×128，3-4 色）
- 命名遵循 `ic_agent_<shortname>` 模式（如 `ic_agent_claude`）

UI 层会在 task 卡片右上角自动显示该 icon。

---

## 四、测试要求

每个 Adapter **至少**要有 3 个测试：

| 测试 | 验证什么 |
|------|----------|
| `IsInstalled_ReturnsCorrectState` | 已安装 / 未安装 两种情况下返回值正确 |
| `Install_PreservesUserHooks` | 安装后用户的原有 hook 不丢失 |
| `Uninstall_RemovesManagedHooksOnly` | 卸载时只移除 `statusMessage: "Managed by AgentPager"` 标记的 hook |

参考 `macos/Tests/AgentGridCoreTests/CodexHookConfigurationTests.swift` 编写测试。

---

## 五、完整示例：Claude Code Adapter

详见后续 PR：[`feat: add Claude Code Adapter`](#)