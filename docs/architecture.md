# Architecture

[English](#english) | [简体中文](#简体中文)

## English

SkylinesLink links useful game information, normal gameplay capabilities, user intent, planning and verifiable results. A C# game mod owns game access and operation facts. A separate TypeScript/Node.js Host exposes MCP over stdio and Streamable HTTP; authenticated Windows named pipes connect the components. The stable Launcher is the stdio entry point.

The neutral Contracts assembly defines shared values and wire contracts. Application owns use cases, operation state and domain rules. Infrastructure implements persistence and communication. Mod composes those parts with game-thread adapters and the game's lifecycle. The Host handles protocol and client presentation rather than implementing game rules. Generated contracts keep both languages aligned.

Reads, planning/preview and writes have distinct paths. Information includes identity, geometry, relationships, qualifications and operational evidence, with source, scope, freshness and completeness. Commands enter one operation authority with stable identity, durable intent and truthful outcomes. Unknown submission results remain queryable and are not automatically replayed. Task and interface views derive from the same facts.

The intended game UI has a Planning & Activity entry for longer plans/history and a Quick Tools entry for explicit player selection, regions and annotations. Public AI guidance is retrieved by task and version. Player preferences come from the current task. Background AI must not take the player's input, active tool, selection or camera.

Work and results have explicit owners, finite capacities and real cleanup lifetimes. Backpressure covers actual processing and IO, not just the caller's wait. Performance acceptance measures the game and all owned processes together. Game rules, funds, unlocks and native validation remain authoritative.

**Current implementation:** foundation components, ten declared tools, bounded connections, durable-operation/history experiments and desktop entry points. Three Host-local tools search/read bundled bilingual public guides and describe the exact schemas exposed by this connection. The guides are versioned product documentation, not city observations; descriptions are declarations, not runtime qualification. Handler composition checks exact capability/revision/effect/schema bindings. Local reads share request/output budgets with game reads and never fall back to a game call. Task Stop uses Control permission and a bounded transport lane; complete original decision reads use Read and explicit live/history sources. Production task creation/execution remains disabled, and accepted Stop metadata does not prove native work has ended. Complete game actions, game-mechanism/asset/mod knowledge, the game UI, player installation and full in-game performance acceptance remain work in progress. Offline checks are not game acceptance. Player packages are not yet released.

The module-runtime read port shares one bounded native capture and publishes separate immutable pages under the current profile identity. It retains loader facts without retaining native objects, and distinguishes a complete observation from a bounded prefix and missing fields. Consumer cancellation, actual capture completion, retained observations and output ownership have separate lifetimes. The ordinary connection composition has passed bounded module reads and population/treasury overview reads in a native new-city development session through both MCP protocol versions. An unconfigured runtime returns unavailable; complete installed inventories, provider adaptation, normal-user operation and full performance acceptance remain pending.

The current city overview revision adds native simulation-speed settings and game-clock facts to the same on-demand read path. Groups keep unavailable values and unknown refresh times explicit. Selected speed is not measured throughput or a pause cause; native calendar values are not a real-world timestamp. The clock comes from current ECS components and public time functions, without cross-load caches. Runtime qualification of this revision is tracked separately from its implementation and offline checks.

The building read uses that existing deferred request path: Application owns the bounded request and value projection, the Mod captures native facts on the game thread, and the single sender rechecks the current context and Read permission. It reads one exact object and at most 16 entries from each of three direct relation buffers, without a city scan, retained native views or an asset cache. Information-panel selection has its own scope witness from the native published-change signal; the witness owns only its subscription and proof, and cannot certify a previous load's cache. Missing selection proof does not disable exact-reference reads. A view or read does not invoke selection refresh, change tools or start construction. Local bounds and metadata have narrower meanings than world geometry, a quote or operational evidence. Exact-reference and published-selection reads have passed a bounded native fixture; that evidence does not establish general object coverage or simultaneous player/AI construction.

Planned city blueprints can keep a coarse overall structure and refine the area needed next by default; other explicitly requested areas can also be detailed within the available budget. Detail levels are not fixed. Blueprint revisions, accepted execution segments, built facts and current suitability have separate meanings within the existing task/operation model. Reviewing an obsolete temporary facility or whole district must use current evidence and honor the task’s continuity requirements, including disclosed interruption and recovery steps when rebuilding on the same site; opening or closing a view does not start, cancel or retain native work by itself. These planning and UI capabilities remain unfinished.

## 简体中文

SkylinesLink把有用的游戏信息、正常游玩能力、用户意图、规划和可核结果链接起来。C#模组负责游戏访问与操作事实；独立TypeScript/Node.js Host通过stdio和Streamable HTTP提供MCP，组件间使用认证Windows命名管道。稳定Launcher是stdio接入入口。

中立Contracts程序集定义共享值与通信契约；Application负责用例、操作状态与领域规则；Infrastructure实现持久化和通信；Mod将它们与游戏线程适配及生命周期装配。Host负责协议与客户端呈现，不另写游戏规则。契约生成保持两种语言一致。

读取、规划/预览、写入分别走适用路径。信息涵盖身份、几何、关系、资格和运行证据，说明来源、范围、新鲜度与完整性。命令进入唯一操作权威，保稳定身份、持久意图与真实结果；提交未知可查且不自动重放。任务和界面投影来自同一份事实。

游戏界面计划提供“规划与记录”长页面入口及“快捷工具”入口，供玩家显式选择对象、区域和标注。公共AI指引按任务和版本取得，个人偏好来自当前任务。后台AI不能夺取玩家输入、活动工具、选择或相机。

工作和结果有明确所有者、有限容量与真实清理寿命。背压覆盖实际处理和IO，不因调用方结束等待提前退额。性能验收合计游戏与全部自有进程。正常资金、解锁及原生校验始终由游戏规则约束。

**当前实现：**骨架组件、十个声明工具、有界连接、持久操作/历史实验和桌面入口。其中三个Host本地工具检索/读取随包双语公共指南，并描述本连接公开能力的精确schema。指南是版本化产品说明，不是城市观测；描述是声明，不是运行资格。处理器组合核对能力/修订/效果/schema的精确绑定；本地读取与游戏读取共用请求和输出预算，失败不会回退到游戏调用。Task停止使用Control权限和有限传输通路，完整原决定读取使用Read与显式live/history来源；生产Task创建/执行仍未启用，停止元数据受理不证明原生工作已经结束。完整游戏操作、游戏机制/资产/模组知识、游戏界面、玩家安装及全面实机性能仍在开发；离线通过不代表游戏验收，目前尚无玩家发行版。

运行模组读取端口共享一次有界原生采样，按当前配置身份保留观察并返回独立小页。纯值中不保原生对象，完整集合、已观察前缀和缺失字段分开表达；消费者取消、实际采样结束、观察保留与输出持有分别管理寿命。普通连接装配已在原生新城开发实验中经两种MCP协议完成有界模块读取及人口/资金概览。未配置运行时仍返回不可用；完整安装目录、Provider适配、普通权限与全面性能继续待验收。

当前城市概览修订在同一个按需读取链加入原生模拟速度设置与游戏时钟，各组保留不可用值和未知刷新时点。选定速度不代表实测吞吐或暂停原因，原生日历也不是真实世界时间戳。时钟来自本次ECS组件和公开时间函数，不读跨加载缓存；本修订的实机资格与源码、离线检查分开记录。

建筑读取复用既有deferred请求链：Application管理有界请求和普通值投影，Mod在游戏线程采样，唯一sender在发送时复核当前上下文和Read权限。一次只读一个准确对象及三类直接关系各最多16行，不扫描城市、不保原生视图或新建资产缓存。信息面板选择以原生已发布变化事件建立独立scope证明，只保自身订阅和证明，不能认证前次加载的缓存；缺少证明不影响准确引用读取。查看或读取不会刷新选择、切换工具或启动施工。局部包围盒和元数据的含义分别小于世界几何、报价与运营证据。准确引用和已发布选择读取已通过有界原生夹具；该证据不代表通用对象覆盖或玩家与AI同时施工已完成。

规划中的城市蓝图可保留粗略整体结构，默认按近期需要逐层细化，明确要求的其他范围也可在预算内展开，不固定层数。沿现有任务／操作模型区分蓝图修订、已受理执行分段、已建事实与当前适用性。过渡设施或整区过时后的复评使用新鲜证据并遵守任务的连续性要求；已受理的同址重建保留停服影响和恢复步骤；打开或关闭页面本身不启动、取消或持有原生工作。这些规划及界面能力仍未实现完成。
