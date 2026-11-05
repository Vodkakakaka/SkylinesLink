# MCP 客户端手动检查

`codex-readonly.mjs` 是手动开发检查，不属于默认 `pnpm test`。它通过已安装 Codex 的独立 app-server 调用真实 Launcher / Host / net48 RuntimeProbe，仅开放 `skylines_context` 和 `skylines_operation_get`。不启动游戏，不发送 `turn/start`，不登录，不连接正常桌面服务，也不读取或复制正常客户端凭据。

## 前提与运行

需要仓库要求的 Node.js、Windows 上的 PowerShell 7.4 或更高版本、已安装且支持直接 `mcpServer/tool/call` RPC 的 Codex，以及已有的 RuntimeProbe 和完整 Core 候选。脚本不会安装、构建、下载或选择“最新”客户端/制品。

这两个手动入口目前断言原查询修订 3 的结果，用于保留已核旧候选的真实客户端证据；当前产品源码已升级查询修订 4。旧 Core 必须配同修订的 RuntimeProbe，不能拿当前重新编译的探针混测，或把先前通过记录贴到新源码。修订 4 的 Codex/DSH 重验仍待匹配的新 Core 就绪，默认离线 SDK/MCP 通过不代替这项验收。

显式提供本次要验证的二进制、精确客户端版本、候选清单哈希和来源提交。哈希与提交应取自已核对的候选记录；不要在运行命令中对任意目录即时算哈希，再将它当作事先确认的候选身份。路径均须是绝对路径。

```powershell
node tests/integration/clients/codex-readonly.mjs `
  --client-bin '<Codex 可执行文件绝对路径>' `
  --expected-client-version '<codex-cli 完整版本输出>' `
  --core-root '<完整 Core 候选绝对目录>' `
  --expected-manifest-sha256 '<64位小写十六进制清单哈希>' `
  --expected-source-commit '<40位小写十六进制来源提交>' `
  --candidate-kind nativeaot `
  --runtime-probe '<SkylinesLink.RuntimeProbe.exe 绝对路径>'
```

`--candidate-kind` 接受 `managed` 或 `nativeaot`，记录操作者声明的构建类型；检查本身不鉴别编译方式，不把 Managed 通过当作 NativeAOT 通过。RuntimeProbe 另记其实际文件 SHA256，不默认与 Core 来自同一提交。清单来源提交也不代表当前工作区 HEAD。

默认调用 `pwsh.exe`，可用 `--pwsh-bin '<绝对路径>'` 指定正式 PowerShell 入口；不依赖 `powershell.exe` 对应哪个版本。`--help`、缺参数和格式错误在创建目录或启动子进程之前返回。所有命令行参数都不包含 token。

## 验证与隔离范围

检查按以下顺序执行：

1. 核清单原始字节哈希、来源提交，并在执行 Setup 前核其实际文件大小/哈希。Setup 只向本次随机环境准备指定候选，然后写该环境自己的 activation。
2. 使用真实 Windows UserProfile 下的新 `probe-[24hex]` 环境。目录创建时即设置当前用户 SID owner 与当前用户/SYSTEM 私有 ACL；祖先不能是重解析点。RuntimeProbe 创建健康的空 journal 和仅 Read 授权，随机内部 token 只经其私有 stdin。
3. 为 Codex 创建空 home、cwd、SQLite/log 目录。子进程环境只继承必要 OS 路径值；不继承 API token、代理或共享 daemon 变量。正常用户 config 只检测是否存在，不读正文；`project_root_markers=[]` 禁止向祖先搜索项目配置。
4. 先核 `initialize.codexHome` 和有效存储/权限配置、apps/plugins 关闭，再确认有效 `mcp_servers` 为空。之后才创建 ephemeral thread，配置唯一测试 server 与两个工具白名单。额外 MCP、登录/交互请求或模型回合都会使检查失败。
5. 初始化/工具目录不能提前连接 Runtime。两个真实读取必须匹配本次实例及健康空 journal；撤销 Read 后同一工具必须返回 `UNAUTHORIZED`。
6. 关闭本次 app-server stdin，等待实际进程及其 stdio 关闭、自有 Launcher/Node 消失，再 stop RuntimeProbe；连接数及受账 byte-array 容量必须归零。stdout 总接收限制为1 MiB、stderr64 KiB；收尾晚到的超额输出、未完成 JSONL 或强制终止均不能计为成功。

使用的是 Codex 当前非 experimental RPC 入口，不代表所装客户端本身一定是稳定发行版。检查不主动调用模型/网络服务，但没有提供操作系统网络隔离；若当前客户端启动需要账户或外部服务，停止并报告限制，不改用正常凭据。此结果不证明模型能正确选工具、游戏 Mono、普通权限令牌、DSH 或生产 HTTP 接入。

## 结果与清理

stdout 输出有限 JSON 摘要；完整同名结果位于本次环境中的 `codex-validation-result.json`。摘要包含候选身份、阶段、正常退出事实、剩余自有产品进程和 RuntimeProbe 哈希，不保存任意 RPC 错误正文、配置全文或工具数据。`passed=true` 需要实际调用与全部正常关闭同时成立，不能只看进程 exit0。

脚本不自动删测试环境。失败时保留现场；可能残留的自有进程按报告中的 PID、创建时间和环境内相对路径核对，不能按进程名批量结束。强制终止只能作为失败后的收尾，不能改写为通过。确认进程已结束、摘取必要脱敏结果后，复用既有清理入口：

```powershell
$probeRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.skylineslink\environments\<摘要中的 ownedEnvironment>'
pwsh -NoProfile -File tests/integration/desktop/remove-owned-probe.ps1 -ProbeRoot $probeRoot
```

该入口核真实 UserProfile、随机目录名、精确 `.owned-probe` 内容和祖先/子项重解析点，再常规删除；不使用 `-Force`。若清理被占用、属性或重解析检查拒绝，保留并查明原因，不改换删除工具绕过。测试产生的 home、授权文件、日志和完整现场不得提交或上传。

首次将检查用于新的公开入口、客户端版本或 Core 候选时，另记录实际运行结果。仅语法/参数失败检查通过不能宣称客户端兼容已经验收。

## DSH 真实应用检查（本地合成响应）

`dsh-readonly.mjs` 同样是显式手动检查，不进入默认测试。当前 DSH 公开 SDK 没有直接 MCP 调用 RPC；此入口通过安装内正式 CLI carrier 启动独立 headless 应用，让固定本地 Messages SSE 响应经过 DSH 的真实 Agent、工具注册/执行和 MCP bridge，再调用产品 Launcher / Host / net48 RuntimeProbe。它没有调用真实模型或 DeepSeek 服务，不把单独导入 MCP 库当作应用验收。

只支持已审阅的 DSH `0.2.1-rc.5`、build commit `d4db512a969fccf0c5cc7ec8fe38e9c1d27af916`；同时核13个随包实现文件与 js-yaml 4.3.2 的固定文件集合指纹。其他版本或内容变化会拒绝，不能只修改命令行版本字符串跳过复核。此版本限制属于手动验证器，不是 SkylinesLink 的客户端版本限制。

```powershell
node tests/integration/clients/dsh-readonly.mjs `
  --client-bin '<DeepSeek Harness.exe 绝对路径>' `
  --client-asar '<安装内 app.asar 绝对路径>' `
  --expected-client-version '0.2.1-rc.5' `
  --expected-build-commit 'd4db512a969fccf0c5cc7ec8fe38e9c1d27af916' `
  --core-root '<完整 Core 候选绝对目录>' `
  --expected-manifest-sha256 '<64位小写十六进制清单哈希>' `
  --expected-source-commit '<40位小写十六进制来源提交>' `
  --candidate-kind nativeaot `
  --runtime-probe '<SkylinesLink.RuntimeProbe.exe 绝对路径>'
```

全部目标参数明确提供后才会运行；没有默认安装位置、候选或来源提交。Core 身份、Setup 执行前大小/哈希、`candidate-kind` 仅记录构建类型声明、可选绝对 `--pwsh-bin` 的语义与前述 Codex 检查相同。RuntimeProbe 在运行前记录实际 SHA256，停止后再核不变，不能因此推定它与 Core 同提交。`--help` 和 `--self-test` 单独使用，不需要目标，也不会启动子进程、服务或会话；后者仅核内存中的配置/协议夹具，不代替真实应用测试。

### 配置与调用范围

共享 `fixture.ps1 -Client dsh` 保持真实 UserProfile、随机环境、当前用户/SYSTEM ACL、归属标记和进程检查，创建空 `dsh-home`、`dsh-cwd`、`dsh-agents-home`。默认 `-Client codex` 保持原 Codex 目录和配置存在性检查，既有 Codex 命令无需变化。

DSH 使用该独立 home 和 Agent home，不连接正常桌面实例；子进程只保必要 OS 环境，禁正常账户/凭据服务、祖先指令与技能读取、遥测、标题模型和其他 provider。本地 adapter 只用固定假 key，没有真实 API 凭据；内部游戏 token 仍只经 Runtime 私有 stdin。正式 carrier 的 Electron Node mode 不运行桌面 main，也不安装 Node/插件。

启动前先获取有界 `--dump-config`。`dsh-config-reader.cjs` 通过安装内已核 js-yaml 解析，并只输出有限配置摘要；`!!js` 只变为带标记的数据，不执行。关键 rows 必须为与本次 overlay 完全一致的常量。dump 是组合树，不是表达式求值后的运行时配置；第一轮本地请求再核 model、工具名和输入 schema。DSH MCP 配置没有工具白名单字段；产品只发布两项读工具，合成服务只输出这两个精确工具名，任何额外调用使检查失败。

本地服务只监听随机 loopback 端口，接受固定四轮 `/v1/messages` 请求：

1. 发现两读工具后，确认 Runtime 尚未连接，再调用 context。
2. 校验真实实例、menu/world=null，随后按固定 Unicode operationId 查询。
3. 校验健康空 journal 返回 absent，确认本次 Launcher/Node，再由夹具撤销 Read，要求同一 context 调用。
4. 校验实际 `UNAUTHORIZED` 失败后，返回固定最终文本。最终文本本身不能证明工具成功。

服务不代理或转发请求。每请求1 MiB、总4 MiB、最多4请求及有限连接/时间预算；应用 stdout 总1 MiB、stderr64 KiB，禁止截断、尾部残帧或 late failure 被忽略。完整 headless 事件须与固定工具调用/结果对应。DSH 将 MCP `isError` 投影为普通工具错误，因此不把模型侧文本等同于原 structuredContent 外壳。

### 结果、退出与限制

摘要写入本次环境 `dsh-validation-result.json`，并输出有限 JSON；不保存 provider 请求正文、原始 dump、任意错误正文或工具数据。stage/providerStage 用于失败定位。该环境也由前述 `remove-owned-probe.ps1` 精确清理，脚本不自动删除。

检查等待应用实际进程/stdio 结束，再核本环境全部产品进程消失、Runtime 连接/受账容量归零及实际停止、本地服务/请求/socket 结束。DSH stdio 协商可能先创建临时探测 child，不能只看最终一对进程。外部可观察回收通过仍不能证明每个内部 disposer 成功：已核 DSH 的清理超时或 dispose 异常也可能沿用 exit0，所以摘要始终保留 `applicationDisposerSuccessProven=false`。强制收尾、超限或任何先前失败都不能转为 passed。

这只验证指定 DSH headless 安装、合成本地响应和给定 Core/探针的组合，不证明真实模型选工具、GUI、游戏 Mono、普通权限或生产 HTTP。未提供 OS 级网络隔离，不能从 loopback adapter 和禁用配置推断客户端绝无其他启动联网。私有原型的成功不能代替参数化公开入口的实际运行，改变入口或候选后须另记录证据。
