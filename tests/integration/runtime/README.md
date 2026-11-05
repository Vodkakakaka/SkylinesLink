# Runtime 边界探针

本项目在 Windows .NET Framework 4.8 上执行与 Mod 共用的连接源码，不加载游戏程序集，不读写玩家档案。通过结果不能代替游戏 Mono 实载、游戏主线程调度或城市操作验收。

根目录统一还原依赖后：

```powershell
dotnet build tests/integration/runtime/SkylinesLink.RuntimeProbe.csproj --no-restore
pnpm test:runtime
```

`--module-runtime-probe` 单独检查注册模块观察的纯值、共享采样/消费者、分页、固定寿命与真正publisher结束；完整`--self-test`也包含它。两者均不调用Unity采集器，不证明真实模组列表或Mono表现。

自测覆盖加载世代、原生加载互斥、有界帧与连接、当前 logon SID ACL、持久用户权限存储、双向认证及离线原生提交端口。授权夹具实际经过 ack、排入提交队列、撤权落盘和运行提交回调，确认原生端口未被调用。新增单 Load 持久切片使用真实受保护文件和 Flush，仍只有合成原生端口。另用少量隐藏直启子进程核排他权、崩溃后持久 epoch 和未知原生任务的宿主寿命，完成后释放自身夹具。输出保留 `isElevated`；管理员测试不代表普通权限通过。探针没有 Shell 或普通权限模拟入口。

`--serve` 用于跨语言与 Launcher 集成。第一行 stdin 提供测试配置：

- `privateDirectory`：独立环境的 connection 目录。首次明确创建时目录应尚不存在，其父目录应已存在；既有目录缺失授权文件不会重新初始化。
- `environmentId`、`profileId`、`coreManifestSha256`：公开身份与候选核心哈希。
- `internalToken`：32 个随机字节的小写 hex；`externalHttpToken` 为独立凭据或 null。秘密只由 stdin 传入，不能放在命令行或日志。
- `initialize:true`：仅为明确的新夹具创建空配置和只读 profile；已有配置保留持久授权，不自动恢复默认权限。
- 可选 `deadlineMilliseconds`：默认 1500 ms。
- 可选 `operationProbe:true`：仅在首次建立这个独立夹具时给合成 profile Read|Control，并启用下面的私有 stdin 操作控制；不增加 MCP 工具。

ready 只输出公开发现身份、所选 grantEpoch、运行时及当前权限条件。随后支持 `metrics`、`revoke`、`rotate`、`load-generation` 和 `stop`；EOF 同样停止。加载与城市身份均为合成夹具。发现与授权位置固定为目录内两个 JSON 文件，单一操作账是同目录 `operations.journal`，独立历史发布器生成有界 `operations.history`，pipe 名由接入 owner 生成。既有目录缺失操作账返回不可用，不创建健康空账掩盖缺失。

`operationProbe` 的有限 JSON stdin 控制为 `{command:"context"}`（返回 writeContextId），以及 `{command:"submit",writeContextId,requestId}`（返回 operationId）。`submit-lost-ack` 写入同一真实 accepted 记录后仅返回 ack-suppressed；两个客户端随后用 `operations.get` 修订 4 按原请求键或 ID 查询同一记录。输入使用 operation-query:3，输出使用 operation-lookup:3，回显 selector 并区分 live/history；旧 schema 保留原文，活动能力不再接收旧 revision 3。合成提交目标固定；这些控制不调用原生加载、不开公开写能力。

握手明确消费 v2：双方先交换带身份绑定的 fresh nonce/HMAC 证明，长期内部凭据不发送到管道。旧 v1 schema 保留作契约历史，当前接入不回退。共用完整握手绝对期限；每个连接只允许一次挑战。普通读取按Read授权；精确`tasks.stop`经独立Control入口受理，不能经通用读取执行。其他公开游戏写入及普通Mod生产接入仍待原任务验收。

探针预算为最多 4 个连接、64 KiB 帧/授权文件、64 profiles、4 KiB 发现记录及有限绝对期限；这些是当前边界实验预算，不是最终性能承诺。私有文件按用户 SID 保护，管道按当前 logon SID 保护。停止先拒绝新调用，待本次持久 writer 与真实 pipe completion 均结束后才释放环境排他权。

`ConnectionAuthorityOwner` 分为后台 `CreateAsync` 和单次 `ActivateAsync`：前者取得排他权、恢复授权与操作账，但不监听、不发布发现；后者建立管道后发布发现。既有 `OpenAsync` 是两步的兼容包装。`GameConnectionLifecycle` 只协调这一个 owner；`StartAsync` 在后台调用整个工厂，随后 attach/activate，不让游戏线程等待目录、ACL 或磁盘 IO。游戏的 IMod 创建过程可能跳过构造器和字段初始化，实际 OnLoad 必须显式 `new GameConnectionLifecycle(...)`。工厂应把尚未取得原生证据的 context 先标为 unavailable；游戏线程观察者才可发布已核菜单/世界事实。

`StartAsync(): Task<bool>` 的 true 才表示实际激活完成，单纯 Task 已结束不表示 ready。失败的异常由生命周期观察，`Read()` 仅保留类别与异常类型，不回显消息或机器路径。`RequestStop()` 立即让授权与 context 失效，不等正在执行的真实发布 IO；实际 IO 仍可能暂时写出发现文件，但不能重新授权或变成 active。`CloseAsync(): Task<bool>` 等初始化、激活、撤发布与真实资源关闭；最终关闭之后没有后继 Activate/Publish。重复启动/关闭复用各自任务，停止之后不能重启这个对象。Close 返回 false 时须读取状态：未知 native 可能仅完成 IO 关闭而仍保留环境 lease，不能报告 fully released。

生命周期夹具用真实受保护存储和有界暂停的发布端口覆盖工厂尚未返回、初始化中停止、发布 IO 中停止、停止后迟到写出、初始化/激活失败、重复停止，以及正常激活后关闭。迟到发布测试要求 grant 当场失效、lease 在 IO 完成前仍排他，Close 完成后发现已撤回且不能再激活。已知 native Task 关闭等待实际终结；未知 native 的隐藏子进程经过同一生命周期，确认 IO 结束不释放物理 fence，同进程不能接管。线程池可复用已经让出的调用线程，所以非阻塞证据来自真实暂停期间 Start/RequestStop 已返回，而不是线程编号不同。以上仍是 Framework 离线证据，不代表游戏 Mono 的实际加载/卸载已通过。

真实帧读写共用最多 4 个 UTF-8 payload 数组、256 KiB 自有数组容量的准入额度；申请失败立即关闭该连接，不排等待者。分配前取得容量，唯一 owner 可以转交，退役不回收仍在发送的数据；每个数组最多 4 个 reader，同一个 reader 不并发执行 IO。`metrics` 的 `ownedPayloadCount` / `ownedPayloadCapacity` 及对应 `peak...` 只统计这些数组的实际 capacity。12 字节帧头、JSON 字符串/对象、CLR 堆、管道内核缓冲和 Host/SDK 复制不包含在该数值中；`privateBytes` 是独立进程观察，不能与容量账相加。停止后的两个 ownedPayload 当前值应归零，归零不等于 CLR 已完成垃圾回收。

局部所有权夹具验证外借数组复制后不受原数组修改影响、复制重叠期间双份计费、转交旧 owner 失效、读者限额、取消/退役后真实 IO 完成才释放、失败清理、超大对象拒绝，以及 FrameCodec 的共享额度拒绝在载荷读取/写出前生效。这仅覆盖 2.16 的 managed 载荷子链，不证明游戏 Native/Job、图像捕获、SDK 完整峰值或游戏内外计算性能比较。

`CopyFrom` 只为新副本取得容量；上面的双份计费由夹具另外持有 scratch 的源租约来证明。发送端仅支持遵守 IO 契约的受信 Stream：不改写输入，并在异步完成后停止使用数组。它没有把任意同进程自定义 Stream 隔离为不可信代码。

加载探针使用真实但完全受控的 CLR Task，不调用游戏。Runtime `NativeLoadGate` 是唯一产品加载排他所有者，`WorldContextTracker` 不再提供手动 Begin/End 放锁入口。只有实际 Task 终结或明确原生尚未调用的 NotStarted 回执释放槽；收到就绪通知、Host 等待取消、观察截止、玩家进入另一个代次和停止新接入均不能提前释放。提交过程抛出异常且没有可靠 Task 句柄时，提交事实保持 Unknown，产品后续加载仍被拒绝；本轮没有推测性的自动重试或解除未知 API。实际 Task 的 Faulted/Cancelled 分别保留，均不意味着已回滚或目标就绪。

实际 Load 句柄必须为原生调用返回的 `Task<bool>`。`physicalTaskState` 区分 notStarted/running/ranToCompletion/faulted/cancelled/unknown；`loadReturnValue` 独立保留 true/false，尚未正常返回时为 null。false 是正常终结的已知负返回，释放物理槽并使操作 failed，但不伪造 Faulted、Unknown 或未提交，也不据此把 mayHaveApplied 改为 false。原生回执没有可信目标时 boundTarget 可为 null，不能填入请求目标冒充绑定。Application 的 `Task<NativeLoadCompletion>` 仅通知普通事实，不持原生句柄，也不能由客户端取消。

Application `LoadTransition` 只归属一个 Operations 记录，保留有界普通值证据，不创建另一个 journal、计时器或全局查询表。它分别呈现原生终态与返回值、目标身份/内容版本、当前代次与历史因果；可信提交版本、可信关联通知、实际 Task 正常终结且返回 true 同时成立才闭合历史完成。显示城名、同资产 GUID、旧代次 ready 或通知晚于调用都不能替代这些证据。B 历史完成后进入 C 保留历史，旧 B 通知不能把 Tracker 回写为当前 ready；Task 先终结但目标未证时允许释放物理槽，观察仍保持未知。

`INativeLoadPort.TryBegin` 的消费者必须先经过 Operations 受理/提交守卫，并处于已验证的正常原生提交线程与屏障；本类不重复实现授权、当前世界重核或游戏线程调度。它不是可由任意线程/客户端直接调用 GameManager.Load 的公开入口。

已静态确认原生菜单经 `SaveLoadGame` 队列调用保存/加载，队列 cap 满可能不运行 delegate 而正常结束，runner 也可能记录并吞掉内部异常。因此 queueTask/代理 TCS 不是 actual native Task；未来适配必须在真实 delegate 进入时重新核提交条件，再取得实际 Task<bool>。当前仍未装配这个原生调度端口。另已确认 GameManager.Save 的 bool 即使内部 saveSuccess=false 也可能为 true，保存须独立结合本次结束事件及产物身份/完整性，不能复用 Load 的返回值解释。

该夹具覆盖菜单无 World、同城不同保存内容的逻辑代次、双客户端竞争、等待取消、B/C 交错、同步已完成/失败/取消 Task、内容绑定缺失及提交未知。它未验证游戏 World 对象、GameManager Task 与每次通知的真实时序、玩家加载起点覆盖、订阅释放、内容版本绑定时的文件变化、游戏线程/保存屏障或禁用模组后的恢复；真实原生端口没有装配，2.12 仍未整体完成。

单 Load 持久切片由同一个 `OperationAuthority` 持有 writeContext、去重和查询索引；`OperationJournal` 只有一个后台 writer。正常顺序为 accepted → commitIntent → nativeTerminal → terminal；明确尚未调用原生或无法取得可靠句柄时使用对应的有限分支。commitIntent 保存冻结意图，真实绑定来自原生回执，恢复时按顺序、绑定目标、原生返回值和终态事实组合复核。操作先分配 ID，再等待真实 Flush；同键不同参数拒绝，同键相同参数先查已有记录，不因容量或 context 失效变成新提交。`operations.get` 修订 4 消费同一记录，当前同环境 Read profile 可以读取脱敏事实。

新写入使用 operation-journal-record:2 与 SLJO frame version 2。旧/混合格式明确标为 JOURNAL_INCOMPATIBLE，查询不可用、拒绝新写、保留原文件，不猜测旧 Task 成功是否等于返回 true，也不自动重建、迁移或混合追加。真实 nativeTerminal 已记录 false 而收尾记录缺失/确认失败时，恢复仍保 failed 与原始 false；恢复后的整体投影可以是 volatile，不把已知负回执变为 unknown。跨记录试图把 false 改为 true 的成功终态被拒绝。

当前索引区分 found、初次未确认的 pending、健康索引在查询瞬间未记录的 absent、已知失效 context 的 expired，以及无法确认的 unavailable。操作执行事实与 durability 分开：最终 Flush 失败不会抹掉活实例已证实的“未进入原生”或“完成”事实；另一个操作的失败也不会降级已持久完成记录。恢复读出合法前缀后在后台 IO 初始化路径再次 Flush(true) 确认已有字节，不修改或截断它；失败保可核事实但 durability 为 unavailable，缺失查询及新写不可用，普通 context 读取仍可用。损坏或截断尾部使未完成结果保持 unknown，禁止新写且缺失查询不可用。缺失文件不会自动初始化，无自动重放。

实验额度为 32 条保留操作、4 条活跃操作、最多签发 8 个 writeContext（本轮不回收，各最多 8 请求，受理期不超过 60 秒）；单 payload 4096 字节、单帧 4140 字节、总 journal 1 MiB / 129 帧。受理预留每操作最多四帧磁盘账、一个可复用普通 stage 槽及两个收尾槽；普通槽 4、收尾槽 8，包含实际正在 Flush 的工作。容量准入只读内存中的已占字节与预留；初始化读取一次文件长度，此后唯一 writer 在成功 Flush、释放实际 frame 后同步推进已占字节、序列并归还 credit，不在准入锁内访问共享 FileStream。失败使 writer 不可用，不把未知写入长度猜成可复用空间。没有等待入队的无界集合。49,680 字节是这些帧的逻辑预留上界，不是总堆或磁盘空间保证。满额拒新请求，保原操作查询及收尾；没有 GC、rotation、迁移或无限增长。

新增 physicalTaskState/false 字段后的最坏转义 nativeTerminal 与最小终态通过实际编码确认仍在 4096 payload 内。此结论不表示所有 schema 合法的 accepted 字段组合都能装入一帧；受理在入索引/追加前实际编码验证，超额拒绝并归还预留，不截断标识或删除已知事实。

Load 目标及关联标识的长度遵循契约的 Unicode scalar 计数，不把 UTF-16 code unit 数量当作字符数，也不额外禁止契约允许的空白。实际日志恢复覆盖 65/128 个非 BMP 标量及空白身份的原值往返；129 标量、孤立高/低代理项拒绝。慢 Flush 夹具使用受保护真实文件和会拒绝 Length 访问的 FileStream 子类，确认并发容量准入不触文件流。

关闭先停止接入与新提交，真实 Task 尚未结束时异步等待它，观察截止不提前释放原生槽或收尾 credit。提交后失去可靠 Task 的 Unknown 仍派生宿主 fence：同一 journal 内的进程 ID 和精确创建时间用于判断旧宿主是否确实结束，新的随机 instanceId 不构成该证明。未知关闭结束 IO 后 `FullyReleased=false`，只用一个 GCHandle 固定环境 lease 到进程退出，重复 Close 返回同一任务，不固定服务器、授权快照或城市对象。操作索引继续是唯一事实来源；这个资源根没有权限或独立状态表。旧进程退出只消除物理阻塞，不把历史 unknown 变为成功或可重放。未来 World 写入口必须消费同一加载屏障；本切片不实现其他领域写入。

持久夹具实际覆盖慢 Flush 仍只一个 writer、丢 ACK 按原键查询、撤权后原生调用数为零、结果落盘失败的两类已知事实、观察截止先于实际 Task、坏尾与缺文件、跨记录绑定/终态矛盾、32 操作完整 129 帧、最坏转义标识的最小终态大小，以及不同 Read profile 读取同一历史。隐藏子进程验证未知关闭保 lease、同进程不能接管，进程确实结束后新 owner 可恢复历史。普通用户权限、真实游戏提交线程/Task/通知、模组卸载时 CLR/Mono 的根与句柄寿命、文件内容版本的原生关联仍未验证。不能据此勾选 2.12 或 3.1—3.4 整体完成。

普通 Mod 的 OnLoad 不启用接入。专用 `SKYLINESLINK_INTEGRATION_PROBE` 构建可装配 `Native/MenuProbeSession`，仅读取固定私有配置、创建全新连接环境和随机 Read profile；实际 EnvPath 必须与明确期望的现有玩家数据路径相符。这个路径核对不证明存档物理隔离。配置没有 token 或 Control 字段；普通构建没有这个探针入口。

菜单探针只有一个原生 updater：发布菜单/加载/不可用普通值，保持本次运行的后台、vSync 和 30 帧标量，验证本 PID 的有限窗口已隐藏。它不改 settings 对象、不进入城市、不调用原生 Load。所有配置、状态文件和连接初始化均在后台；状态最多每秒采样一次，单 writer 合并待写快照。停止先解绑观察者，恢复仍由本探针持有的临时渲染值并保窗口隐藏，再异步关闭资源。配置时限到达时仅在仍为已核菜单且不在加载时，资源真实关闭后经下一帧正常 Quit；Dispose 或玩家切换会阻止迟到退出。最后摘要的 `quitScheduled` 只证明安排，实际进程退出须独立观察；关闭完成后不再启动摘要 writer。普通权限及游戏内实际生命周期仍须实载证据，离线自测不覆盖这些原生调用。

普通 Mod 构建仍要求完整官方工具链与后处理；普通构建不部署、发布入口拒绝。任务状态和实际验证限制统一记录在原 OpenSpec 任务及其关联验证文档。
