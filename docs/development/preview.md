# Developer preview / 开发者预览版

[English](#english) | [简体中文](#简体中文)

## English

This preview publishes the engineering foundation of SkylinesLink. It is intended for contributors reviewing the structure, building the source and running isolated tests. It is not a player installation package. Do not copy development probe output into a normal game profile.

### What the source contains

| Component | Responsibility |
| --- | --- |
| `contracts/` | Shared schema, generated C#/TypeScript contracts and wire values |
| `src/game/SkylinesLink.Application/` | Context, bounded observations, operation rules and consumer ports |
| `src/game/SkylinesLink.Infrastructure/` | Persistence, authenticated local communication and lifetime ownership |
| `src/game/SkylinesLink.Mod/` | Official game entry, native adapters, connection settings and development probes |
| `src/host/` | Official MCP SDK integration, stdio/HTTP transports, routing and bilingual guidance |
| `src/desktop/` | Launcher, Setup and shared deployment logic |
| `tests/` | Contract, runtime, protocol, history and desktop-process checks |
| `tools/` | Contract generation, dependency checks, builds and source packaging |

The declared MCP surface has nine tools. Three Host-local tools discover/read public guidance and describe capability schemas. The other tools cover context, operation lookup, Task Stop/decision lookup, module observations and city overview. Declarations and offline tests do not establish game readiness. Task creation and production construction tools are not enabled.

The operation and history components exercise identity, durable intent, bounded work, recovery and truthful unknown outcomes. The native construction probes are separate development experiments behind an explicit compile symbol; they are not public building tools or a general construction engine.

### Native experiment update

The latest development source records optional generated members, bounded typed rejection details, and value-based checks of a cancelled preview against the candidate rebuilt before payment. A limited building experiment on game 1.6.4 completed normal preview/cancel/build, relocation, and a different-prefab build at the old site. The runtime-qualified internal source and the later wording-only revision are recorded separately; subsequent source changes are not automatically game-qualified.

These checks do not establish a public construction API, general asset support, dynamic player/AI coexistence or actual service operation. A separate menu-only exit control without the product assemblies also ended with a native access violation. This narrows the observed preconditions; it does not identify the cause or prove that the process-local background policy is unrelated.

### Build and verify

Use Windows, PowerShell 7 and stable tools meeting the minimum versions in the [toolchain guide](toolchain.md). VS Code is sufficient as an editor. From a checkout:

```powershell
pnpm install --frozen-lockfile
dotnet restore SkylinesLink.slnx --locked-mode
pnpm build
pnpm test
pnpm test:core
```

These checks do not require starting the game. `pnpm test` explicitly skips the NativeAOT real-process installation case in its ordinary managed test form. Official Mod compilation additionally requires a legitimate game installation and its configured toolchain. NativeAOT publishing has separate native build prerequisites. Neither is supplied or installed by these commands.

The [Foundation workflow](../../.github/workflows/foundation.yml) runs the game-independent checks on Windows. Its run belongs to the exact commit shown by GitHub; a successful local run is not a remote CI result. [Architecture](../architecture.md) describes component boundaries and the current implementation.

### Downloads and provenance

The [GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases) developer prerelease contains an explicitly named source ZIP, `source-manifest.json`, `SHA256SUMS.txt` and a validation summary. The manifest identifies the exact public commit, Git tree, file count, ZIP size and SHA-256. The validation summary distinguishes executed checks, skipped checks and unverified game/player flows.

To verify a download on Windows:

```powershell
Get-FileHash -LiteralPath ./SkylinesLink-0.1.0-dev.0-source.zip -Algorithm SHA256
```

Compare the result with `SHA256SUMS.txt`. `pnpm build:source-preview` packages a clean public checkout after checking every tracked path and each archived Git blob. Existing output is refused. It does not filter private files out of an internal tree, compile binaries, upload a release or claim game validation. The source ZIP is not the shared player Core planned for GitHub and Paradox Mods.

### Open work

Player installation and updates, the game UI, full city information and actions, player/AI coexistence, complete mod adaptations and performance acceptance remain in development. Native construction experiments still have unsupported asset branches. Development game sessions have also shown an unresolved native access violation on exit; returning to the menu or releasing the MCP root does not prove a normal process exit. This preview does not claim that defect is fixed or that this source revision has passed player qualification. No Paradox Mods player release accompanies this source preview.

## 简体中文

本预览版公开 SkylinesLink 的工程骨架，供贡献者阅读结构、构建源码和运行隔离测试。它不是玩家安装包，请勿将开发探针产物复制到日常游戏配置中。

### 源码包含什么

| 组件 | 职责 |
| --- | --- |
| `contracts/` | 共享 schema、派生的 C#/TypeScript 契约和通信值 |
| `src/game/SkylinesLink.Application/` | 上下文、有界观察、操作规则及消费端口 |
| `src/game/SkylinesLink.Infrastructure/` | 持久化、认证本地通信及资源寿命管理 |
| `src/game/SkylinesLink.Mod/` | 官方游戏入口、原生适配、连接设置及开发探针 |
| `src/host/` | 官方 MCP SDK、stdio/HTTP 传输、路由及双语指南 |
| `src/desktop/` | Launcher、Setup 及共用部署逻辑 |
| `tests/` | 契约、运行时、协议、历史和桌面进程检查 |
| `tools/` | 契约生成、依赖检查、构建及源码打包 |

当前 MCP 声明九个工具。其中三个 Host 本地工具检索和读取公共指南、描述能力 schema；其余工具提供上下文、操作查询、Task 停止及决定查询、模组观察和城市概览。声明和离线检查不代表游戏可用性验收。生产 Task 创建和施工工具尚未启用。

操作与历史组件覆盖身份、持久意图、有界工作、恢复及真实未知结果。原生施工探针是由显式编译符号隔离的开发实验，不是公开建筑工具或通用施工引擎。

### 原生实验更新

最新开发源码增加实际可选生成成员、有界的类型化拒绝诊断，以及取消预览与付款前重建候选的逐值比对。游戏1.6.4的有限建筑实验完成了正常预览／取消／建造、迁移和原址不同资产新建。实机合格的内部源码与后续仅说明文字的修订分别记录，不能将源码更新自动视为重新通过实机。

这些结果不代表公开施工接口、通用资产支持、动态玩家并行或实际服务运营已完成。另一个不加载产品程序集的纯菜单退出对照也发生原生访问冲突；这缩小了已观察前提，尚未定位根因，也未证明本进程后台策略与崩溃无关。

### 构建与验证

使用 Windows、PowerShell 7，以及达到[工具链说明](toolchain.md)最低版本的稳定工具。编辑器可以使用 VS Code。在源码根目录执行：

```powershell
pnpm install --frozen-lockfile
dotnet restore SkylinesLink.slnx --locked-mode
pnpm build
pnpm test
pnpm test:core
```

上述检查不启动游戏。普通托管测试形态下，`pnpm test` 会明确跳过 NativeAOT 实进程安装用例。官方 Mod 编译另需合法游戏及已配置的配套工具链，NativeAOT 发布另需原生构建前提；上述命令不会提供或安装这些工具。

[Foundation 工作流](../../.github/workflows/foundation.yml) 在 Windows 执行游戏无关检查，运行结果属于 GitHub 显示的准确提交；本地通过不等于远端 CI 通过。组件边界和当前实现见[架构说明](../architecture.md)。

### 下载与来源

[GitHub Releases](https://github.com/Vodkakakaka/SkylinesLink/releases) 开发预览条目包含单独命名的源码 ZIP、`source-manifest.json`、`SHA256SUMS.txt` 和验证摘要。清单记录准确公开提交、Git 树、文件数、ZIP 大小及 SHA-256；验证摘要区分已执行、已跳过和仍未验证的游戏与玩家链路。

Windows 下载后可运行：

```powershell
Get-FileHash -LiteralPath ./SkylinesLink-0.1.0-dev.0-source.zip -Algorithm SHA256
```

将结果与 `SHA256SUMS.txt` 对照。`pnpm build:source-preview` 只打包干净的公开源码树，检查全部跟踪路径与归档内每个 Git blob，拒绝覆盖既有输出。它不会从内部树靠排除文件生成公开包、编译二进制、上传或宣称游戏验收。此源码 ZIP 不是未来 GitHub 与 Paradox Mods 共用的玩家 Core。

### 尚未完成

玩家安装与更新、游戏界面、完整城市信息与操作、玩家和 AI 并行、完整模组适配及性能验收仍在开发。原生施工实验仍有不支持的资产分支。开发游戏会话还曾在退出时发生原生访问冲突，问题尚未解决；回到菜单或 MCP 根作用域释放不证明进程正常退出。本预览版不宣称已修复该问题，也不宣称本源码修订通过玩家可用性验收。本次源码预览不附带 Paradox Mods 玩家发行版。
