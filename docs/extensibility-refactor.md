# AgentPager 架构可扩展性分析与重构方案

> 评估：将来增加其他 Agent 能否做到"最少改动"？

---

## 目录

1. [客观答案](#一客观答案目前不是但可以做到接近加-agent-跟加皮肤一样)
2. [当前耦合度实测清单](#二当前耦合度加-claude-code-需要改的地方)
3. [两步重构方案](#三改造成接近零改动的两步方案)
4. [改动量对比](#四最终改动量对比)
5. [诚实说明的限制](#五有几个点要诚实告诉你)
6. [实施建议](#六我的建议)

---

## 一、客观答案：目前不是，但可以做到接近"加 agent 跟加皮肤一样"

加一个新 Agent **当前需要触 14-16 个文件、3 个平台同步改动**。下面是真凭实据的清单，以及把它降到 **1 个 adapter + 1 套资源** 的具体方案。

---

## 二、当前耦合度（加 Claude Code 需要改的地方）

| #  | 文件                                                    | 改动                                                              |
|----|--------------------------------------------------------|------------------------------------------------------------------|
| 1  | `windows/src/AgentPager.Core/WireModels.cs`            | 加 `ClaudeCode` 到 `AgentSource` 枚举 + 序列化映射                |
| 2  | `macos/Sources/AgentGridCore/Domain.swift`             | 同上                                                             |
| 3  | `android/.../domain/Models.kt`                         | 同上                                                             |
| 4  | `windows/.../AgentPager.Core/`                         | 复制 `CodexHooks.cs` → `ClaudeCodeHooks.cs`，改 events / payload / settings.json 路径 |
| 5  | `windows/.../AgentPager.Core/`                         | 复制 `CodexRolloutObserver.cs` → `ClaudeCodeRolloutObserver.cs`，改监听路径 |
| 6  | `windows/.../AgentPager.Core/`                         | 复制 `CodexUsageLoader.cs` → `ClaudeCodeUsageLoader.cs`          |
| 7  | `windows/.../AgentPager.Bridge/BridgeRuntime.cs`       | `_hookConfiguration` 类型替换 + 注册新 adapter                   |
| 8  | `windows/.../AgentPager.Bridge/TrayController.cs`      | 加 `ToolStripMenuItem("安装或修复 Claude Code Hook", ...)`        |
| 9  | `windows/.../AgentPager.Bridge/MainWindow.xaml.cs`     | UI 按钮事件                                                       |
| 10 | `macos/.../AgentGridBridge/BridgeModel.swift`          | 注册新 adapter + `installHooks` 改名                              |
| 11 | `macos/.../AgentGridBridge/BridgeViews.swift`          | UI 按钮                                                           |
| 12 | `android/.../ui/AgentGridTheme.kt`                     | 加 `AgentGridColors.ClaudeOrange` 等                              |
| 13 | `android/.../ui/AgentGridScreen.kt`                    | 加 `MetaTag("Claude", ClaudeOrange, alpha)`                       |
| 14 | `android/.../render/PixelCoreSurfaceView.kt`           | 调色板映射加分支                                                  |
| 15 | `macos/.../AgentGridHooks/main.swift`                  | Hook 二进制命令行分发（因为不同 agent 的 hook payload 字段不同）  |
| 16 | 测试：每端 4-6 个新测试文件                            | —                                                                 |

**根因**：所有 Agent 特定代码都用 `Codex*` 命名直接堆在 core 里，没有抽象。

---

## 三、改造成"接近零改动"的两步方案

### 第 1 步：抽 `IAgentAdapter` 接口 + 注册表（改动量：~14 → 3）

#### 核心抽象（Windows 为例，macOS / Kotlin 完全镜像）

```csharp
// 新文件 AgentPager.Core/IAgentAdapter.cs
public interface IAgentAdapter {
    AgentSource Source { get; }
    AgentDescriptor Descriptor { get; }      // 名字 / icon / 颜色 / 品牌
    HookTransportKind Transport { get; }     // CommandHook | HttpHook | Plugin

    bool IsInstalled();
    HookInstallResult Install(string bridgeExecutable);
    void Uninstall();

    // 可选能力：rollout 增量、额度查询、标题读取
    IReadOnlyList<RolloutSignal>? ScanRollout(DateTimeOffset since) => null;
    UsageSnapshot? LoadUsage() => null;
    string? ReadTitle(string sessionId) => null;
}

public sealed record AgentDescriptor(
    AgentSource Source,
    string DisplayName,       // "Codex" / "Claude Code" / "Codebuddy" / "OpenCode"
    string ShortName,         // "CDX" / "CLD" / "CB" / "OC"
    uint BrandColor,          // 0xFF5BC0BE
    string IconKey            // 资源 key
);

public enum HookTransportKind { CommandHook, HttpHook, Plugin }
```

#### Hook payload 解耦

把 `CodexHookPayload` 改成通用 `HookEnvelope`：

```csharp
public sealed record HookEnvelope(
    string Source,            // "codexCLI" / "claudeCode" / "codebuddy" / "openCode"
    string HookEventName,     // "PreToolUse" / "PermissionRequest" ...
    string SessionId,
    string Cwd,
    JsonElement Raw,          // 原始 JSON，adapter 自行解析
    string? ToolName,
    string? ToolUseId,
    string? Prompt,
    string? TranscriptPath
);
```

#### 注册表

```csharp
public static class AgentRegistry {
    private static readonly Dictionary<AgentSource, IAgentAdapter> _adapters = new();
    public static void Register(IAgentAdapter adapter) => _adapters[adapter.Source] = adapter;
    public static IEnumerable<IAgentAdapter> All => _adapters.Values;
    public static IAgentAdapter? Get(AgentSource source) => _adapters.GetValueOrDefault(source);
}

// 启动时
AgentRegistry.Register(new CodexAdapter());
AgentRegistry.Register(new ClaudeCodeAdapter());
AgentRegistry.Register(new CodebuddyAdapter());
AgentRegistry.Register(new OpenCodeAdapter());
```

#### Hook TCP Server 改造

```csharp
// 旧
public HookTcpServer(Func<CodexHookPayload, Task> hookHandler, ...)
// 新
public HookTcpServer(Func<HookEnvelope, Task> hookHandler, ...)
```

Server 解析 `source` 字段后，**自动路由到对应 adapter 的 reducer**。

#### Bridge UI 数据驱动

```csharp
// TrayController.cs 旧
new ToolStripMenuItem("安装或修复 Codex Hook", null, (_, _) => _runtime.InstallHook()),
// 新
foreach (var adapter in AgentRegistry.All) {
    var item = new ToolStripMenuItem(
        $"安装或修复 {adapter.Descriptor.DisplayName} Hook",
        null,
        (_, _) => adapter.Install(Environment.ProcessPath!)
    );
    menu.Items.Add(item);
}
```

- **`TrayController` 一行不动**，自动出现新 agent 入口
- **`BridgeViews` 一行不动**，自动出现新 agent 按钮
- **`MainWindow` 一行不动**

#### 第 1 步成果

加一个新 agent 触 3 个文件：

1. **新文件** `ClaudeCodeAdapter.cs`（Windows）/ `ClaudeCodeAdapter.swift`（macOS）
2. `WireModels.cs` / `Domain.swift` / `Models.kt` 加 1 个枚举值（3 个文件，但改动量极小）
3. **新文件** `AgentGridColors.Claude` + `MetaTag` 注册 + icon 资源（Android 端，可以一次性创建 1 个 `agents.yaml` 集中管理，见第 2 步）

**协议层 0 改动**（除了枚举值），**Bridge UI 0 改动**，**手机 UI 自动出现新 source 的卡片**。

---

### 第 2 步：UI 资源也数据驱动（改动量：3 → 1）

把颜色 / icon / 短名从代码里抽到 **单一资源文件**：

#### `android/app/src/main/res/values/agents.yaml`（或 JSON）

```yaml
- source: codexCLI
  displayName: Codex
  shortName: CDX
  brandColor: "#FF5BC0BE"
  iconKey: ic_agent_codex
- source: claudeCode
  displayName: Claude Code
  shortName: CLD
  brandColor: "#FFD97757"
  iconKey: ic_agent_claude
- source: codebuddy
  displayName: Codebuddy
  shortName: CB
  brandColor: "#FF8B5CF6"
  iconKey: ic_agent_codebuddy
- source: openCode
  displayName: OpenCode
  shortName: OC
  brandColor: "#FF06B6D4"
  iconKey: ic_agent_opencode
```

构建时（`scripts/`）读这个 YAML 生成 `AgentGridColors` Kotlin 常量 + 资源 id 映射。

#### Kotlin 代码改造

```kotlin
// AgentGridScreen.kt 旧
MetaTag("Codex", AgentGridColors.Blue, alpha)

// 新
val descriptor = AgentRegistry.descriptor(task.source)
MetaTag(descriptor.shortName, descriptor.brandColor, alpha)
```

`PixelCoreSurfaceView.kt` 调色板同理。

#### 第 2 步成果

加一个新 agent 触 **1 个文件 + 1 套资源**：

1. **新文件** `ClaudeCodeAdapter.cs` / `.swift`
2. `agents.yaml` 加 1 条 + 放一个 icon 文件
3. `AgentSource` 枚举加 1 个值（3 处）

---

## 四、最终改动量对比

| 阶段                  | 加一个 agent 触文件数 | 平台数 | 风险                                         |
|-----------------------|----------------------|--------|--------------------------------------------|
| **当前**              | 14-16                | 3      | 中（涉及核心 reducer，要回归测试）            |
| **第 1 步后**         | 3                    | 3      | 低（adapter 自包含，UI 自动出现）             |
| **第 2 步后**         | 1 + 1 资源            | 3      | 极低（资源 + 适配器，业务代码零侵入）         |

---

## 五、有几个点要诚实告诉你

### 1. `AgentSource` 枚举还是要改

它是三端通信协议的一部分（手机端要知道这是 Claude 还是 Codex 才好渲染），无法规避。但只是 3 行 enum 改动，不是负担。

### 2. `CodexPaths.Home` 这种小工具类要不要也抽象？

我倾向于**不抽象**，因为它只是个常量辅助类，加 agent 时 copy 一份 `ClaudePaths` 反而清晰。

### 3. OpenCode 是 plugin 不是 hook，adapter 形态差异很大

建议**为它单独留一个 `OpenCodePluginAdapter`**，其他三个都实现 `CommandHookAdapter` 抽象基类，避免被 OpenCode 的特殊性反向污染。

### 4. 测试要跟上

每个 adapter 至少要有 3 个测试（`IsInstalled / Install / Uninstall`），第 1 步后总测试量减半（不用改 `TaskCatalog` 等核心测试）。

---

## 六、我的建议

如果你重视后续扩展，**第 1 步的 ROI 最高**：

- 改动可控（只是抽接口 + 拆分 Codex 现有代码）
- 测试可分阶段（先把 Codex 跑通回归，再加 Claude Code）
- 用户侧立即受益（即使还没加新 agent，code 重构后维护成本也降低）

**第 2 步可以等加到第 3 个 agent 时再做** —— YAGNI 原则。但 YAML 配置文件可以**现在就定义好 schema**，让第 1 步就预留扩展点。

### 推荐执行顺序

1. **先画 `IAgentAdapter` 接口的完整定义** + Codex 怎么改造成第一个实现的具体 PR 清单（10-15 个文件的具体 diff 计划）
2. **先做第 2 步的 YAML schema 设计**（让第 1 步的接口能预留这个扩展点）
3. **直接动手做阶段 0 的 `AgentSource` 枚举扩展**（最小风险，先跑通流程）