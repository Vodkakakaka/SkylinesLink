# Host 骨架验证

测试夹具位于 `tests/host`，只在隔离的本机 stdio、回环 HTTP 和随机命名管道运行。`probe.submit`、合成城市与合成操作只在测试目录，不能据此声称实际游戏施工、Codex 或 DeepSeek Harness 已通过。

## 运行

在仓库根执行：

```powershell
pnpm --filter @skylineslink/host typecheck
pnpm --filter @skylineslink/host build
pnpm --filter @skylineslink/host test
```

全套测试需要先构建 `tests/integration/runtime/SkylinesLink.RuntimeProbe.csproj` 和产品 Host bundle。管道测试通过隐藏子进程启动该探针，随机凭据经 stdin 传入，不进入命令行；结束时正常停止该进程并删除本次随机目录中的已知发现、授权、操作日志和锁文件。若需避免占用正在构建的 RuntimeProbe，在 `src/host` 运行 `node test.mjs auth.test.ts discovery.test.ts budgets.test.ts protocol.test.ts`。

`node test.mjs` 同时编译微基准；在 `src/host` 运行 `node ../../artifacts/host-tests/codec.bench.mjs` 输出 `artifacts/host-codec-benchmark.json`。当前采样每组预热 20 次、测量 120 次、每 10 次让出事件循环；另对空哨兵及有限 8 消息批次记录事件循环观测。输出记录源码指纹、输入字节/深度/token、p50/p95/p99/最大观测值；包含原始检查、公开 SDK 解码以及结构化/文本双结果的完整编码顺序。小型真实契约 Context 与 64 KiB 附近的合成角落分列；测量不是完整 SDK 请求处理、游戏帧、图像编码或跨进程复制，也不证明理论最坏停顿、堆峰值或硬实时门槛。

## 当前已覆盖的行为

- 同一 generated catalog 同时服务 stdio 和 Streamable HTTP；官方 Client 分别固定验证 2025-11-25 与 2026-07-28，执行 list、读上下文、合成提交/查询、参数错误及结构化/文本一致性。没有声明 Tasks。
- 官方 Client 2.4.0 默认仍是 legacy。现代协议必须显式 `versionNegotiation.mode: { pin: '2026-07-28' }` 并核对 `getNegotiatedProtocolVersion()`，不能按 SDK 包版本猜协议版本。
- stdio 在 SDK 解码之前限制字节、结构、绝对半帧期限与同一输入块内的帧洪水；公开 Transport 装饰器限制关联请求、通知速率和发送逻辑租约。输出租约等待真实 Writable 回调或目标流 close，SDK 的 `send()` 返回不独立构成释放证据。
- HTTP 在公开 Node 请求/响应入口执行凭据、Host/Origin、连接、请求体、期限与输出额度限制；Node 的 header 检查周期显式缩短，半个请求头与零字节连接都会关闭。响应配额由实际 finish/close 释放；body/SDK 适配过程单独占用 pipeline 额度至其 finally；游戏工作配额由实际 handler finally 释放。
- 撤销等待不会伪称已停止工作。现代 HTTP 关闭原请求及 legacy HTTP 真正断连会传播 abort；legacy stateless 的取消通知属于另一次请求，不能找到原来的 handler。该情况由原请求绝对期限收敛，原 work 仍持额到真实完成；后续生产写入必须返回可查询操作身份。
- 取消后的迟到结果不会提前恢复工作额度；stdio 真实接入测试验证 BUSY、原工作完成及后续恢复。输出预算失败仍保留已知 operationId。
- Node 与真实 .NET Framework 4.8 探针互通覆盖实例身份、代际、只读查询、实时撤权及关闭；旧实例/authorityEpoch 与冲突核心哈希分别在真实握手拒绝。内部协议 2 使用四帧双向 HMAC-SHA256，长期 token 不进入任何管道帧。固定 Python 独立向量与 Node 假端点测试覆盖坏证明、角色反射、旧证明重放、提前/重复/同块 ACK、末帧身份变化及不可续延的握手期限。当前执行环境为提升权限进程，不能外推普通用户身份或另一登录会话已验证。

## 原操作查询

`skylines_operation_get` 使用生成目录中的修订 `3`，输入继续采用 `operation-query:2`：`{selector:{kind:"operationId",operationId}}` 或 `{selector:{kind:"submissionKey",submissionKey:{writeContextId,requestId}}}`；旧平面输入显式拒绝。成功返回 `operation-lookup:2` 的 `{lookup:...}`，用 `found/pending/absent/expired/unavailable` 区分领域事实，结构与含义见 `contracts/README.md`。两端授权与唯一操作索引在游戏侧；Host 不建映射表、日志、重试或提交工具。

只读错误只描述本次查询，不附 `mayHaveApplied:false` 来否定原操作。Host 在调用前保留已验证 selector；只有合法 `found/pending` 输出才能补充嵌套 operationId，后续输出预算失败也保留这两个关联值。按 ID 查询收到不同 ID，或只适用于提交键的 `expired/CONTEXT_UNKNOWN`，会作为结果关联错误拒绝，保留原输入 ID；历史 originInstanceId 与当前连接不同仍可合法。写调用的原有不确定性语义保留，测试写工具仅存在于离线 fixture。

2026-11-01 的官方 SDK stdio/HTTP 两代协议测试消费新输入/输出，测试内存操作明确标为 `volatile`。预算反例覆盖连接错误、未知结果、合法嵌套 ID、非法结果 ID 和错关联；错误文本与结构化内容一致，查询没有隐式重放。契约更新本身不证明真实 Load、持久恢复或客户端产品验收完成。

修订 3 的四组 SDK 测试同时传递 `physicalTaskState` 与原始 `loadReturnValue`（true/false/null），保留独立 Ready 事实，不从 Task 正常结束推导业务成功。旧 `nativeState:succeeded` 结果形状明确拒绝，并保留原查询身份；`JOURNAL_INCOMPATIBLE` 是正常不可用领域结果。Runtime 对真实 `Task<bool>` 的所有权、跨字段一致性和旧日志拒绝另行验证，Host 夹具不冒充这些执行证据。

产品探针链使用实际 `host.mjs` 和 net48 `OperationAuthority`/`operations.journal`：只有显式 `operationProbe:true` 的私有测试 stdin 能受理固定合成 Load 意图，并抑制这次受理回执；两个 SDK 客户端经两个产品 Host 用事先保存的原提交键查回同一 durable 操作，按 ID 查询一致，重复同键也保留原 ID。新目录必须完成真实 header flush 才能报告健康空索引，既有目录缺账不能伪装为空。该验证没有执行原生 Load，也没有向生产目录添加提交工具。

## 资源生命周期与局部测量（原任务 2.14 / 2.16）

2026-10-31 追加反例先证明了两个实际缺口：一个 owner 的 close 拒绝会跳过 SDK transport close；输出条目已经超预算时，测量器仍先复制全部 4,096 个属性描述。修正后，各独立清理分支都会执行并等待实际结束，显式 close 仍报告未全部成功；JSON 测量按条目及字节停止，不再先创建整个 descriptor/entries 副本。后续交叉反例还验证了 10 层、每层 4,000 元素的数组会在最终拒绝前积压超量待检查项；现在所有 value/key 在入栈前统一预留 token，已排队兄弟也计入同一总额，非 JSON 的 leave 标记另受深度限制。稀疏数组仍按完整长度计费，访问器、自定义 toJSON、循环和非普通值拒绝，不能在随后 stringify 时绕过测量。V8 自身的键枚举仍可能分配，不能宣称任意对象检查零分配。

真实 HTTP 反例覆盖了响应断连后工作尚未完成、SDK 适配仍持有引用、GameClient 清理失败的组合：close 不提前完成，监听/连接/响应/pipeline 均收敛，失败仍显式返回。SDK 入口另覆盖同块重复握手与非法消息洪泛；公开 Transport 测试透传 auth/request 上下文、session、版本 setter 和完整 send options。后者证明装饰器透明性，不声称产品已经提供 HTTP 事件恢复存储。端口冲突明确失败且原监听保持，零字节连接已由当前 Node 截止机制正常关闭，因此没有额外引入重复定时器。

2026-11-01 的有限补测用 bare 官方 SDK 作对照，stdio/HTTP × 2025/2026 四组在两个读请求逆序完成时保持各自 ID、文本/结构化结果和参数错误语义；HTTP 的声明超长、分块超长、结构超限、坏 UTF8 和未完成 body 均在游戏分派前拒绝，关闭后各层额度归零。

同轮真实 Windows stdout 慢读反例定位了原入口的所有权缺口：`process.stdout.destroy()` 并不关闭底层 fd，而公开 `Socket({fd:1})` 虽能真正关闭，却仍在 Windows 强制同步写；读端停读会阻塞主循环，发送定时器无法执行。15 个有界 `tools/list` 响应足以复现，自有夹具只在验收失败后被父进程清理，不能算正常关闭。`overlapped` 创建对照也没有消除 fd 1 的强制同步写；额外 fd 也不能把同步底层管道变成可真实取消的异步管道。依据：[Node stdout 实现](https://raw.githubusercontent.com/nodejs/node/v26.13.0/lib/internal/bootstrap/switches/is_main_thread.js)、[Node net.Socket fd 1/2 实现](https://raw.githubusercontent.com/nodejs/node/v26.13.0/lib/net.js)、[官方 process I/O 说明](https://nodejs.org/api/process.html#a-note-on-process-io)。这些失败没有改记为成功。

当前入口由已有 Native Launcher 拥有外侧标准流，透明转发到两条私有异步命名管道；Node 通过公开 `Socket.connect` 连接，不读取任何标准流 getter，三个标准句柄均由 Launcher 接 NUL。Node 拥有内侧真实 Socket，发送截止后等待回调/close 才退租约。正式 `stdio-os` 夹具现在测这条实际内侧边界：最多 16 个响应包含初始化，读端停读确实观察到 `writableNeedDrain`、未完成字节和发送租约，随后实际关闭、归零并正常退出；没有以强制结束代替通过。Native 的外侧同步/overlapped IO 取消与 Join、ACL/PID 核验和完整 Launcher 链由桌面测试另验，不能从 Node 测试外推。

输入 EOF 沿用官方 SDK 语义，中止未完成请求且不保证其回答，不新增业务 drain。自然 EOF、输出关闭和显式关闭汇入同一 close Promise；`StdioHost.closed` 只观察该结果，不拥有第二套清理。任一清理失败仍结束其他分支，并向产品 composition 报告退出码 4。测试另覆盖缺端点、真实单端接入后失败、输出管道意外反向字节，以及参数/启动失败的无标准输出诊断。

当前可执行预算是通信探针初值，不是已冻结的游戏性能门槛：

| Owner | 单位与初值 | 执行边界及结束条件 |
| --- | --- | --- |
| stdio 原始输入 / pipe 帧 | 单消息 64 KiB，JSON 深度 32 / tokens 8,192 | 解码/dispatch 前拒绝；stdio scratch 固定容量，pipe 声明长度先检后分配 |
| stdio 接入关联 | 8 个请求，64 条消息 / 1 秒 | 超限关闭；响应或取消释放关联，实际工作另计 |
| HTTP 接入 | 16 连接；8 个响应、8 个 pipeline | `finish/close` 释放响应；body/SDK async finally 释放 pipeline；socket close 释放连接 |
| 共用 handler | 8 个实际工作 | 满额返回 BUSY；取消不归还，实际 finally 才归还 |
| 发送 | 2 MiB wire 等值预留，stdio 最多 16 条；每条预留 3×测量 wire bytes | stdio 真实 write callback/关闭，HTTP 响应 finish/close；该数值不是对象元数据或 V8 全堆上限 |
| 截止 | 接收 5 秒，发送 5 秒 | 半帧进展不续延；HTTP 请求总窗口 10 秒；期限触发真正关闭，持额至对应清理结束 |

有限同条件微基准中，移除全量 descriptor/entries 副本前后，4,000 字段测量 p50 为 1.346 / 0.497 ms，4,000 元素为 0.433 / 0.167 ms；这是一次前后采样及修改后复测，不能作为完整性能验收或稳定 P99 改善结论。内存样本为进程时点值，未加和 RSS/heap/external/arrayBuffers，也不冒充 allocation owner 峰值。游戏 capture、编码与结果 reader lease、跨进程复制、真实客户端及预算总量仍属于原任务未完成范围。

## 产品入口与剩余范围

`artifacts/host/host.mjs` 是实际生产入口，接受 Launcher 的 `--transport stdio --profile ... --environment ... --core-manifest-sha256 ... --stdio-relay ...`，只公开两个真实 catalog 读工具。核心哈希来自 Launcher 已核验的发行清单，必须是 64 位小写 hex；直接运行 Host 也不能省略。自身编译身份固定产品 `0.1.0-dev.0`、内部契约 `2`，不从发现文件推断自身版本，不回退旧握手。

`--stdio-relay` 必须是 32 位小写 hex；只派生固定 `skylineslink-stdio-<nonce>-in/out` 两个本机管道名，不能传任意路径。nonce 只是端点关联，不是凭据；Launcher 承担当前用户 ACL 和准确 Node 子 PID 验证。Node 两次连接共用 5 秒绝对启动期限，缺端点/单端失败/反向输出数据都会关闭该对，不重连或回退 fd 1。CLI 拒绝重复选项，参数、启动、关闭失败分别使用退出码 2、3、4；诊断由 Launcher 承担，Host 不打印内部异常或秘密。外部 MCP 客户端仍只使用稳定 Launcher 的标准 stdio。

initialize/tools/list 不连接游戏；首次实际调用惰性地共享一次发现/连接尝试。Host 从 `USERPROFILE/.skylineslink/environments/<env>/connection/` 依次读取固定 `discovery.json`（4 KiB）和 `authorization.json`（64 KiB、最多 64 profiles），先核发行再读取选定 profile，只保留该连接需要的身份与凭据。不创建、修复或写回授权，不启动游戏；失败/断线不在同一会话偷偷换实例或重放请求。新 Host 会话才重新发现。

统一根位于用户配置目录下，避免不同 AI 客户端的 MSIX 包身份将 AppData 写入各自私有位置。本次桌面实测中，逻辑 LocalAppData 路径的祖先没有可见链接，`realpath` 却指向包内 LocalCache；严格路径检查正确拒绝了两套不同位置。[Windows 官方文档](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)说明了该重定向与私有位置优先读取行为。修正是全产品统一固定根，保留现有身份与路径检查；不扫描、迁移或回退旧 AppData 存储，也不修改 AI 客户端包配置。

缺少发现返回 `GAME_UNAVAILABLE`；发现存在但授权文件/所选 profile 缺失返回 `CONNECTION_NOT_CONFIGURED`；发行不一致为 `INCOMPATIBLE_COMPONENT`，世代/读取身份变化为 `STALE_DISCOVERY`，已撤销为 `UNAUTHORIZED`。路径异常及非法记录以稳定的脱敏错误报告，不输出系统路径、token 或任意内部异常文本。

C# publisher 承担私有 Windows ACL 的创建与核验。Node 仅补充有限只读、可识别链接/junction、realpath、open/fstat 与读取后文件/祖先身份复核；Windows 的 mode 位不是 ACL 证明，Node 当前也没有可用的 O_NOFOLLOW，不能据此声称消除了全部文件系统竞态。发现中的 PID 仅供诊断；随机实例、authorityEpoch 和实时双向认证共同绑定连接，PID 重用不构成放行条件。

CLI 的部署选择标识与 Desktop 对齐：ASCII 字母、数字、`.`、`_`、`-`，长度 1–64，拒绝 `.`、`..` 和尾部点。环境再拒绝 Windows 保留设备名并转为小写；profile 是授权快照内的大小写敏感精确键，不进入路径。这是部署参数边界，不能将其误用为所有 Wire 身份字符串的约束。

当前稳定 SDK 的有限接入探针已覆盖双协议/双传输正常结果与逆序回复基线、公开选项透传、洪泛/取消及真实系统 IO 关闭；2.14 的完成状态和证据统一见原 tasks.md 与骨架验证。它不覆盖普通用户、完整 HTTP 产品生命周期、全部 allocation 峰值、游戏帧/GC 或真实 Codex/DSH，这些仍由 2.5/2.10/2.11/2.16 等原任务跟踪。`product-discovery.test.ts` 使用真实产品 Host、测试专用异步 pipe server、官方 Client 的公开 Transport 和 C# publisher 独立存储，验证两个 Host、两个 MCP 协议版本、惰性连接和撤权；该 fixture 没有证明 Native Launcher 的 ACL/PID，也不是游戏实载或 Codex/DSH 产品验收。HTTP 入口可通过测试注入配置启动，产品 HTTP profile 生命周期和游戏设置入口仍未交付；HTTP 配置的 scopes 不能代替 C# 执行边界的当前授权。

SDK 依据是本次安装的公开声明与实现：`@modelcontextprotocol/server` 2.4.0 的 `CreateMcpHandlerOptions`、公开 `Transport`、`serveStdio`，及 `@modelcontextprotocol/node` 2.1.2 的 `toNodeHandler`。实现没有修改 SDK 私有对象。`build.mjs` 生成的 `host.mjs.LEGAL.txt` 只收集部分源码注释，不是完整许可证清单；完整原文另写入 `host.THIRD_PARTY_NOTICES.txt`，两者均须随 Host 分发。

固定版本可复核位置（均相对于本包 `node_modules`）：

- `@modelcontextprotocol/server/dist/createMcpHandler-DzlXH0KL.d.mts:254`：`CreateMcpHandlerOptions` 没有 transport 工厂替换选项，因此 HTTP 不仿造 stdio 装饰器入口。
- `@modelcontextprotocol/node/dist/index.mjs:294`：`toNodeHandler` 将响应 close 转成 Request.signal，并在 write 返回 false 时等待 drain/abort。
- `@modelcontextprotocol/server/dist/index.mjs:852`：legacy stateless 每个 HTTP 请求新建 server/transport，解释跨请求取消通知的作用范围。
- `@modelcontextprotocol/client/dist/client-BTIWJvaF.d.mts:250`：协议协商为显式 opt-in，默认 legacy；测试固定协议并验证实际协商值。

## Host 发行许可材料（关联原任务 2.6 / 13）

本次只读复核用与 `build.mjs` 相同选项加 `write:false, metafile:true` 在内存构建，检查最终输出的 `inputs`，没有改变 bundle。当前生产入口只有 stdio；实际输出涉及 `@modelcontextprotocol/server` 2.4.0、`@modelcontextprotocol/core` 2.4.0、`zod` 4.6.7 三个外部包。声明依赖中的 HTTP Node adapter、测试 Client 不能仅因存在于 package.json 就算作当前生产制品内容。

server/core 的包元数据为 Apache-2.0，其各自的 `LICENSE` 内容相同：完整的 MIT→Apache 过渡说明、Apache-2.0 全文、保留的原 MIT 许可和文档许可说明。不能缩写成单独的 MIT 或用通用 Apache 文本覆盖原文件；应原样分发官方包的组合文本。[官方 SDK 原始 LICENSE](https://raw.githubusercontent.com/modelcontextprotocol/typescript-sdk/main/LICENSE) 可交叉核对，但具体发行以固定版本包内文件为准。

当前 176 字节 `host.mjs.LEGAL.txt` 只有 content-type 的短版权注释。esbuild 的 [legalComments 文档](https://esbuild.github.io/api/#legal-comments) 描述的是特定源码注释的收集与输出，不会读取所有包根的 LICENSE/NOTICE，也不会补齐 SDK 已经预打包的第三方许可。

| 来源 | 本次固定版本 | 原许可文件与来源 |
| --- | --- | --- |
| MCP server / core | 2.4.0 / 2.4.0 | 各自已安装包根 `LICENSE`，12,227 字符；两份相同，可在通知中明确同时覆盖两个包 |
| Zod | 4.6.7 | 已安装包根 `LICENSE`，MIT，版权 Colin McDonnell |
| AJV | 8.18.0 | [官方 npm 固定包](https://registry.npmjs.org/ajv/-/ajv-8.18.0.tgz) 的 `package/LICENSE`，MIT |
| ajv-formats | 3.0.1 | [官方 npm 固定包](https://registry.npmjs.org/ajv-formats/-/ajv-formats-3.0.1.tgz) 的 `package/LICENSE`，MIT |
| fast-deep-equal | 3.1.3 | [官方 npm 固定包](https://registry.npmjs.org/fast-deep-equal/-/fast-deep-equal-3.1.3.tgz) 的 `package/LICENSE`，MIT |
| json-schema-traverse | 1.0.0 | [官方 npm 固定包](https://registry.npmjs.org/json-schema-traverse/-/json-schema-traverse-1.0.0.tgz) 的 `package/LICENSE`，MIT |
| fast-uri | 3.1.8 | [官方 npm 固定包](https://registry.npmjs.org/fast-uri/-/fast-uri-3.1.8.tgz) 的 `package/LICENSE`，BSD-3-Clause，包含 Gary Court 与 Fastify 归属 |
| content-type | 1.0.5 | [官方 npm 固定包](https://registry.npmjs.org/content-type/-/content-type-1.0.5.tgz) 的 `package/LICENSE`，MIT |

AJV 等六项来自已发布 SDK 的内部预打包区域，而非本项目 pnpm 依赖解析。取证位置为 server 的 `dist/ajvProvider-DyjnscPj.mjs`、`dist/src-DdvbAHrR.mjs` 的包名/版本区域标记，并在最终 bundle 确认对应的 AJV provider、格式处理、`require_fast_uri`、`require_fast_deep_equal`、`require_json_schema_traverse`、`require_content_type` 代码。这里按这些固定上游模块完整覆盖许可，不把原 SDK 整个 dist 的候选依赖都宣称成最终输出；例如浏览器备用 `@cfworker/json-schema` 不在本次输出输入中。

六个包的官方 tarball 已仅在内存读取，逐一验证 registry 返回的 SHA-512 integrity 并核对 `package/LICENSE`；各包未发现单独 NOTICE/THIRD_PARTY_NOTICES。server/core/Zod 的已安装包亦未发现其他同类文件。六项原始 LICENSE 的 SHA-256（保留固定来源以便后续小范围收集）为：

```text
ajv@8.18.0                  a05350a88e318e4f5f2c2a1ff1e2e88daa4dd38e6e78b71cccae422bdc762cc3
ajv-formats@3.0.1            9df3bb69929a3b650ed73b3bfa1756725aaff0ac296461605753547004eafeaf
fast-deep-equal@3.1.3        7bf9b2de73a6b356761c948d0e9eeb4be6c1270bd04c79cd489c1e400ffdfc1a
json-schema-traverse@1.0.0   7bf9b2de73a6b356761c948d0e9eeb4be6c1270bd04c79cd489c1e400ffdfc1a
fast-uri@3.1.8              b010b0dfdfdb23d7396e03b82cd4621fc9bb8f95d6b0aea70b9c24e12074c786
content-type@1.0.5          257aed98914108e91a337912727b6a802eef218248507f74b76faffaff517a38
```

已保存的输入位于 `licenses/`：九个包的原始 LICENSE 文件保持上游字节内容，`manifest.json` 记录精确版本、官方 tarball、校验过的 SHA-512 integrity、许可 SHA-256，以及两份 SDK 内嵌模块文件的 SHA-256 与包版本标记。SDK/server 与 core 虽然许可文本相同，仍分别保存，以明确对应关系。采集是一次性的官方包读取；正常构建不联网。

`build.mjs` 先在内存生成候选 bundle，核对实际输出输入涉及的包/版本、已安装原 LICENSE、六项 SDK 内嵌标记集合及源码证据 hash，再核九份本地许可原文。缺失、版本漂移或内容变化都会在写制品前失败。通过后生成 `host.mjs`、`host.mjs.LEGAL.txt` 与 `host.THIRD_PARTY_NOTICES.txt`；Core manifest 应同时包含三者。通知包含明确包名、版本、固定来源与完整原文，保守覆盖六项 SDK 预打包模块，没有建立通用许可扫描框架。

构建验证通过；内存注入的缺失许可、许可内容变化、包版本漂移、SDK 内嵌源码变化四个负例均拒绝，原有三个制品的哈希保持不变。负例没有修改已安装依赖或许可证源文件。

此核对与通知准备范围是当前 Host bundle，不能外推整个 Core（随包 Node、.NET、原生组件）或模组市场已完成许可审查；日后把 HTTP 加入生产入口时也须按实际输出重新核对范围。
