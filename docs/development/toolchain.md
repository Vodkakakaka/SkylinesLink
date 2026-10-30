# 开发工具与版本基线

核实日期：2026-10-30。以下是本项目选定的起始基线；不是所有本机工具已完成升级或所有依赖已经安装的声明。架构决定见 [OpenSpec design](../../openspec/changes/establish-project-foundation/design.md)。

## 版本原则

- 运行时优先最新的 LTS 系列及该系列最新稳定补丁；没有 LTS 分支的语言、SDK、包管理器与构建工具采用最新稳定版。
- 不使用 alpha、beta、RC、nightly 或不固定的 `latest` 作为构建输入。查询最新后写入精确版本和锁文件，更新时重新核查来源及兼容性。
- 官方游戏工具链、目标运行时及其配套库共同构成兼容边界；不得单独升级 Unity、ECS、Burst 或游戏程序集。记录该边界，而不是把游戏旧目标框架误当作开发机缺少新 SDK。
- 使用当前稳定工具实现已确认功能；没有实际需求就不引入框架。新增依赖仍须核对最新稳定版本、许可证和传递依赖。
- 包管理统一使用 **pnpm**；npm 仅作备用工具。项目安装、脚本和 CI 文档使用 pnpm，以 `pnpm-lock.yaml` 为唯一 JavaScript 依赖锁文件，不混用 npm 安装或提交 `package-lock.json`。

## 起始基线

| 用途 | 选定版本 | 权威依据 |
|---|---|---|
| MCP 运行时 | Node.js **26.13.0 LTS (Lithium)** | [官方发行索引](https://nodejs.org/dist/index.json)、[LTS 状态](https://nodejs.org/en/about/previous-releases) |
| MCP 开发语言 | TypeScript **7.0.2**，启用严格检查 | [官方包元数据](https://registry.npmjs.org/typescript/7.0.2) |
| 包管理 | pnpm **12.16.1** | [官方包元数据](https://registry.npmjs.org/pnpm/12.16.1) |
| MCP 服务 SDK | `@modelcontextprotocol/server` **2.4.0** | [包元数据](https://registry.npmjs.org/@modelcontextprotocol/server/2.4.0) |
| MCP HTTP 适配 | `@modelcontextprotocol/node` **2.1.2** | [包元数据](https://registry.npmjs.org/@modelcontextprotocol/node/2.1.2)，声明兼容 server `^2.4.0` |
| 协议测试客户端 | `@modelcontextprotocol/client` **2.4.0** | [包元数据](https://registry.npmjs.org/@modelcontextprotocol/client/2.4.0) |
| Node 类型 | `@types/node` **26.6.6** | [包元数据](https://registry.npmjs.org/@types/node/26.6.6) |
| 输入 schema | Zod **4.6.7** | [包元数据](https://registry.npmjs.org/zod/4.6.7) |
| 服务代码打包 | esbuild **0.28.2** | [包元数据](https://registry.npmjs.org/esbuild/0.28.2) |
| 随包运行环境 | 所选版本的官方可移植 Node 运行时 + 构建后的 JS bundle | 从[官方发行目录](https://nodejs.org/dist/v26.13.0/)获取并核对校验值，玩家无需安装 |
| C# 开发 SDK | .NET SDK **10.0.403**，对应运行时 **10.0.14**，LTS | [官方 .NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)、[发行元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) |
| 版本控制 | Git for Windows **2.56.0.windows.4** | [官方稳定发行](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.4) |
| 规格工具 | OpenSpec **1.15.0** | [官方包元数据](https://registry.npmjs.org/@fission-ai/openspec/1.15.0) |

测试优先使用所选 Node 自带的测试能力以及实际需要的 C# 测试工具。尚未引入的测试、检查或界面依赖，在首次采用时核对稳定版本，不把调研过的所有工具都装入项目。

官方 [SEA 单可执行机制](https://nodejs.org/api/single-executable-applications.html)仍标注 Active development，不作为首发打包前提。一个订阅包可以包含多个运行文件，免额外安装不要求所有代码合成一个二进制。

## 游戏侧的明确约束

当前随游戏提供的 `Mod.props` 设置 `TargetFramework=net48`、`LangVersion=9.0`。开发使用最新稳定 .NET SDK，但发行模组先保持官方支持的目标和语言级别。SDK 自带 C# 14 编译能力不等于游戏与其后处理器支持全部 C# 14 特性；若未来官方工具链提高支持范围，再同步采用。[Microsoft 说明](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version)也要求考虑语言与运行时兼容，并避免浮动 `LangVersion=latest`。

这是游戏插件边界的兼容要求，不影响 TypeScript/Node 层采用最新 LTS / 稳定版。官方 ModPublisher 的目标运行时同样由游戏工具链管理，不能私自重编译或修改它来追版本号。

## 实施与更新

初始化代码时写入固定 Node / pnpm / .NET SDK 配置与依赖锁文件，并在构建中核对；在更新前核查官方最新版本及支持状态，验证后更新基线。构建和公开发行记录实际使用版本，不能只引用这张表。

当前开发机已经通过既有 nvm 安装并选用 Node 26.13.0 LTS，官方可执行文件校验值一致；pnpm 已更新为 12.16.1，正常入口与新 PowerShell 进程均已验证。.NET SDK 与 OpenSpec 已符合基线，现有 PATH 层级与稳定入口保持不变。Git 尚未更新至表中基线，应在开发初始化时通过既有管理方式处理。上述是开发机状态，不代表项目依赖或构建已经完成。

TypeScript 不作全局安装。代码初始化时通过 pnpm 将其作为固定版本的项目 `devDependency`，使用项目脚本执行类型检查和构建。Node 对部分 TypeScript 语法的运行支持不能代替类型检查，也不是发行源码给玩家的理由。

发行包携带所需 Node 运行时和适用许可证，不携带 npm/pnpm 开发环境；玩家无需联网安装依赖。Node 安全更新随模组发行维护，不能把补丁责任转交给玩家。
