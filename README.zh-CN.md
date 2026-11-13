[English](README.md) | [简体中文](README.zh-CN.md)

# SkylinesLink · 天际互联

让 AI 工具连接你的城市。

SkylinesLink 是面向**《城市：天际线 II》**的独立 Model Context Protocol（MCP）项目，目标是让 AI 助手和智能体读取、理解并操作城市。项目从头设计和开发。

核心是把有用的游戏信息、正常游玩操作、规划与可核结果链接成统一框架。公共AI指引将随产品按任务提供，玩家偏好来自当前任务，不写进默认规则。这些是产品交付目标，不代表相应能力已经实现。

**开发者预览版：[GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases)。** 源码下载附准确提交清单、校验值及验证摘要。源码结构、构建命令和已知限制见[预览版说明](docs/development/preview.md)。目前尚无经过游戏验收的玩家安装发行版，源码归档是贡献者下载内容，不是玩家安装包。

![SkylinesLink 原创城市互联概念插画，非游戏截图](assets/branding/city-link-concept.png)

*原创概念插画。[标志素材与来源](assets/branding/README.md)；图片不代表游戏实机或已实现界面。*

## 当前状态

项目处于骨架开发与关键可行性验证阶段，已有模块化代码结构、有界连接、持久操作与历史查询实验，以及桌面入口。当前源码声明十个工具：`skylines_context`、`skylines_operation_get`、`skylines_guidance_search`、`skylines_guidance_get`、`skylines_capability_describe`、`skylines_task_stop`、`skylines_task_stop_decision_get`、`skylines_module_runtime_get`、`skylines_city_overview` 和 `skylines_building_inspect`。

模组观察接口采集加载器事实，后续分页读取同一份不可变观察。原生适配器使用已配置的普通模组连接；未配置采集源的运行时明确返回不可用。它不等于全部安装或订阅清单，也不证明已启用、功能健康或已经适配；完整覆盖与帧成本仍待验收。

建筑查询通过当前加载作用域中的准确引用，或原生信息面板已发布的选择，读取一座建筑的姿态、预制件元数据、资产局部包围盒，以及附属物、子网和已装升级的直接引用前缀。缺失字段与未完整返回的关系明确标记，不改变玩家选择或活动工具。基础成本、已存道路引用和局部包围盒不等于建造资格、可达证明或完整世界占地。原生选择源须在当前作用域发布过变化，旧缓存不能直接返回；实现、离线检查与实机资格分别记录。该读取链已在有界原生新城夹具中，经普通模组连接和两种受测MCP协议完成准确引用及正常发布的面板选择读取；通用资产覆盖、普通权限和性能仍待验收。

城市概览读取当前已加载城市的原生人口和玩家资金组件，附加载作用域、采样帧、单位、缺失原因及无限资金标志。来源更新时间仍未知，它不等于完整城市诊断。已通过普通模组连接、双 MCP 协议的有界新城加载／读取／返回循环；完整城市覆盖、玩家并行操作和性能仍待验收。

Task停止请求使用Control权限；完整决定读取使用Read权限，显式选择live或history。生产Task创建与执行尚未启用，不支持Task的运行时明确返回unavailable。停止受理表示请求停止后续工作，不证明所有原生工作已经结束；它不会停止AI客户端或撤销已经提交的游戏动作。

三个公共数据工具无需连接游戏，即可读取随包的英语/简体中文指引和精确输入输出契约。可先用 `{}` 调用 `skylines_guidance_search`，再用返回的指南 ID 调用 `skylines_guidance_get`；传入 `language: "zh-CN"` 选择简体中文。它们说明本构建，不代表当前城市事实或动作执行许可。施工工具与可供玩家使用的完整安装仍在开发，这些改动也不会自动升级旧 Core 候选。

离线检查覆盖 Launcher、随包 Node.js/Host 和独立 C# 运行时探针。Codex 与 DeepSeek Harness 的隔离检查为具体受测客户端及制品组合提供证据，不代表所有客户端、当前源码修订、真实模型决策或实际游戏均已适配。当前实现与限制见[架构说明](docs/architecture.md)。

游戏运行时完整覆盖、玩家与 AI 并行操作、发行渠道及完整性能验收仍未完成。开发构建或本地 Core 候选不等于可供玩家使用的发行版。

产品目标是提供有用的游戏信息和可靠的正常游玩操作，覆盖复杂任务。以完整城市任务的完成、返工、调用成本和实际资源开销检验；计划中的功能与模组集成是交付责任，不代表已经完成。

## 架构与产品目标

架构为 **C# 游戏模组 + TypeScript/Node.js MCP Host**，分别运行在独立进程中。Host 使用官方 TypeScript MCP SDK，通过认证的 Windows 命名管道调用 C# 运行时。稳定 Launcher 是对外的 stdio 入口。Streamable HTTP 已有实现及离线检查，完整的玩家启动与生命周期接入仍在开发中。

预期的玩家体验是：

- 支持标准 MCP，不绑定单一模型供应商，优先适配并验收 Codex 和 DeepSeek Harness。每种受支持客户端、协议版本和可选能力都需要各自的验证证据。
- 完整包内提供所需运行时和 Host。订阅并启用模组后，无需另装 Node.js、Python、.NET、服务程序或桥接插件；首次在 AI 客户端注册 MCP 连接仍需简短的配置流程。
- 从 GitHub 或 Paradox Mods 安装，得到相同 Core、配置/数据和稳定连接入口；更新与渠道切换相互协调，避免重复加载。这些分发和安装流程尚未通过玩家验收。
- 遵循游戏正常资金、解锁、原生校验及操作生命周期。
- 玩家与 AI 可以并行操作，AI 不抢占玩家的鼠标、键盘、窗口焦点、当前工具或选择状态；检测并重新核验相互冲突的城市变更。这是设计要求，尚未在实际游戏中证明隔离成立。
- 模块职责清楚、依赖单向，通过可复现的空闲和负载测量验证性能与内存占用。

## 开发入口

使用 Windows 及达到[工具链说明](docs/development/toolchain.md)最低版本的稳定 Node.js、pnpm 和 .NET SDK。允许更高稳定版本的开发工具，源码依赖仍锁定。TypeScript 是项目依赖，不需全局安装。包管理以 pnpm 为主、npm 备用。

```powershell
pnpm install --frozen-lockfile
pnpm build
pnpm test
```

以上命令构建并检查不依赖游戏的骨架部分。`pnpm build:mod` 另需官方游戏工具链，NativeAOT 桌面发布另有原生构建前提。普通构建不会部署模组或发布发行版。这些是贡献者开发命令，不是玩家安装步骤。

## 项目资料

- [架构与组件职责](docs/architecture.md)
- [开发工具与版本基线](docs/development/toolchain.md)
- [许可与品牌边界](docs/governance/licensing.md)
- [公开材料与脱敏](docs/governance/publication-privacy.md)
- [原创标志与概念视觉](assets/branding/README.md)

## 发布与权益

初版功能与验收完成后，玩家下载将通过本仓库的 [GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases) 发布，并计划提供 Paradox Mods 订阅。两个渠道共用同一 Core、版本号、源码提交和发行说明，由可恢复的发布流程协调。完整随包交付是基线；安装器与游戏设置页条件方案见[架构说明](docs/architecture.md)。

项目自身内容采用 [Mozilla Public License 2.0](LICENSE)，第三方材料依各自许可。

Copyright © 2026 Vodka and SkylinesLink contributors. 品牌使用见[品牌说明](BRANDING.md)。

SkylinesLink 是独立社区项目，与 Paradox Interactive、Colossal Order、OpenAI 或 DeepSeek 没有官方隶属或背书关系。相关产品名称和商标属于其各自权利人。
