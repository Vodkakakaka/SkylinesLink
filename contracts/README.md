# 契约与派生代码

本目录是 SkylinesLink 组件间普通值的结构权威。当前骨架有 30 份产品 schema、2 个只读产品能力，以及独立的测试模型。活动 `operations.get` 修订5使用 `operation-query:3` / `operation-lookup:4`，保留覆盖缺口返回 `RETENTION_NOT_COVERED`；`journal-control:1` 支持同writer的有界物理保留。完整游戏操作和 Task/Plan 尚未实现；授权存储、认证与生命周期由连接模块负责，这些结构文件不能证明游戏或客户端实载通过。

## 输入与派生

- `schemas/`：JSON Schema 2020-12 有限产品子集；根 `$id` 使用不可变的产品契约修订，子定义不能改变 `$id` 或方言。
- `capabilities.json`：能力身份、修订、效果、输入输出引用和边界专用槽声明。动态模组文字不能修改这份注册定义。
- `fixtures/`：两端共用的合成输入、预期结果和测试模型；测试模型不进入产品能力目录。
- `ts/index.ts`、`cs/SkylinesLink.Contracts/Contracts.generated.cs`：生成的类型、描述、校验模型与 codec。
- `src/desktop/SkylinesLink.Deployment/Generated/`：同一发行 schema 的有限子集，使用 System.Text.Json，独立于游戏 Contracts/Newtonsoft。
- `schema-identities.generated.json`：已登记schema及保留历史的规范化原文指纹，同一ID不能改变原文。
- `semantic-identities.generated.json`：同一schema的受控边界/十进制语义，以及能力id/revision对应的效果、输入输出和公开调用名。保留不再活动的指纹，不恢复其活动目录。

生成器只读 schema、声明、测试模型与自身支持源，不依赖 Host 或游戏编译产物。派生文件可追踪；只修改源文件，再运行生成。`--check` 验证实际派生差异，不接受手工修补生成文件。

身份守卫在写任何派生输出前核两份已有指纹，未知配置字段、重复metadata路径、旧身份换义、缺失或损坏指纹均失败。元数据顺序不影响语义身份，能力description修字不改绑定；schema原文注解仍受原文身份约束。不锁compiled model或生成器版本。首次语义基线只覆盖核过的23份原文和两项当前能力，没有猜测早期临时绑定；这些构建守卫不替代可信Git/发行历史或代码审查。

## 有限结构和验证语义

支持闭对象、本地无递归 `$defs/$ref`、有界数组/字符串、boolean/null、有界 number/安全 integer，以及要求 `kind` 常量的闭合对象判别联合。未知关键词、无界集合、非法注解、改变引用基准的子 `$id`、重叠或不支持的分支在构建时失败；未引用的定义也检查。

nullable 与 required 分别表达。JSON Schema 关键词取交集：`type: [string, null]` 加不含 null 的字符串 enum/const，仍拒绝 null；明确的“枚举类型或 null”二分 `anyOf` 才允许该 null。C# 可选字段使用 `Optional<T>` 保留 absent/null/value，生成 codec 不注入 default 注解。

nullable的null分支也完整执行有限profile检查，不能在其未引用定义或注解中隐藏非法结构。TS直接普通值入口只接受密集数组和普通数据属性，拒绝holes、getter、序列化会丢失的字段及自定义对象/数组行为；不通过JSON往返清洗或执行getter来完成检查。合法null原型数据仍支持，显式undefined不等于字段缺失。

内部SLNK帧的固定字节样例在`fixtures/pipe-frame-v1.json`，由Node实际解码器/接收路径与net48共用源码读取。帧层长度为1到当前上限，单字节JSON仍须经后续Wire契约拒绝；超长声明分配前拒绝，非法UTF-8与不合法Wire消息分层处理，不把小载荷当成已认证请求。

字符串须为 Unicode 标量序列，长度按标量数；传输另核 UTF-8 字节。number 使用有限 binary64，integer 限安全整数范围。规范十进制规则由受控字段元数据声明符号、总位数和固定小数位；不接受指数、前导加号、多余前导零或负零。当前产品 schema 尚无金额字段，相关规则由共享测试模型验证。

`RegisteredPayload` 仅允许规定的请求 arguments/结果槽：外层验证后按能力 ID/revision 再验内层，结果须提供原调用捕获的 binding；不能把外壳合法当作可执行。`SchemaDocument` 只接受已编译 schema 的原文/身份，客户端不能提交任意 schema 注册能力。描述中的能力与其 schema 的业务关联仍由唯一能力目录负责。

TS `parseContractJson` 与 C# `ContractJson.Parse` 提供严格 JSON 入口：拒绝注释、单引号、裸键、尾逗号、非 JSON 数字和孤立 surrogate，重复键采用后值。C# 不直接依赖 Json.NET 的宽松解析行为。词法和普通值检查有有限深度、节点/成员、字符串及原文长度；当前数值是骨架防无界输入的初值，不能当作已测游戏性能预算。原始帧分配和流生命周期由通信层另负责。

C# 直接构造的 `JValue` 也须满足普通 JSON 值约束。Json.NET 的 `String/null` 内存形态在 validator 中安全拒绝为 TYPE；可空字段由 codec 明确输出 JSON null，不将内部空值误送到 Unicode 字符串检查。该形态无法写成 JSON 文本 fixture，另由托管入口直接构造回归。

## 消费入口

TS 包为 `@skylineslink/contracts`。使用 `schemaIds`、`schemaById`、`capabilities`、`validateContract`/`requireContract`；注册官方 SDK 时绑定原 schema 与对应 validator。源码保持平台中立，不导入 Node、DOM 或 MCP。

C# 命名空间为 `SkylinesLink.Contracts`。使用 `ContractCatalog.ParseAndValidate`、`Validate`、`ContractCodec.Decode*`/`Encode*`；结构验证不能替代当前资金、解锁、权限和对象代次检查。JSON 库按游戏已有 13.0.2 API 兼容边界编译，Mod 不再分发另一份 Newtonsoft.Json。

Deployment 使用 `SkylinesLink.Deployment.Generated.DeploymentContract.ParseCoreManifest` / `ParseActivationRecord`。路径归属、重复文件、哈希真实性与实际激活是 Deployment 的规则，不通过增添宽泛 schema 关键字代替。

历史消费者最初通过 `operations.get:4` 接入 `operation-query:3` / `operation-lookup:3`。当前活动修订5复用相同输入，输出 `operation-lookup:4` 仅扩live保留未覆盖原因；旧schema/绑定指纹保留，注册入口不静默映射旧4。`operation-history-checkpoint:1` 和 Native request/capture 两份结构不变，检查点最多32条持久终态，缺失只表示 `HISTORY_NOT_COVERED`。上下文、selector、source和record的重复子结构由生成器核同形，Deployment只解析必要子值并提供无反射capture编码。文件完整性、授权、跨记录一致性、当前性和选中记录仍由各自可信边界负责。

真实编码夹具确认：默认 STJ 的最大长度非 BMP 单条 capture 为 17,472 字节，故 Native 回复限额为 32 KiB；Newtonsoft Default 编码的 32 条唯一控制字符记录加 SLHC 44 字节结构共 217,691 字节，低于 256 KiB。这是当前有限字段/编码的容量证据，不是文件授权、进程峰值、NativeAOT 或实际游戏验收。

## 发现、授权与内部认证 2

`AuthorizationSnapshot` 与 `EnvironmentDiscovery` 是固定本地记录的同源结构；前者包含内部、外部两跳的独立凭据，后者不接受凭据字段或任意凭据路径。未激活授权快照允许 authorityEpoch 为 0；发现与握手必须为活跃代次。`externalHttpToken` 必须明确为字符串或 null，缺失不等同于未配置。最多 64 个 profile；文件字节预算、标识与小写 hex 语义、profile 唯一性、epoch 顺序和原子持久化仍由可信边界检查。

旧 `wire-authenticate:1` / `wire-authenticated:1` 保持不变。新入口明确使用 `WireAuthenticateV2` → `WireChallengeV2` → `WireProofV2` → `WireAuthenticatedV2`，对应 ID 修订 `:2`。Hello 仅携带公开身份与随机 nonce，不接受长期 token；成功回复携带实际实例、发行、profile 与代次，不能以描述文件中的 scope 替代服务端当前授权。结构验证不负责 HMAC、随机数生成、恒定时间比较或授权临界区。

认证证明的字段顺序、长度前缀与生命周期以设计 D11.1 为准。`fixtures/handshake-v2.json` 提供三组公开合成凭据与固定结果，分别覆盖 ASCII、UTF-8 请求 ID 和安全整数边界。预期 HMAC 由独立 Python `hmac`/`hashlib` 与 `struct` 计算，Node 测试再独立核对；Host 和运行时实际认证实现应消费这些固定结果验证，不能把测试用凭据用于运行配置。加密实现不生成进 Contracts。

## 原操作查询与内部日志

`operations.get` 当前修订为 `4`，工具名仍为 `skylines_operation_get`。输入使用 `OperationQueryV3`：原有 `selector` 闭合联合保留，另可显式设置 `source:"live"|"history"`；缺省按 live 处理，不注入字段。调用方不能指定环境或 profile。当前 Read 授权可查询同环境脱敏事实，不引入按 profile 所有者划分的第二读取权限。原 schema 与能力修订的指纹保留，活动调用不静默映射旧修订。

`OperationLookupV3` 根是 `{selector,result}`，两个 MCP 协议版本共用形状并回显原选择器。`result.kind=live` 携带实际实例和原 lookup；其分支含义如下。不可变的 `OperationLookupV2` 仍可独立解析，但不再是活动能力的根输出。

| kind | 返回的事实 |
| --- | --- |
| `found` | 已确认受理，保留操作 ID、原实例、执行状态、原操作的副作用不确定性及当前事实的可恢复性 |
| `pending` | 已分配 ID，但首次受理落盘尚未确认；不声称受理或补写副作用 false |
| `absent` | 健康、完整恢复的适用历史视图在本次查询时没有记录；不是允许重新提交的凭据 |
| `expired` | 没有已知记录，且提交键引用了已知失效上下文；不用于按操作 ID 查询 |
| `unavailable` | 上下文未知、恢复不完整、日志不可用或明确不兼容；不能解释成原操作未执行 |

已知记录优先于上下文期限；未知上下文返回 `CONTEXT_UNKNOWN`，不能伪称已签发后过期。当前单 Load 账没有历史清理；未来若保留策略造成历史覆盖缺口，应返回不可用，不能用剩余索引 miss 证明完整历史缺失。查询、缺失及读取错误均不触发重放。

`result.kind=history` 区分 `access=activeAuthority|offlineLease`，携带 producer/source、水位、完整性和可空 ageMs。只返回一条已发布的持久终态或 `HISTORY_NOT_COVERED`；历史观察始终 current=false。实时调用失败不改读磁盘。stdio Host 的离线读取使用父 Launcher 持续固定的同包入口；它只读既有授权/租约/SLHC，校验完整文件后选择一条，释放租约才输出。Host 首次成功读取绑定同一主体代次，后续不自动升级授权。生产 HTTP profile 历史读取、全局进程/堆预算、物理保留与 AOT 新消费者验收仍未完成。

`found.durability` 为 `durable|volatile|unavailable`，描述本条已知执行及历史事实的可恢复性，不是全局 writer 健康或首次 accepted 的标签。另一操作写失败不能抹掉已有持久终态；本操作最终落盘失败也不能抹掉活实例已证实的执行或未执行事实。可缺省的闭 `load` 对象分别表达原生 Task、目标匹配、当前观察、因果关联、历史完成及观察结束。`isObservationCurrent` 由查询时真实实例和代次重算，不能跨重启恢复为 true。

`load.physicalTaskState` 描述实际原生 Task 的状态，正常结束使用 `ranToCompletion`；`loadReturnValue` 单独保存原始 `true` / `false`，尚未取得返回值时明确为 `null`。实际 `Task<bool>` 正常结束、Load 返回 true 和可信目标 Ready 是三个独立事实。false 不转换成 faulted，也不能因另有 Ready 就宣称历史加载成功。跨字段一致性由唯一 Runtime/Operations 核验；Host 不把队列任务或协议请求结束解释成原生成功。

`OperationJournalRecordV2` 是当前内部唯一日志结构，不增加公开能力。五个闭分支是 `header`、`accepted`、`commitIntent`、`nativeTerminal` 和 `terminal`；`commitIntent.target` 只是冻结意图，`nativeTerminal.boundTarget` 才记录提交回执中的实际绑定，未核时明确为 null。物理终态与返回值分别保存，记录没有凭据、重放指令或持久化的“当前观察”布尔。序列连续性、环境身份、键去重、代次、命令哈希、真实持久确认及恢复规则由 Journal/Operations 核验，结构 validator 不替代这些规则。

旧 `operation-journal-record:1` 仅保留历史结构。运行时使用 SLJO frame version 2；旧版或混合版本明确返回 `JOURNAL_INCOMPATIBLE`，不发布其前缀索引、不写入、不截断或重建原文件，也不猜测旧记录缺少的 bool。此版本不提供自动迁移。

`fixtures/operation-v2-cases.json`、`operation-journal-cases.json` 保留旧结构样本；`operation-v3-cases.json`、`operation-journal-v2-cases.json` 覆盖当前字段、true/false/null、旧形状拒绝及当前能力绑定。四组同时由 TS 和 C# 契约入口消费，C# 额外执行实际 typed codec 往返。Host 另验证请求/结果关联；错误输出省略只读调用的 `mayHaveApplied`，保留已验证的原 selector，不将读取错误误作原操作结果。

## 本轮验证和限制

在仓库根执行：

```powershell
pnpm generate
pnpm check:generated
pnpm test:contracts
pnpm test:contracts:managed
```

2026-10-31：Node 的 88 项检查通过，包括 80 条共享 fixture、构建拒绝、描述冻结、词法预算与独立 HMAC 向量。C# 探针的 80 条共享 fixture 全部匹配，并通过新增六类接入 DTO 的显式 codec 往返、缺失/null 和 default 不注入检查。该探针运行于 .NET 10，不能冒充随游戏 Mono 的实载结果。TS 严格类型检查使用 `strict`、`noUncheckedIndexedAccess` 和 `exactOptionalPropertyTypes`。新增接入契约的实际持久化和双向认证结果由两端集成探针提供。

2026-11-01：新增查询与日志结构后，Node 128 项检查通过，其中 120 条为两端共享 fixture；新 DTO 的 C# codec 往返已接入同一检查入口，合并后的托管验证由统一构建执行，不以 Node 结果替代。 schema 生成与 `--check` 均保持旧 ID 指纹不变。

同日 Load 返回值修正后：Node 150 项检查通过，其中 142 条为共享 fixture；新 nullable bool 同时进入 C# typed codec 往返入口，托管结果仍由统一构建核实。23 个 schema 的派生检查通过；与上一轮 185 文件源快照逐一比较，既有 21 个 schema 的原始 SHA256 全部未变。

本模块只证明已交付的契约核心与当前样本。12 字节帧的字节序、长度、分片和关闭由 Runtime/Host 探针验证；Task/PlanRevision/writeContext 的生产引用、实际游戏加载、完整能力目录、普通权限和客户端实测仍保留原任务。OpenSpec 任务由主代理按合并后的真实证据更新，本目录不维护第二份完成清单。
