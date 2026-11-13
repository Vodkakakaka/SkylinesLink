# 当前发行许可输入

这里处理已经有实际消费者的 Node win-x64 与桌面 NativeAOT 许可输入，并提供本地 developer Core 候选组装。它不上传、不安装或激活，也不证明完整产品或模组市场合规。Host SDK 的许可仍由 `src/host/build.mjs` 及其原始通知机制负责。

## 本地 developer Core

`pnpm build:core` 仅在准确干净的 Git checkout 中执行，并要求 `artifacts/host`、`artifacts/nativeaot`、`artifacts/dotnet` 及 `artifacts/developer-core` 均不存在。已有开发输出会明确拒绝，不删除、不复用、不把当前 HEAD 贴到旧二进制上。先在独立工作树通过现有 pnpm 冻结安装准备依赖；官方 Mod 工具链仍须正常可用。该命令会实际依次构建 Host、NativeAOT Launcher/Setup 和普通 Release Mod，不部署或启动游戏。不要与其他构建共享输出目录。

组装只消费本轮固定输出路径。构建前后还核对实际 MSBuild `Compile/EmbeddedResource/AdditionalFiles` 项与 Git 文件集合，并拒绝源码目录中未跟踪的编译/脚本输入；Mod 的 `Library` 仅作为工程明确排除的官方生成状态，不能靠 Git 忽略规则获得编译资格。外部 SDK、游戏程序集和锁定包仍属于明确工具链依赖，这不是整个机器的密闭构建证明。

`inspect-core-inputs.ps1` 使用 BCL PEReader/MetadataReader 检查程序集身份、AssemblyRef、正式 BuildMode 属性与 CodeView，不反射加载或执行游戏 DLL。完整 informational version 必须对应本次产品版本及准确 commit；它不会仅凭数值 AssemblyVersion 把不同 prerelease 混为一版。元数据与 SHA-256/大小在同一个只读句柄下取得，后续复制字节必须一致。四个自有托管程序集的实际依赖必须符合当前有限关系；游戏、Unity 和 Newtonsoft.Json 仍由游戏提供，不复制进候选。IntegrationProbe、未知 DLL 或文件、错版程序集、实际本机 PDB 路径均拒绝。普通 Mod 构建可能产生三平台 Burst 文件，当前 win-x64 候选只纳入实际存在且通过核验的 Windows DLL；没有该文件时明确记录缺省，不制造占位文件。PDB、macOS/Linux 输出和 SDK 工具不随包。nvm 的受管 Node 入口仅在最开始解析为真实版本 exe，再按固定官方 SHA/许可核验与复制；其他路径的重解析限制不放宽。

候选写入 `artifacts/developer-core/core`，包含普通 Mod、Host 三个产物、精确官方 Node 及原许可、两个 NativeAOT 入口及各自原许可/通知、项目 LICENSE、SOURCE.txt，以及 `git archive` 从本次准确 HEAD 导出的对应源码 ZIP。源码文件集合及各文件实际解压内容的 Git blob hash 均与准确树逐项核对，不能让本机导出属性改写正文后仅靠同名通过；源码总量与 ZIP 在读取/展开前各有限额。受跟踪的私有目录、二进制、链接或源归档替换规则被拒绝。仅包含受跟踪文件本身不能证明内容脱敏，因此另做当前仓库/用户根前缀与 PE 调试路径检查；完整实际材料及私有值的发布前审查仍须单独完成。

复用 `CoreManifest:1`，文件按相对路径排序，清单不列自身。最后直接运行候选内实际 NativeAOT Setup 的 `verify-core`，成功后在 Core 之外写 `validation.json`，包含准确 commit、Git tree、核心清单 hash、实际程序集闭包与明确未验边界，不写本机路径。构建与组装前后都复核 HEAD/工作树；任何失败均不返回成功报告，也不自动删除失败材料。干净 NativeAOT 发布还会在构建根生成自有 Deployment 共享库的 PDB；构建白名单识别这一确切调试文件，Core 复制白名单仍排除全部 PDB，两类清单的职责不同。

SOURCE.txt 明示这是开发候选而非可用初版，当前声明工具及验证边界见同包 README.md 和 docs/architecture.md；旧验证不自动覆盖本次制品。对应源码随候选提供，不假称尚未推送的 commit URL 已可远端下载。该候选不写 activation/授权/客户端注册，不生成市场外壳或第二套核心内容。市场包与 GitHub 安装外壳须在已核平台及安装边界上消费同一份核心；此命令不代表完整发行验收。

`pnpm test:core` 仅验证有限闭包/禁止材料规则及真实 Git 源归档成员，不触发上述构建、不连接游戏或上传。

`runtime-inputs.json` 固定已核版本、官方来源、原文完整性和本次取证范围。`licenses` 保留完整上游文本，没有将不同第三方许可改写成项目 MPL-2.0 或统一 MIT。局部 `.gitattributes` 关闭这些原文的换行转换；Node 文件采用官方 raw 的 LF，.NET 文件保留 NuGet 归档原始字节。构建不下载或执行外部脚本。

## Node 的实际消费者

`runtime-inputs.mjs` 导出只读函数 `verifyNodeRuntime(nodeExe)`。它核对待分发的实际 exe SHA-256 与固定官方 win-x64 制品，再核完整原 LICENSE，返回版本、文件大小、哈希及许可来源路径。函数不启动 exe、不联网、不安装。

桌面夹具在复制 Node 后验证目标文件，复制返回的 `license.sourcePath` 到 `license.relativePath`（`runtime/Node.LICENSE.txt`），再将实际文件纳入既有 Core 清单。该入口尚不构成玩家 Node 安装器。

2026-10-31 的取证：v26.13.0 本机 exe 与官方 [SHASUMS256.txt](https://nodejs.org/dist/v26.13.0/SHASUMS256.txt) 一致；同一字节制品的 Authenticode 为 Valid、签名者为 OpenJS Foundation。原 [LICENSE](https://raw.githubusercontent.com/nodejs/node/v26.13.0/LICENSE) 为 156,386 字节，SHA-256 记录在输入文件中。nvm 随附 CRLF 版本完整归一化后相同，仅作为来源交叉证据，构建不依赖 nvm 的文本路径。离线检查固定字节不等于重新在线验证证书或吊销状态。

## NativeAOT 的实际消费者

`tools/build/publish-desktop.ps1` 在每个实际 `dotnet publish` 成功后调用 `write-nativeaot-notices.ps1`。任一步不匹配即失败；它不修改锁文件、不下载运行时、不改变 Launcher 的工程依赖。

- 核对应工程的 NativeAOT lock 与本次 `project.assets.json` 中两个 ILCompiler 包及 ILLink 的版本和 signed-package content hash。
- 对实际 `.ilc.rsp` 的全部 `-r` 输入以 PE [HasMetadata](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader.hasmetadata) / [CLR header](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.peheaders.corheader) 区分：有元数据的输入必须能读取 AssemblyDef/AssemblyRef，拒绝 Game、SkylinesLink.Contracts/Application/Infrastructure 和 Newtonsoft.Json；损坏的 PE/元数据直接失败。无元数据输入只接受两个已审 runtime pack 的准确 `runtimes/win-x64/native/*.dll` 条目，且必须先通过下述来源、归档及逐字节核对；未知原生 DLL 不因没有元数据而跳过。分别报告实际 managed/native `-r` 计数，不加载程序集、不遍历目录，只报告文件名或计数。managed `.deps.json` 仅证明对应 managed 边界，AOT 的 PE/无 CLR header 检查不能替代这项实际编译输入检查。
- 从本次 `.ilc.rsp` / `link.rsp` 取得实际托管框架及静态库输入，包括 `Microsoft.NETCore.App.Runtime.win-x64` 的 CoreLib 和 `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64` 的 AOT 输入；这些隐式 SDK runtime pack 不在 `packages.nativeaot.lock.json` 中，因此另按实际输入和固定归档检查。
- 核五个本机 NuGet 归档的 SHA-512、来源与 content hash；再逐文件比对本次实际引用的运行时框架/静态库与对应 runtime pack 归档条目。该检查不重建编译器/SDK 的完整供应链审计。
- 核原 LICENSE 与 THIRD-PARTY-NOTICES 的归档条目、固定文件大小和 SHA-256，向当前 `artifacts/nativeaot/launcher` / `setup` 输出 `DOTNET.LICENSE.txt`、`DOTNET.THIRD-PARTY-NOTICES.txt` 与不含本机路径的 `DOTNET.SOURCES.txt`。

当前已审版本为 [最新 LTS .NET 10.0.15](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)。五个包的 nuspec 都指向 `dotnet/dotnet` commit `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7`。实际 NativeAOT runtime pack 内含原 LICENSE（1,139 字节）和完整通知（78,041 字节），对应 [runtime 源码](https://github.com/dotnet/dotnet/tree/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/runtime)。通知保守保留整个上游集合，不声称每个条目都进入当前二进制；ILCompiler、ILLink、SDK 工具文件不因此复制给玩家。

2026-11-14 对五个 10.0.15 归档逐一执行 `dotnet nuget verify --all`，Microsoft 作者签名和 NuGet 仓库签名均成功验证。归档完整 SHA-512 绑定本次验证，不冒充每次构建都重新检查在线证书。两种哈希必须区分：已签 `.nupkg` 的整个 ZIP 哈希不同于 NuGet lock 的 content hash。[NuGet 官方实现](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs) 对已签包读取内容哈希；[.NET 10 verify 命令](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify) 会直接输出该值。本次 verify 输出与锁/记录一致，没有用 ZIP 哈希伪装成 content hash。

[Directory.Build.targets](../../Directory.Build.targets) 的单个版本属性约束 .NET 10 的工具包和 reference/apphost；self-contained/AOT 通过官方 `RuntimeFrameworkVersion` 契约精确选择同版运行库。`pnpm test:core` 实际调用 MSBuild 的 `ProcessFrameworkReferences` 核对两个入口的编译器、链接器、reference、普通 runtime 与 NativeAOT 下载图，和上述独立来源记录必须一致；普通开发构建不设置 self-contained runtime pin。SDK 用标准 `latestFeature` 保持当前明确选定的 10.0 LTS 系列内 >=10.0.403，不固定到某个 SDK 补丁，也不自动跳转并装的 STS 系列。

升级版本需要重新核官方来源、原文及真实链接输入并更新本记录。当前检查尚不覆盖完整 Mod/第三方模组闭包、市场条款、正式源码获取包、玩家安装或发布认证。
