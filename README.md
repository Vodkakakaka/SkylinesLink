# SkylinesLink · 天际互联

Connect AI tools to your city.

面向《城市：天际线 II》的独立 MCP 项目，目标是让支持 Model Context Protocol 的 AI 助手和智能体读取、理解并操作城市。

项目从头设计和开发。当前处于技术调研与规格设计阶段，尚未提供可用模组或 MCP 服务。

## 兼容目标

- 面向标准 MCP 客户端，不绑定特定模型供应商。
- Codex 与 DeepSeek Harness 是重点适配、验收对象。
- 默认通过模组内置的 Streamable HTTP 服务接入；每种客户端、协议版本和可选能力均需实际验证。
- 玩家订阅并启用模组即可获得完整连接能力，无需另装服务程序、Node.js、Python、.NET 或桥接插件。
- 首次在 AI 客户端添加 MCP 连接属于接入配置，提供尽可能简短的引导；不要求玩家编译、运行脚本或复制 DLL。
- 遵循游戏正常资金、解锁、原生校验及操作生命周期。
- 模块职责清楚、依赖单向，按可复现的空闲和负载测量验证低占用与性能。

## 当前资料

- [语言与架构调研](docs/research/2026-10-30-language-selection.md)
- [模组市场与同步发布调研](docs/research/2026-10-30-distribution.md)
- [许可证、来源与品牌边界](docs/governance/licensing.md)
- [公开材料与脱敏规则](docs/governance/publication-privacy.md)
- [OpenSpec 项目基础提案](openspec/changes/establish-project-foundation/proposal.md)

目前推荐以 C# 实现游戏模组及内置 MCP 服务，沿用游戏官方运行时；有自定义界面需求时采用构建后随模组分发的 TypeScript 界面。此为调研建议，SDK 在游戏中的兼容性及无额外安装体验尚未通过原型验收。

## 发布与权益

计划通过 GitHub 发布源码及相同模组包，通过 Paradox Mods 提供订阅安装；两处共用版本号、源码提交和更新日志，并由一条可恢复的发布流程协调。普通玩家无需再去 GitHub 下载运行组件。项目自身内容采用 [Mozilla Public License 2.0](LICENSE)，第三方材料依各自许可。当前仅有项目资料与设计，尚无可用软件版本或自动发布流程。

Copyright © 2026 Vodkakakaka and SkylinesLink contributors. 品牌使用见[品牌说明](BRANDING.md)。

SkylinesLink 是独立社区项目，与 Paradox Interactive、Colossal Order、OpenAI 或 DeepSeek 没有官方隶属或背书关系。相关产品名称和商标属于其各自权利人。
