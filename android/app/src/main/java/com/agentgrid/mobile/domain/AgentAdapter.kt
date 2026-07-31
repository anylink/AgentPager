package com.agentgrid.mobile.domain

import kotlinx.serialization.json.JsonElement

/** Agent Hook 传输方式的分类。不同 Agent 的 Hook 协议差异较大。 */
enum class HookTransportKind {
    /** 通过 CLI 子进程 stdin/stdout 通信（Codex / Claude Code / Codebuddy） */
    COMMAND_HOOK,

    /** 通过本地 HTTP 端点同步阻塞响应（Codebuddy PermissionRequest） */
    HTTP_HOOK,

    /** 通过宿主进程的插件机制（OpenCode plugin SDK） */
    PLUGIN,
}

/** Agent 静态描述信息（品牌、UI 资源）。手机端用它渲染任务卡片右上角的来源标识。 */
data class AgentDescriptor(
    val source: AgentSource,
    val displayName: String,
    val shortName: String,
    /** 0xAARRGGBB */
    val brandColorArgb: Long,
    val iconKey: String,
)

/** Hook 安装结果。 */
data class HookInstallResult(
    val changed: Boolean,
    val backupPath: String? = null,
    val error: String? = null,
) {
    val success: Boolean get() = error == null

    companion object {
        fun noChange() = HookInstallResult(changed = false)
        fun updated(backup: String?) = HookInstallResult(changed = true, backupPath = backup)
        fun failure(error: String) = HookInstallResult(changed = false, error = error)
    }
}

/**
 * 通用 Hook 入站信封。屏蔽不同 Agent 的 payload 字段差异。
 *
 * 注意：这是【阶段 1 引入】的通用类型。当前手机端只读取由 Bridge 推送的
 * StateSnapshotPayload，本类型主要用于未来 Bridge → Phone 协议扩展。
 */
data class HookEnvelope(
    val source: AgentSource,
    val hookEventName: String,
    val sessionId: String,
    val cwd: String,
    val raw: JsonElement,
    val toolName: String? = null,
    val toolUseId: String? = null,
    val prompt: String? = null,
    val transcriptPath: String? = null,
    val model: String? = null,
)

/**
 * Agent Adapter 接口。每种 Agent 的"Hook 配置 / 事件归约 / rollout 文件读取 /
 * 额度查询 / 标题读取"都被封装在这里。
 *
 * 手机端目前主要消费 `AgentDescriptor`，用于在任务卡片上显示 agent 品牌。
 * 后续接入 Claude Code / Codebuddy / OpenCode 时，Bridge 端会实现这个接口，
 * 手机端按 `AgentSource` 渲染对应的 UI 资源。
 */
interface AgentAdapter {
    val source: AgentSource
    val descriptor: AgentDescriptor
    val transport: HookTransportKind

    fun isInstalled(): Boolean
    fun install(bridgeExecutablePath: String): HookInstallResult
    fun uninstall(): HookInstallResult
}

/**
 * 手机端的 Agent 描述符注册表。当前只描述"如何显示"，不直接执行 hook 安装
 * （安装发生在 Bridge 端）。
 */
object AgentRegistry {
    private val descriptors: MutableMap<AgentSource, AgentDescriptor> = mutableMapOf()

    fun register(descriptor: AgentDescriptor) {
        descriptors[descriptor.source] = descriptor
    }

    fun get(source: AgentSource): AgentDescriptor? = descriptors[source]

    fun all(): List<AgentDescriptor> = AgentSource.entries.filter { descriptors.containsKey(it) }
        .mapNotNull { descriptors[it] }
}