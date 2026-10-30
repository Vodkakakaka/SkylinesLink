# Proposal

## Why

SkylinesLink（天际互联）需要从独立需求建立可维护、可发布的《城市：天际线 II》MCP 系统。首先明确游戏运行时、跨客户端兼容和发行来源边界，避免语言选择或接口连通被误当作完整可用性。

## What Changes

- 确定 C# 游戏模组与 TypeScript/Node.js MCP Host 的双组件架构，使用官方 TS SDK，通过受控本机 HTTP 调用游戏能力。
- 订阅并启用模组后提供完整运行能力，不要求玩家另装 Host、运行时、桥接程序或开发工具；简化首次客户端连接配置。
- 将工具契约、长操作状态、协议版本与客户端可选能力列为明确验收对象。
- Host 同时提供 stdio 和 Streamable HTTP；Node 运行时随包携带，进程按模式由客户端或模组管理，更新后连接入口保持稳定。
- 以 Codex、DeepSeek Harness 为重点实测客户端，对其他支持 MCP 的工具保留标准接入路径。
- 建立原创内容、第三方依赖、品牌与发行材料的来源规则；采用维护者已选择的 MPL-2.0。
- 所有上传材料均须脱敏，覆盖源码、提交历史与元数据、日志、截图及发行附件。
- 以 Paradox Mods 为官方订阅渠道，GitHub 发布源码与相同版本模组包；统一版本、提交、更新日志及可恢复发布流程。
- 以清晰模块、单向依赖、有界资源和可复现性能测量约束架构，兼顾扩展与低占用，不以复杂框架或版本号代替质量。
- 采用最新 LTS / 稳定工具和依赖，固定可复现版本，明确官方游戏工具链的兼容边界。
- 本阶段只完成调研与方案，不实现城市建设功能、不操作游戏、不发布模组。

## Capabilities

### New Capabilities

- `game-runtime-boundary`: 游戏身份、线程与服务生命周期、原生执行及可查询的操作状态。
- `mcp-interoperability`: 无额外安装的标准接入、稳定工具契约、可选能力和客户端兼容证据。
- `release-provenance`: 可追溯制品、双渠道同版发布、合法分发材料、脱敏与准确声明。

### Modified Capabilities

无；本项目尚无既有规格。

## Impact

影响未来 C# Mod、TS/Node Host、跨语言契约、连接引导、发行包和同步发布流程；当前没有产品源码。语言与组件分工已经确定，见[选型记录](../../../docs/research/2026-10-30-language-selection.md)；具体[工具版本](../../../docs/development/toolchain.md)、[分发依据](../../../docs/research/2026-10-30-distribution.md)一并维护。架构确定不等于平台分发、客户端接入或性能已经通过验证。
