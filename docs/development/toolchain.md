# Development toolchain / 开发工具链

[English](#english) | [简体中文](#简体中文)

## English

These are contributor build instructions for Windows. They do not install the mod into a game profile or describe a player release. VS Code is suitable for editing C# and TypeScript; a full Visual Studio IDE is not required.

### Minimum tool versions

Development tools may use these minimum versions or newer stable versions. Dependency lockfiles remain fixed; each release records the tools actually used.

| Tool | Minimum / dependency version | Source of truth |
| --- | --- | --- |
| Node.js | >=26.13.0 | [package.json](../../package.json), [.node-version](../../.node-version), [.nvmrc](../../.nvmrc) |
| pnpm | >=12.16.1 | [package.json](../../package.json) |
| .NET SDK | >=10.0.403 | [global.json](../../global.json): newest installed stable SDK at or above the minimum |
| TypeScript | 7.0.2 | Project devDependency in [package.json](../../package.json) |

Use PowerShell 7 (`pwsh`) for the PowerShell scripts. TypeScript is installed with the project; no global TypeScript installation is needed. Use pnpm with [pnpm-lock.yaml](../../pnpm-lock.yaml), not a second npm lockfile. Exact MCP SDK and other JavaScript dependency versions are in [the Host package](../../src/host/package.json) and the lockfile.

`packageManager`, `.node-version` and `.nvmrc` identify reference versions. [.npmrc](../../.npmrc) allows a newer pnpm without automatically downloading a different package-manager version. [Directory.Build.targets](../../Directory.Build.targets) keeps the net10.0 ILLink/ILCompiler package versions aligned with the reviewed lockfiles independently of SDK patch upgrades. This preserves compatibility analysis without turning an SDK patch into a dependency update. Bundled player runtimes remain separate, byte-verified distribution inputs.

### Ordinary build and checks

From the repository root, with compatible tools available:

```powershell
pnpm install --frozen-lockfile
dotnet restore SkylinesLink.slnx --locked-mode
pnpm build
pnpm test
```

`pnpm build` generates contracts, checks TypeScript, bundles the Host and builds the six game-independent C# projects in [SkylinesLink.slnx](../../SkylinesLink.slnx). The solution excludes the game Mod. `pnpm test` runs the configured offline checks and Windows integration fixtures; it is not game acceptance.

Ordinary C# restores use each project's `packages.lock.json`. Local .NET outputs go under `artifacts/dotnet`; the Host bundle goes under `artifacts/host`.

### Official game Mod build

This additionally needs a legitimate Cities: Skylines II installation and its configured official modding toolchain, including the matching Unity editor, project and dependencies. Use the versions required by that toolchain; do not independently upgrade Unity, Entities or Burst.

The [Mod project](../../src/game/SkylinesLink.Mod/SkylinesLink.Mod.csproj) targets .NET Framework 4.8 and C# 9. It resolves the official `Mod.props` and `Mod.targets` through the user-scoped `CSII_TOOLPATH`; the official tools also use their configured `CSII_*` paths and Unity version. Keep machine-specific paths outside public source and follow the toolchain's setup instructions.

```powershell
pnpm build:mod
# Explicit Release build:
pwsh -NoProfile -File tools/build/build-mod.ps1 -Configuration Release
```

The [build script](../../tools/build/build-mod.ps1) temporarily sets `DOTNET_ROLL_FORWARD=Major` in its process so child tools may use an installed newer runtime, then restores the previous value in `finally`. It does not change machine/user environment settings or official runtime configuration, install a runtime, or turn a post-processing failure into success.

This build invokes the official post-processor. Deployment is disabled and the Mod publisher is rejected by the project. The optional `-IntegrationProbe` switch produces a separate developer build, not a player package. Compilation and post-processing do not prove runtime behavior in the game.

### NativeAOT desktop outputs

NativeAOT is separate from ordinary development. The current target is Windows x64 and needs the supported native C++ build tools and Windows SDK in a suitable build environment; see [Microsoft's NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/?tabs=windows). This does not require the full Visual Studio IDE for ordinary C#/TypeScript work.

```powershell
pnpm build:aot
```

The [desktop publication script](../../tools/build/publish-desktop.ps1) uses locked restores and writes local Launcher/Setup executables and notices under `artifacts/nativeaot`. NativeAOT uses the separate `packages.nativeaot.lock.json` files. It does not upload a release; ordinary managed-build checks do not establish AOT compatibility or complete package acceptance.

When updating the baseline, update the version files, relevant lockfiles and generated outputs together, then run the affected checks. Keep proprietary game dependencies out of the repository and release payloads. See [architecture](../architecture.md) and [licensing](../governance/licensing.md) for component and distribution boundaries.

## 简体中文

本文面向 Windows 贡献者构建，不会将模组安装到游戏配置中，也不是玩家发行版的安装说明。可以使用 VS Code 编辑 C# 和 TypeScript，无需安装完整 Visual Studio IDE。

### 最低工具版本

开发工具可使用以下最低版本或更高稳定版本。依赖锁文件仍固定，发行记录本次实际使用的工具版本。

| 工具 | 最低版本 / 依赖版本 | 权威配置 |
| --- | --- | --- |
| Node.js | >=26.13.0 | [package.json](../../package.json)、[.node-version](../../.node-version)、[.nvmrc](../../.nvmrc) |
| pnpm | >=12.16.1 | [package.json](../../package.json) |
| .NET SDK | >=10.0.403 | [global.json](../../global.json)：采用达到最低版本的最新已装稳定 SDK |
| TypeScript | 7.0.2 | [package.json](../../package.json) 中的项目开发依赖 |

PowerShell 脚本使用 PowerShell 7（`pwsh`）。TypeScript 随项目依赖安装，无需全局安装。使用 pnpm 及唯一的 [pnpm-lock.yaml](../../pnpm-lock.yaml)，不另建 npm 锁文件。MCP SDK 和其他 JavaScript 依赖的精确版本见 [Host 配置](../../src/host/package.json)及锁文件。

`packageManager`、`.node-version` 和 `.nvmrc` 标识参考版本。[.npmrc](../../.npmrc) 允许更高版本 pnpm，不会因此自动下载切换包管理器。[Directory.Build.targets](../../Directory.Build.targets) 让 net10.0 的 ILLink/ILCompiler 包版本独立于 SDK 补丁，与已审锁文件一致，保留兼容分析，避免 SDK 补丁隐式变成依赖升级。玩家随包运行时仍按实际字节单独核验。

### 日常构建与检查

在仓库根目录执行，事先准备好兼容版本的工具：

```powershell
pnpm install --frozen-lockfile
dotnet restore SkylinesLink.slnx --locked-mode
pnpm build
pnpm test
```

`pnpm build` 生成契约、检查 TypeScript、打包 Host，并构建 [SkylinesLink.slnx](../../SkylinesLink.slnx) 中六个不依赖游戏的 C# 工程；该 solution 不包含游戏 Mod。`pnpm test` 执行配置的离线检查和 Windows 集成夹具，不代表游戏验收。

普通 C# 依赖还原使用各工程的 `packages.lock.json`。.NET 本地输出位于 `artifacts/dotnet`，Host bundle 位于 `artifacts/host`。

### 官方游戏 Mod 构建

此步骤另需合法安装的《城市：天际线 II》及已配置的官方模组工具链，包括匹配的 Unity 编辑器、项目和依赖。遵循官方工具链要求的兼容版本，不单独升级 Unity、Entities 或 Burst。

[Mod 工程](../../src/game/SkylinesLink.Mod/SkylinesLink.Mod.csproj) 使用 .NET Framework 4.8 和 C# 9，通过用户层 `CSII_TOOLPATH` 定位官方 `Mod.props` 和 `Mod.targets`；官方工具还使用其已配置的 `CSII_*` 路径及 Unity 版本。按工具链说明配置，本机路径不要写进公开源码。

```powershell
pnpm build:mod
# 显式构建 Release：
pwsh -NoProfile -File tools/build/build-mod.ps1 -Configuration Release
```

[构建脚本](../../tools/build/build-mod.ps1) 仅在自身进程中临时设置 `DOTNET_ROLL_FORWARD=Major`，让子工具可以考虑使用已安装的较新运行时，并在 `finally` 中恢复原值。它不修改机器层／用户层环境或官方运行配置，不安装运行时，也不忽略后处理失败。

构建会执行官方后处理器。工程已禁用部署并拒绝调用 Mod 发布器。可选的 `-IntegrationProbe` 开关产生独立开发构建，不是玩家安装包；编译和后处理通过不代表游戏实载行为已验收。

### NativeAOT 桌面输出

NativeAOT 与日常开发分开。当前目标为 Windows x64，需要适用构建环境中的原生 C++ 工具及 Windows SDK，前提见 [Microsoft 官方说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/?tabs=windows)。普通 C#/TypeScript 开发无需因此安装完整 Visual Studio IDE。

```powershell
pnpm build:aot
```

[桌面发布脚本](../../tools/build/publish-desktop.ps1) 使用锁定还原，将本地 Launcher/Setup 可执行文件及 notices 写入 `artifacts/nativeaot`。NativeAOT 使用独立的 `packages.nativeaot.lock.json`；该命令不会上传发行版，普通托管构建检查也不能替代 AOT 及完整安装包验收。

更新基线时，同步版本配置、相关锁文件与生成文件，再运行受影响的检查。专有游戏依赖不得进入仓库或发行包；组件和分发边界见[架构](../architecture.md)与[许可说明](../governance/licensing.md)。
