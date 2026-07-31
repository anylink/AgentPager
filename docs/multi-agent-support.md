# AgentPager 多 Agent 支持方案

> 目标：在现有 Codex 集成基础上，支持 Claude Code、Codebuddy、OpenCode。

---

## 目录

1. [AgentPager 现状（Codex 专属）](#一agentpager-现状codex-专属)
2. [三个目标 Agent 的 Hook 机制调研](#二三个目标-agent-的-hook-机制调研)
3. [推荐改造方案](#三推荐改造方案)
4. [落地顺序与文件清单](#四落地顺序与文件清单)
5. [需要进一步确认的事项](#五需要进一步确认的-3-件事)

---

## 一、AgentPager 现状（Codex 专属）

### 数据流

```
Codex CLI / Codex Desktop
  │  (写入 ~/.codex/hooks.json 注册 command hook)
  ▼
HookTcpServer loopback:49361 (macOS: HookBridgeServer)
  │  CodexHookPayload (6 个事件)
  ▼
CodexEventReducer.Reduce()  →  AgentLifecycle / AgentActivity (统一模型)
  │
  │  + CodexRolloutObserver 监听 ~/.codex/sessions/rollout-*.jsonl
  │     (Token 用量、子代理、用户提示)
  │  + CodexUsageLoader 读取 5h / 周用量窗口
  │  + CodexSessionTitleReader 读取 session_index.jsonl
  ▼
TaskCatalog  →  TaskSnapshot (统一协议模型，AgentSource 枚举)
  │
  ▼
局域网 WebSocket  →  Android 手机
     (PairingPayload 配对，SignedControlEnvelope 控制)
```

### 关键文件分布

| 平台   | Hook 入口                                                                                          | 事件归约                                | 协议层           |
|--------|---------------------------------------------------------------------------------------------------|----------------------------------------|-----------------|
| Windows| `windows/src/AgentPager.Core/CodexHooks.cs` + `CodexRolloutObserver.cs` + `CodexUsageLoader.cs`   | `TaskCatalog.cs::CodexEventReducer`     | `WireModels.cs` |
| macOS  | `macos/Sources/AgentGridCore/CodexHooks.swift` + `CodexRolloutReader.swift` + `CodexUsageLoader.swift` | `CodexReducer.swift` / `TaskCatalog.swift` | `Domain.swift`  |
| Android| —                                                                                                  | —                                       | `Models.kt`     |

### 协议层已 Agent 无关的部分

- `AgentLifecycle` 枚举（`Offline / Idle / Starting / Running / WaitingApproval / WaitingAnswer / Succeeded / Interrupted`）
- `AgentActivity` 枚举（`Thinking / Reading / Searching / Editing / Executing / Testing / Browsing / Delegating`）
- `TaskSnapshot` / `PendingRequest` / `UsageSnapshot` 等数据结构

### 协议层仍 Agent 相关的部分

- `AgentSource` 枚举：当前只有 `CodexDesktop` / `CodexCLI`
- `CodexHookPayload` 类型：字段硬编码 Codex 协议
- `CodexEventReducer`：硬编码在 `TaskCatalog.cs` 内
- `CodexHookConfiguration` / `CodexHookInstaller` / `CodexRolloutObserver` / `CodexUsageLoader` / `CodexSessionTitleReader`：全部以 `Codex*` 命名直接堆在 core 里

---

## 二、三个目标 Agent 的 Hook 机制调研

| 维度             | Claude Code                                       | Codebuddy                                  | OpenCode                              |
|------------------|--------------------------------------------------|-------------------------------------------|---------------------------------------|
| 配置文件         | `~/.claude/settings.json`                         | `~/.codebuddy/settings.json`              | `~/.config/opencode/opencode.json`    |
| Hook 协议        | **标准 hook**（最成熟）                          | Claude Code 兼容                          | **Plugin 系统**（不是传统 hook）      |
| Hook 类型        | `command` **+ `http`**                           | command + HTTP PermissionRequest          | plugin (default export 一个 function) |
| 关键事件         | 16 个（见下表）                                  | 8 个（Claude Code 子集）                  | plugin 提供的回调                     |
| 审批机制         | HTTP hook 同步阻塞响应                           | HTTP hook 同步阻塞响应                    | plugin 拦截                          |
| 会话文件         | `~/.claude/sessions/<pid>.json` + `tasks/<uuid>/` | 类似 Claude Code                          | 待研究                               |
| 额度查询         | API rate limit headers                           | 类似 Claude Code                          | plugin 直接拿到                       |

### Claude Code 完整事件清单

```
Elicitation / Notification / PermissionRequest / PostCompact / PostToolUse /
PostToolUseFailure / PreCompact / PreToolUse / SessionEnd / SessionStart /
Stop / StopFailure / SubagentStart / SubagentStop / UserPromptSubmit / TeammateIdle
```

### Codebuddy 支持事件（v1.16+）

```
SessionStart / SessionEnd / UserPromptSubmit / PreToolUse / PostToolUse /
Stop / Notification / PreCompact + HTTP PermissionRequest
```

### OpenCode Plugin 关键约束

> "opencode's legacy plugin loader iterates Object.values() of THIS module's namespace and throws 'Plugin export is not a function' on any non-function export, silently killing the plugin."

plugin 模块**必须只有 default export 一个 function**，否则整个 plugin 加载失败。

### 关键发现

Claude Code 与 Codebuddy 都用 Claude Code 兼容格式，且都支持 **HTTP type hook**（`url: http://127.0.0.1:23333/permission`）做审批 —— 这意味着 AgentPager 可以**直接在它们已有的 `settings.json` 里加一个 HTTP hook 路由到自己的本地服务**，连"权限审批回调"这条路都不用单独开发，比 Codex 的 TCP+PermissionRequest 双通道还简单。

---

## 三、推荐改造方案

### 总体思路

> 抽出"Agent Adapter"接口，Codex 作为第一个实现。

新增一个抽象层，把"Agent 特定的 hook 配置 / 事件归约 / rollout 文件读取 / 额度查询"四块隔离开，上层 `TaskCatalog` 只认 `AgentLifecycle / AgentActivity / TaskSnapshot` 这些与 Agent 无关的统一协议。

### 阶段一：基础抽象（让多 Agent 接入成为可能）

#### 1. 扩展 `AgentSource` 枚举（三端同步）

```csharp
// WireModels.cs / Models.kt / Domain.swift
public enum AgentSource {
    CodexDesktop, CodexCLI,
    ClaudeCode,
    Codebuddy,
    OpenCode,
}
```

序列化时分别映射为 `codexDesktop / codexCLI / claudeCode / codebuddy / openCode`。

#### 2. 抽出通用接口（Windows 为例，macOS 同步）

```csharp
// 新文件 AgentPager.Core/AgentAdapter.cs
public interface IAgentAdapter {
    AgentSource Source { get; }
    string DisplayName { get; }      // "Codex" / "Claude Code" / "Codebuddy" / "OpenCode"
    AgentHookCapability HookCapability { get; }  // CommandHook | HttpHook | Plugin
    bool IsInstalled();
    HookInstallResult Install(string executablePath);
    void Uninstall();
    IEnumerable<CodexRolloutSignal> ScanRollout(DateTimeOffset since); // 改名 RolloutSignal
    UsageSnapshot? LoadUsage();
}
```

#### 3. 改造 Hook TCP Server

把 `HookTcpServer` 改成接收**带 source 字段**的事件，让不同 Agent 的 hook 都连到同一个 49361 端口：

```json
{ "source": "claudeCode", "hook_event_name": "PreToolUse", "session_id": "...", ... }
```

Codex 当前 payload 加 `"source": "codexCLI"` 即可。

#### 4. 把 `CodexEventReducer` 拆成两层

- `CommonReducer`：把 6 个 Codex 事件 + 16 个 Claude 事件 + 8 个 Codebuddy 事件 → `AgentLifecycle` / `AgentActivity`
- 每种 Agent 提供自己的 `IAgentEventReducer` 适配

事件映射示例（基于实测）：

| Agent 事件               | → AgentLifecycle                                |
|--------------------------|-------------------------------------------------|
| Claude `PreToolUse`      | → Running / Activity=对应 tool                  |
| Claude `PermissionRequest` | → WaitingApproval                              |
| Claude `SubagentStart/Stop` | → Subagent 状态                                |
| Claude `Elicitation`     | → WaitingAnswer（关键：Codex 没有，Claude 多了这个） |
| Claude `Stop / StopFailure` | → Succeeded / Interrupted                     |
| OpenCode `tool.execute.before/after` | → Running / Activity                          |

### 阶段二：三种 Agent 的具体实现

#### 2.1 Claude Code Adapter（最容易，参考 Clawd 已经做好的）

```csharp
public sealed class ClaudeCodeHookConfiguration {
    public string HooksPath => "~/.claude/settings.json";

    // Claude Code 16 个事件（注意 matcher 写法）
    private static readonly (string Name, string? Matcher, int Timeout)[] Events = [
        ("SessionStart", "startup|resume", 45),
        ("SessionEnd", null, 45),
        ("UserPromptSubmit", null, 45),
        ("PreToolUse", null, 45),
        ("PostToolUse", null, 45),
        ("PostToolUseFailure", null, 45),
        ("PermissionRequest", null, 3600),  // 这里我们用 command hook 发到 49361
        ("Notification", null, 45),
        ("PreCompact", null, 60),
        ("PostCompact", null, 60),
        ("Stop", null, 45),
        ("StopFailure", null, 45),
        ("SubagentStart", null, 45),
        ("SubagentStop", null, 45),
        ("Elicitation", null, 3600),        // Codebuddy 没有这个
        ("TeammateIdle", null, 45),
    ];

    // 安装时：合并到 hooks 字段、保留用户原有 hook
}
```

**关键风险点**：

- Claude Code 用户 settings 里往往已经有别的 hook（实测本机就有 Clawd 的命令 hook + 自己的 HTTP hook），**不能简单覆盖，必须做"合并而不是替换"**。这正是当前 `CodexHookInstaller.isManaged()` 已经实现的模式（通过 `statusMessage: "Managed by AgentPager"` 标记），照搬即可。
- 子代理事件 `SubagentStart / SubagentStop` 是 Codex 没有的，正好可以补全 `TaskSnapshot.Subagents` 数据。
- `Elicitation` 事件对应 `WaitingAnswer`，可以补全 README 里"暂不能在手机上回答问题"的功能。

#### 2.2 Codebuddy Adapter（几乎直接复用 Claude Code）

```csharp
// Codebuddy 支持的 8 个事件（v1.16+）
private static readonly (string Name, string? Matcher, int Timeout)[] Events = [
    ("SessionStart", null, 45),
    ("SessionEnd", null, 45),
    ("UserPromptSubmit", null, 45),
    ("PreToolUse", null, 45),
    ("PostToolUse", null, 45),
    ("Stop", null, 45),
    ("Notification", null, 45),
    ("PreCompact", null, 60),
    // PermissionRequest 走 HTTP hook（不同机制）
];
```

格式与 Claude Code 一致，写入 `~/.codebuddy/settings.json`。

**PermissionRequest 特殊处理**：Codebuddy 的 PermissionRequest 是独立配置的 HTTP hook。我们有两个选择：

- (A) 仍然用 command hook 发到 49361，**和 Claude Code 保持一致**（推荐，代码复用）
- (B) 注册一个 HTTP hook URL，AgentPager Bridge 暴露 `/permission` 端点

#### 2.3 OpenCode Adapter（完全不同的路线：plugin）

OpenCode 用的是 **plugin 系统**而不是 command hook。参考 Clawd 的实现：

```javascript
// ~/.config/opencode/opencode.json
{
  "plugin": [
    "C:/path/to/agentpager-plugin/index.mjs"
  ]
}
```

我们需要：

1. 写一个 OpenCode plugin（TypeScript / JavaScript），提供默认导出的 `async function({ app, $ })`
2. plugin 内部通过 plugin SDK 监听 `tool.execute.before / after`、`session.idle`、`session.error` 等事件
3. 通过 HTTP POST 把事件推到 AgentPager Bridge 的 `localhost:49361/hook`
4. 安装时把 plugin 路径注入 `~/.config/opencode/opencode.json` 的 `plugin` 数组

**注意 Clawd 注释里的坑**：plugin 模块**必须只有 default export 一个 function**，否则整个 plugin 加载失败。

### 阶段三：rollout / 用量 / 子代理差异化

| 能力            | Codex                                              | Claude Code                          | Codebuddy             | OpenCode             |
|-----------------|----------------------------------------------------|--------------------------------------|-----------------------|----------------------|
| Token 用量      | `CodexUsageLoader` 读 sessions/rollout             | 子代理 + API response headers        | 类似 Claude Code      | plugin 直接拿到       |
| 用量窗口        | Codex 5h / 周                                       | API rate limit headers               | 类似                  | plugin 拿到          |
| 子代理          | rollout JSONL `agent_message`                      | `SubagentStart / Stop` hook          | 类似 Claude Code      | `session.compacted` 等 |
| 项目名          | cwd basename                                       | cwd basename                         | cwd basename          | 需要研究             |

每种 Agent 实现自己的 `IRolloutObserver` / `IUsageLoader`，上层 `TaskCatalog` 不感知。

### 阶段四：手机端 UI 适配

`android/.../render/PixelCoreSurfaceView.kt` 已经有"按 source 渲染像素动画"的钩子。最小改动：

- `AgentSource` 枚举增加 3 个值
- 给每个 source 加一个 8-bit 调色板（Codex 用蓝绿，Claude Code 用橙色，Codebuddy 用紫色，OpenCode 用青色……）
- `TaskRowSummary` 在卡片角标显示对应 Agent 缩写字母

---

## 四、落地顺序与文件清单

```
阶段 0: AgentSource 枚举扩展（三端同步，1 PR）
  ↓
阶段 1: 抽出 IAgentAdapter 接口 + 把 Codex 改造成第一个实现
       （代码重构，不引入新功能，回归测试）
  ↓
阶段 2: 实现 ClaudeCodeAdapter（最高价值，hook 系统最标准）
       —— 可借力 Clawd 的 claude-hook.js 作为对照实现
  ↓
阶段 3: 实现 CodebuddyAdapter（复用 Claude Code 90% 代码）
  ↓
阶段 4: 实现 OpenCodeAdapter（plugin 系统，需要写 JS/TS）
  ↓
阶段 5: UI 层适配 + 调试 Elicitation / SubagentStart 等新事件
```

每个阶段结束时，手机端都**无需重新安装 APK**（只要 `AgentSource` 枚举扩展在阶段 0 完成，后续都是 Bridge 侧后端工作 + 协议层扩展）。

---

## 五、需要进一步确认的 3 件事

1. **OpenCode plugin SDK 的具体事件清单** —— 没有完整文档，需要看 OpenCode 官方 plugin 文档或反编译 `~/.config/opencode` 看 Clawd 用了哪些回调
2. **Claude Code 子代理事件 `SubagentStart / SubagentStop` 的 payload 结构**（`session_id` 是父任务还是子任务？）—— 需要实测或读 Claude Code 文档
3. **Codebuddy 的会话文件路径** —— 与 Claude Code 完全一致还是略有差异？