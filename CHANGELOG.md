# Changelog / 更新记录

## Unreleased / 开发中

- Fixed stdio memory growth during long sessions. The Windows Launcher now waits for process and pipe events while idle. Existing output deadlines and worker cleanup still apply.
- City and building queries share capture lifecycle and encode a result once for consumers of the same sample.
- Authenticated game pipes now survive idle periods and keep the original deadline for started frames. Operation lookups retain the selector and source supplied at call time.
- Source packaging scans verified Git archive bytes directly. Clean builds record delivery outputs and verify cleanup.

### 简体中文

- 修复 stdio 长连接的内存增长。Windows 启动器空闲时等待子进程和管道事件，保留输出超时与工作线程回收。
- 城市与建筑读取共用采样生命周期，同一份样本的结果只编码一次。
- 已认证的游戏管道可以继续使用空闲连接，已开始的帧沿原绝对期限收束。操作查询固定调用时的选择器和来源。
- 源码打包直接检查已核对的 Git 归档字节。干净构建记录交付产物，并核对临时输出清理结果。

## 0.1.0-dev.0 — Developer foundation preview

First source preview of the C# game runtime, TypeScript MCP Host, shared contracts, desktop entry points and isolated validation tools. Includes nine declared MCP tools, bilingual public guidance, bounded communication, operation/history components, native development experiments, architecture checks and Windows CI.

Development tools accept stable versions meeting the minimum baseline. Dependencies remain locked. Source packaging verifies the exact public Git tree and archived blobs and provides a commit-bound manifest and SHA-256 checksums.

This release supplies contributor source and validation evidence. It does not provide a player installer, a Paradox Mods subscription package or production construction tools. Full game coverage, player/AI coexistence, installation/update flows and performance acceptance remain open. The native exit access violation seen in development sessions is unresolved. See the [preview guide](docs/development/preview.md).

### 简体中文

C# 游戏运行时、TypeScript MCP Host、共享契约、桌面入口和隔离验证工具的首次源码预览。包含九个声明 MCP 工具、双语公共指南、有界通信、操作与历史组件、原生开发实验、架构检查及 Windows CI。

开发工具允许达到最低基线的稳定版本，依赖继续锁定。源码打包检查准确公开 Git 树和归档内每个 blob，提供绑定提交的清单与 SHA-256 校验值。

本版提供贡献者源码及验证证据，不提供玩家安装器、Paradox Mods 订阅包或生产施工工具。完整游戏覆盖、玩家与 AI 并行、安装更新及性能验收仍未完成；开发会话中观察到的原生退出访问冲突尚未解决。详见[预览版说明](docs/development/preview.md)。
