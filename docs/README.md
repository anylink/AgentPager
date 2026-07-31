# AgentPager 设计文档索引

本目录收录 AgentPager 的架构设计、扩展方案与开发指南。

## 文档清单

| 文档 | 用途 |
|------|------|
| [multi-agent-support.md](./multi-agent-support.md) | 多 Agent 支持方案：Claude Code / Codebuddy / OpenCode 如何接入 |
| [extensibility-refactor.md](./extensibility-refactor.md) | 架构可扩展性分析与重构方案（IAgentAdapter 抽接口） |
| [agent-adapter-guide.md](./agent-adapter-guide.md) | Agent Adapter 开发指南：给项目贡献新 Agent 的步骤详解 |

## 阅读顺序建议

如果你是 **新加入项目的开发者**，按以下顺序阅读：

1. [multi-agent-support.md](./multi-agent-support.md) — 了解项目要支持哪些 Agent，以及整体数据流
2. [extensibility-refactor.md](./extensibility-refactor.md) — 了解 `IAgentAdapter` 接口抽象的设计动机
3. [agent-adapter-guide.md](./agent-adapter-guide.md) — 实际动手加新 Agent 时参考

如果你是 **架构师 / 想了解长期演进**，重点读 [extensibility-refactor.md](./extensibility-refactor.md) 的"两步重构方案"章节。

## 当前重构状态

- ✅ 阶段 0：`AgentSource` 枚举扩展（三端同步）
- ✅ 阶段 1 核心：`IAgentAdapter` 接口 + `AgentRegistry` + `HookEnvelope` 接口骨架
- ⏳ 阶段 1 完整：把 Codex 代码迁移到 adapter 实现（下一个 PR）
- ⏳ 阶段 1.5：迁移 `HookTcpServer` 路由逻辑
- ⏳ 阶段 2（暂缓）：UI 资源数据驱动（`agents.yaml`）—— 按文档建议等加到第 3 个 agent 再做

详细进展见 git log。