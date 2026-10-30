# SkylinesLink · 天际互联

Connect AI tools to your city.

面向《城市：天际线 II》的独立 MCP 项目，目标是让支持 Model Context Protocol 的 AI 助手和智能体读取、理解并操作城市。

项目从头设计和开发。当前处于技术调研与规格设计阶段，尚未提供可用模组或 MCP 服务。

## 兼容目标

- 面向标准 MCP 客户端，不绑定特定模型供应商。
- Codex 与 DeepSeek Harness 是重点适配、验收对象。
- MCP 服务提供 stdio 和 Streamable HTTP；通过本机 HTTP 调用 C# 模组，每种客户端、协议版本和可选能力均需实际验证。
- 玩家订阅并启用模组即可获得完整连接能力，无需另装服务程序、Node.js、Python、.NET 或桥接插件。
- 首次在 AI 客户端添加 MCP 连接属于接入配置，提供尽可能简短的引导；不要求玩家编译、运行脚本或复制 DLL。
- 遵循游戏正常资金、解锁、原生校验及操作生命周期。
- 模块职责清楚、依赖单向，按可复现的空闲和负载测量验证低占用与性能。

## 当前资料

- [语言与架构调研](docs/research/2026-10-30-language-selection.md)
- [模组市场与同步发布调研](docs/research/2026-10-30-distribution.md)
- [开发工具与版本基线](docs/development/toolchain.md)
- [许可证、来源与品牌边界](docs/governance/licensing.md)
- [公开材料与脱敏规则](docs/governance/publication-privacy.md)
- [OpenSpec 项目基础提案](openspec/changes/establish-project-foundation/proposal.md)

架构已确定为 **C# 游戏模组 + TypeScript/Node.js MCP 服务**。游戏操作与协议服务分进程，Node 运行时和服务随同一个订阅包提供；stdio 模式由 AI 客户端启动，HTTP 模式由模组按需管理。采用官方 TS SDK，所有开发工具优先最新 LTS / 稳定版，并固定可复现版本；包管理以 pnpm 为主、npm 备用。架构决定已完成，随包分发、真实接入和性能仍待实现与验证。

## 发布与权益

计划通过 GitHub 发布源码及相同模组包，通过 Paradox Mods 提供订阅安装；两处共用版本号、源码提交和更新日志，并由一条可恢复的发布流程协调。普通玩家无需再去 GitHub 下载运行组件。项目自身内容采用 [Mozilla Public License 2.0](LICENSE)，第三方材料依各自许可。当前仅有项目资料与设计，尚无可用软件版本或自动发布流程。

Copyright © 2026 Vodkakakaka and SkylinesLink contributors. 品牌使用见[品牌说明](BRANDING.md)。

SkylinesLink 是独立社区项目，与 Paradox Interactive、Colossal Order、OpenAI 或 DeepSeek 没有官方隶属或背书关系。相关产品名称和商标属于其各自权利人。
