import { deepFreeze } from '../../../../contracts/ts/runtime.ts';

export interface PublicGuideTranslation {
  readonly guideId: string;
  readonly revision: string;
  readonly language: 'en' | 'zh-CN';
  readonly title: string;
  readonly summary: string;
  readonly bodyMarkdown: string;
  readonly capabilityRefs: readonly { readonly capabilityId: string; readonly schemaRevision: string }[];
}

const foundationRefs = [
  { capabilityId: 'system.context', schemaRevision: '2' },
  { capabilityId: 'operations.get', schemaRevision: '5' },
  { capabilityId: 'guidance.search', schemaRevision: '1' },
  { capabilityId: 'guidance.get', schemaRevision: '1' },
  { capabilityId: 'capabilities.describe', schemaRevision: '1' },
] as const;
const operationRefs = [
  { capabilityId: 'system.context', schemaRevision: '2' },
  { capabilityId: 'operations.get', schemaRevision: '5' },
  { capabilityId: 'capabilities.describe', schemaRevision: '1' },
  { capabilityId: 'tasks.stop', schemaRevision: '1' },
  { capabilityId: 'tasks.stop.get', schemaRevision: '1' },
] as const;
const gettingStartedRefs = [...foundationRefs, { capabilityId: 'tasks.stop', schemaRevision: '1' },
  { capabilityId: 'tasks.stop.get', schemaRevision: '1' }, { capabilityId: 'system.modules.get', schemaRevision: '1' },
  { capabilityId: 'city.overview', schemaRevision: '2' }] as const;
const moduleRefs = [
  { capabilityId: 'system.modules.get', schemaRevision: '1' },
  { capabilityId: 'system.context', schemaRevision: '2' },
  { capabilityId: 'capabilities.describe', schemaRevision: '1' },
] as const;

/** Public packaged text. Capability schemas and runtime evidence remain separate authorities. */
export const guideTranslations = deepFreeze([
  {
    guideId: 'getting-started', revision: '6', language: 'en',
    title: 'Getting started with the current build',
    summary: 'Discover the packaged guidance and declared capabilities, check the current connection, and distinguish the development foundation from a playable game integration.',
    capabilityRefs: gettingStartedRefs,
    bodyMarkdown: `# Start with what this build provides

SkylinesLink is in development. Read tools supply current observations only when their game source is available; inspect each result's scope and availability. This build supplies no construction, preview or execute tools.

## Discover before making a claim

1. Use the ordinary MCP tool list to find the available tools. Use \`guidance.search\` (\`skylines_guidance_search\`) to locate a relevant public guide, then \`guidance.get\` (\`skylines_guidance_get\`) to read it. Check the returned guide ID, revision and language. These guides are bundled product content; reading them requires no private AI skill or global prompt installation.
2. Use \`capabilities.describe\` (\`skylines_capability_describe\`) to inspect a declared capability's current input/output schemas and revision. A description is not a live city observation, permission grant or proof that a proposed action meets current game requirements.
3. Use \`system.context\` (\`skylines_context\`) when you need the connected instance and loaded-world identity. A verified ready world has a loading generation even if its persistent city ID is unknown. Bind current object references to the instance and generation; never invent a city ID from its display name, map or session. A menu or loading state is not a ready city; an unavailable connection supplies no substitute city facts.
4. Use \`operations.get\` (\`skylines_operation_get\`) only for an existing operation ID or original submission key that you actually know. Read the operation-status guide before interpreting uncertainty or history.
5. For a known task and its original control version, \`tasks.stop\` (\`skylines_task_stop\`) requests a task stop decision and requires Control permission. \`tasks.stop.get\` (\`skylines_task_stop_decision_get\`) reads that same decision with Read permission. Read the operation-status guide before interpreting a pending stop. Task creation and execution are not exposed in this build; a legacy authority reports \`TASKS_UNAVAILABLE\`.
6. Use \`system.modules.get\` (\`skylines_module_runtime_get\`) to observe the current process's module-loader registry when that source is available. Read the module-runtime guide before interpreting its coverage or paging the same snapshot.
7. Call \`city.overview\` revision \`2\` (\`skylines_city_overview\`) with \`{}\` and Read permission for population, player money, simulation-speed settings and the native clock in the currently loaded city. Bind the result to its instance and loading generation; the persistent city ID may be unknown. These observations are not a complete city survey. Capture frames describe when the values were read; \`sourceUpdatedFrame: null\` means source update times are unknown. When \`treasury.unlimited\` is true, its value is the game's effective money limit, not a finite balance. Preserve each group's \`unavailable\` and reason instead of substituting zero or discarding the other available groups. This read does not load a city or advance simulation.

The selected speed is a setting, and \`smoothSpeed\` is the native smoothed-speed observation. Neither a positive selected speed nor unchanged capture frames proves whether simulation is advancing now. \`selectedPaused\` means only that the selected speed is zero; \`pauseReason\` and \`advancingNow\` remain unknown (\`null\`). The native clock supplies its year, day/year fractions, days per year, epoch and evaluation frame. Preserve those values without inventing a conventional calendar date or treating the evaluation frame as the source's last refresh.

Reading context or operation status does not start the game, load a save, or operate the player's mouse or keyboard. The text in a guide, a declared schema and a successful MCP connection do not establish current funds, unlocks, installed content, game permissions or buildable geometry.

## Report the actual boundary

If the game is unavailable, say that current city information cannot be obtained through this connection. Do not present examples, earlier observations or guide text as the current city. Do not claim that a planned capability exists merely because a workflow mentions it conditionally.

The currently declared capabilities cover context, city population, player money, simulation-speed and native-clock observations, runtime module observations, operation status, task stop decisions and their status, guide search/read and capability description. A normal setup still uses the AI client's own MCP connection flow. These guides do not change that client's settings or guarantee that it will automatically read or follow them.`,
  },
  {
    guideId: 'getting-started', revision: '6', language: 'zh-CN',
    title: '从当前构建开始使用',
    summary: '查找随包指引与已声明能力，核对当前连接，区分开发基座与能够实际游玩的游戏集成。',
    capabilityRefs: gettingStartedRefs,
    bodyMarkdown: `# 先确认当前构建提供什么

SkylinesLink 仍在开发。读取工具只有在游戏来源可用时才提供当前观测，须核对每次结果的范围与可用状态；当前没有施工、预览或执行工具。

## 先发现能力，再作判断

1. 通过普通 MCP 工具列表确认可用工具。用 \`guidance.search\` (\`skylines_guidance_search\`) 查找相关公共指引，再用 \`guidance.get\` (\`skylines_guidance_get\`) 阅读，核对返回的指引 ID、修订版与语言。这些内容随产品提供，读取它们不要求安装私人 AI 技能或修改全局提示词。
2. 用 \`capabilities.describe\` (\`skylines_capability_describe\`) 查看已声明能力当前的输入、输出 schema 与修订版。能力说明不是当前城市观测，不授予权限，也不证明某项操作满足当前游戏条件。
3. 需要连接实例与已加载世界身份时，使用 \`system.context\`（\`skylines_context\`）。已验证就绪的世界具有本次加载代次，持久城市 ID 可以未知；当前对象引用绑定实例与加载代次，不能用显示名、地图或会话编号补造城市 ID。菜单或加载中不等于城市已就绪；连接不可用时不能补造城市事实。
4. 只有确实掌握已有操作 ID 或原提交键时，才用 \`operations.get\`（\`skylines_operation_get\`）查询。解释未知结果或历史信息前，先阅读操作状态指引。
5. 已知任务及其原控制版本时，用 \`tasks.stop\`（\`skylines_task_stop\`）请求任务停止决定，需要 Control 权限；用 \`tasks.stop.get\`（\`skylines_task_stop_decision_get\`）读取同一个决定，需要 Read 权限。解释停止待确认状态前，先阅读操作状态指引。当前尚未公开任务创建或执行能力；旧权威会明确返回 \`TASKS_UNAVAILABLE\`。
6. 来源可用时，用 \`system.modules.get\`（\`skylines_module_runtime_get\`）观察当前进程的模块加载器注册项。解释覆盖范围或读取同一快照后续页前，先阅读 module-runtime 指引。
7. 使用 Read 权限，以 \`{}\` 调用 \`city.overview\` 修订 \`2\`（\`skylines_city_overview\`），读取当前已加载城市的人口、玩家资金、模拟速度设置与原生时钟。结果绑定实例和加载代次，持久城市 ID 可以未知。这些观测不是完整城市调查；采集帧表示读取时点，\`sourceUpdatedFrame: null\` 表示来源刷新时点未知。\`treasury.unlimited\` 为 true 时，数值是游戏的有效资金上限，不是有限余额。保留每组的 \`unavailable\` 和原因，不能补零，也不能因此丢弃其他可用组；该读取不会加载城市或推进模拟。

选定速度表示设置，\`smoothSpeed\` 是原生平滑速度观测。选定速度为正或采集帧未变化，都不能证明模拟此刻正在推进或已经停止。\`selectedPaused\` 仅表示选定速度为零；\`pauseReason\` 与 \`advancingNow\` 仍是未知（\`null\`）。原生时钟提供年份、日内与年内比例、每年天数、纪元和求值帧；保留这些值，不补造通常日历日期，也不把求值帧解释成来源最后刷新帧。

读取上下文或操作状态不会启动游戏、加载存档，也不操作玩家的鼠标或键盘。指引文字、静态 schema 和 MCP 连接成功，都不能证明当前资金、解锁、已装内容、游戏权限或可建设几何。

## 如实说明当前边界

游戏不可用时，说明这条连接无法取得当前城市信息。示例、旧观测和指南文字不能充当当前城市；某个条件式流程提到未来能力，也不表示当前已经提供它。

当前声明的能力包括上下文、城市人口、玩家资金、模拟速度与原生时钟观察、运行时模块观察、操作查询、任务停止决定与查询、指引检索与读取、能力说明。正常接入仍沿用 AI 客户端自己的 MCP 连接流程；指引不会修改其设置，也不保证客户端自动阅读或遵循。`,
  },
  {
    guideId: 'operation-status', revision: '3', language: 'en',
    title: 'Read operation status, uncertainty and history',
    summary: 'Query the original identity, preserve real execution facts, and interpret live, retained and historical coverage without replaying an uncertain operation.',
    capabilityRefs: operationRefs,
    bodyMarkdown: `# Query the same operation

If the client does not already have the still-valid complete contract for \`operations.get\` revision \`5\`, use \`capabilities.describe\` (\`skylines_capability_describe\`) to read it. With that contract available, query directly without fetching it again for every call. Select either the known \`operationId\`, or the original \`submissionKey\` containing \`writeContextId\` and \`requestId\`. Preserve these identities across a timeout or reconnect. Status reads do not submit, resume, cancel or replay operations; this build provides no operation-submission MCP tool.

## Keep live and history separate

An omitted \`source\` means \`live\`. An explicit \`source: "history"\` requests published durable terminal records. A failed live request does not silently become a history request. History is a separate observation with its own producer, store, watermark and age; it is not the current city's state or permission to write. Integrity verification does not prove freshness. Use \`system.context\` separately when current instance/world identity is needed.

## Read facts together

- \`found\` means a record was found. Read its state, \`mayHaveApplied\`, durability and load facts together. Finding a record or seeing durable storage does not prove successful gameplay or a current observation.
- \`unknown\` and \`mayHaveApplied: true\` do not prove that nothing happened. A persisted native return value of \`false\` remains \`false\`; do not replace it with a guessed success or a claim that no effects were possible.
- \`pending\` is not a completed durable acknowledgement. A transport error, timeout or cancelled wait is not proof that the underlying operation was cancelled or failed.
- \`absent\` has the scope of that source and observation. An expired context concerns admission, not an undo of its accepted operations. Neither result grants permission to replay an old action.
- \`RETENTION_NOT_COVERED\` means the live store no longer establishes the requested historical identity. \`HISTORY_NOT_COVERED\` means the published history does not cover it. Neither means that the operation never existed, failed, or is safe to submit again.

After an uncertain reply, retain the original identity and query it again within a bounded wait. Preserve any proven completed prefix and unresolved effects. If the relevant source cannot establish the outcome, state that limit and obtain fresh relevant evidence through capabilities actually available. Do not invent progress, switch keys to force a retry, or substitute history for current facts. Cancelling a client's wait and cancelling a game operation are different events.

## Query a task stop decision

\`tasks.stop\` (\`skylines_task_stop\`, Control permission) takes the known \`taskId\` and \`expectedControlVersion\`. Keep that original pair after uncertainty and read \`tasks.stop.get\` (\`skylines_task_stop_decision_get\`, Read permission). Omitted \`source\` means live; explicitly choose history for retained published decisions. A failed request never silently changes source. The returned original submission key and journal facts remain evidence; do not invent a new stop identity or infer an absent decision from \`TASKS_UNAVAILABLE\` or \`HISTORY_NOT_COVERED\`.

A stop receipt records a task control decision. \`pending\` does not mean every action has stopped, and even a persisted decision does not undo effects or prove physical work has drained. It does not pause the AI model, the conversation or the whole game. This build exposes no task creation or execution tool; legacy authorities accurately report \`TASKS_UNAVAILABLE\`.`,
  },
  {
    guideId: 'operation-status', revision: '3', language: 'zh-CN',
    title: '理解操作状态、不确定性与历史记录',
    summary: '用原身份查询操作，保留实际执行事实，正确区分活动、保留与历史覆盖，避免重放结果不明的操作。',
    capabilityRefs: operationRefs,
    bodyMarkdown: `# 查询同一个操作

尚未掌握 \`operations.get\` 修订版 \`5\` 仍有效的完整契约时，先用 \`capabilities.describe\` (\`skylines_capability_describe\`) 读取；已有该契约即可直接查询，不必每次重复读取。选择确实掌握的 \`operationId\`，或包含 \`writeContextId\` 与 \`requestId\` 的原 \`submissionKey\`；超时或重连后仍保留这些身份。状态读取不会提交、恢复、取消或重放操作；当前构建没有操作提交 MCP 工具。

## 区分活动与历史来源

省略 \`source\` 表示 \`live\`；显式 \`source: "history"\` 才请求已发布的持久终态记录。活动查询失败不会静默改为历史查询。历史是独立观测，有自己的生产者、存储身份、水位和年龄，不是当前城市状态，也不授予写权限；完整性验证不证明新鲜度。需要当前实例或世界身份时，另用 \`system.context\` 核对。

## 联合读取事实

- \`found\` 只表示找到记录，应一起读取状态、\`mayHaveApplied\`、持久性与加载事实。找到记录或记录已持久化，不证明游戏操作成功，也不保证观测仍是当前状态。
- \`unknown\` 和 \`mayHaveApplied: true\` 不能证明什么都没发生。已记录的原生返回值 \`false\` 必须原样保留，不能猜成成功，也不能由此断言绝无影响。
- \`pending\` 不是已完成的持久确认。传输报错、超时或取消等待，不证明底层操作已取消或失败。
- \`absent\` 只对该来源与观察点成立；上下文过期涉及受理资格，不会撤销其中已受理的操作。两者都不授权重放旧动作。
- \`RETENTION_NOT_COVERED\` 表示活动存储已不能证明所查历史身份；\`HISTORY_NOT_COVERED\` 表示已发布历史未覆盖它。两者都不等于从未存在、执行失败或可以重新提交。

回执不明时保留原身份，在有界等待内查询同一个操作，保留已证实完成的前缀和未决影响。相关来源不足以确定结果时，如实说明缺口，再通过实际已提供的能力取得有关新证据。不要编造进度、换键强行重试，或拿历史冒充当前事实。取消客户端等待与取消游戏操作是不同事件。

## 查询任务停止决定

\`tasks.stop\`（\`skylines_task_stop\`，Control 权限）接收已知 \`taskId\` 与 \`expectedControlVersion\`。结果不明时保留这个原始组合，用 \`tasks.stop.get\`（\`skylines_task_stop_decision_get\`，Read 权限）查询。省略 \`source\` 表示活动来源；需要已发布的保留决定时显式选历史。失败不静默切换来源。响应中的原提交键和日志事实应原样保留，不能另造停止身份，也不能把 \`TASKS_UNAVAILABLE\` 或 \`HISTORY_NOT_COVERED\` 解读为决定不存在。

停止回执记录的是任务控制决定。\`pending\` 不表示所有动作都已停止；决定已持久化也不会撤销既有效果，更不能证明实际工作已经结束。它不会暂停 AI 模型、对话或整个游戏。当前没有任务创建或执行工具；旧权威会明确返回 \`TASKS_UNAVAILABLE\`。`,
  },
  {
    guideId: 'planning', revision: '4', language: 'en',
    title: 'Plan from evidence and the current user task',
    summary: 'Define neutral task constraints and a conditional evidence-to-result workflow without pretending that this build can preview or execute city construction.',
    capabilityRefs: foundationRefs,
    bodyMarkdown: `# Begin with the current task

Record the user's objective, functional needs, acceptable scope and any stated style, budget or layout preferences. Keep the source of each preference clear. Do not assume a private city's habits, a preferred road pattern, building style or a universal beauty score. Ask for a decision when a meaningful unresolved tradeoff changes the task; ordinary implementation details need not create repeated approval steps.

Use \`guidance.search\` (\`skylines_guidance_search\`) and \`guidance.get\` (\`skylines_guidance_get\`) for relevant method guidance and \`capabilities.describe\` (\`skylines_capability_describe\`) to inspect the capabilities actually supplied. Static guide text and schemas do not supply current city facts, current permission, funds, unlocks, installed content or action qualification. \`system.context\` supplies identity/state information, not a full city survey.

## Current limit

This development build has no city-construction, preview or execute tools. A city plan cannot be presented as a verified in-game preview or completed work through this build. Identify the missing observations or actions instead of inventing callable tools or game rules.

## Conditional workflow for future supplied domain capabilities

Only when the required domain reads, native preview, execution and result queries actually exist:

1. Obtain relevant current facts with their instance/world identity, scope, units, completeness and observation point. Distinguish direct observations, derived results, user preferences and assumptions.
2. Resolve the intended objects or region precisely. Check the requested shape, extent, orientation, height, connections and attachments rather than relying on a name or a rectangle alone. Compare a few useful alternatives when the task has a real tradeoff.
3. Check current normal game rules and qualifications through the corresponding trusted capability. Verify geometry, effects and costs in an actual supported preview; do not bypass validation or treat arithmetic or attractive concept art as an in-game result.
4. Execute only within the user's current scope and actual permissions. Preserve the plan and original operation identities. Use the supplied operation query, including \`operations.get\` where applicable, to resolve progress and uncertainty without replay.
5. Read back actual created objects, shape, connections and effects before calling the task complete. Report unverified effects and subjective player feedback separately from measured results. Revise the affected remainder when evidence requires it; do not discard a proven completed prefix.

## Keep the plan useful as the city changes

Keep distant areas at the level of purpose, relationships and constraints. By default, refine the near-term area and dependencies needed next; other explicitly requested areas can also be refined within the available budget. There is no mandatory number of detail levels. A coarse sketch is not a native preview. When domain capabilities become available, obtain the full required geometry, effects and cost checks for the part actually being built, rather than materializing the whole future city.

For important facilities or areas, retain why the current design was chosen, whether it is temporary or transitional, and any known review or replacement condition. Do not invent a missing rationale. Growth, unlocks, changed demand or observed problems may invalidate a formerly adequate layout. Preserve what was actually built and the original operation results; reassess current suitability separately. Revise the affected future work without replaying the completed prefix or silently changing an accepted action. If the impact is unclear, mark it for review.

When continuity is required, prove the replacement's actual service or connection before removing the old one. An explicitly accepted demolish-then-rebuild plan may allow an interruption, but its affected users, costs, protection and recovery steps must be clear. A higher-level facility does not automatically replace every distinct function of a smaller one.

For resuming work, retain the relevant upper-level objective and constraints, current area, built facts, open problems and next few steps. Do not require the full transcript or every distant detail for each call. Keep queries and geometry bounded; opening a planning view is not a request to simulate, build or continuously scan the city.

## Check the domain, not just the object count

These are checks to request from supplied capabilities, not claims that this build already supplies them:

- Roads: compare roles, declared growth assumptions, required movements, conflict and downstream capacity. Check real widths, curves, heights, merge alignment and generated lanes. Bus-only lanes must not silently remove the only ordinary-car movement. A compact option must still serve the same required directions; a dead end may already have a valid native U-turn. No intersection type or fixed lane count is universally best.
- Sites and buildings: compare the full main structure, attachments, entrances, resources, terrain and environmental effects together. Read actual effects, stacking conditions and costs instead of judging by names or levels. For accumulating services, distinguish stored amount, remaining capacity, processing or transfer rate, and emptying destination.
- Travel and industry: check relevant homes, jobs, schools, services, hubs and production destinations in the task's scope. Follow entrances, internal paths, interchange and dispatch through to actual arrival, processing or transport. Distinguish configured capacity from observed throughput; one valid path does not prove all demand is covered.
- Changes: preserve intentional open space and protected objects. Treat requested shapes, native candidates and built results separately. Apply current normal limits even when a low-level setter would accept more. Compare alternatives using compatible observations, and disclose missing evidence instead of assuming a complete survey.

Player actions can change the facts during work. Recheck affected evidence and qualification instead of taking over the player's mouse, keyboard, selection or active tool. Concurrent in-game isolation remains unverified in this build.`,
  },
  {
    guideId: 'planning', revision: '4', language: 'zh-CN',
    title: '依据证据与当前用户任务规划',
    summary: '建立中性的任务约束与条件式工作流，不把当前构建说成已经能够预览或执行城市建设。',
    capabilityRefs: foundationRefs,
    bodyMarkdown: `# 从当前任务出发

记录用户的目标、功能需要、允许范围，以及已明确的风格、预算或布局偏好，并保留各项偏好的来源。不默认套用私人城市习惯、某种路网、建筑风格或通用美观总分。只有未解取舍会实质改变任务时才请用户决定，不必为普通实施细节反复增加确认步骤。

通过 \`guidance.search\` (\`skylines_guidance_search\`) 与 \`guidance.get\` (\`skylines_guidance_get\`) 阅读相关方法，用 \`capabilities.describe\` (\`skylines_capability_describe\`) 核对实际提供的能力。静态指引和 schema 不提供当前城市事实、权限、资金、解锁、已装内容或操作资格；\`system.context\` 提供身份与状态，并非完整城市调查。

## 当前限制

此开发构建没有城市施工、预览或执行工具。不能把城市规划说成已经通过本构建完成了实机预览或建设；应列出缺少的观测或动作，不能编造可调用工具或游戏规则。

## 未来相关领域能力实际具备后的条件式流程

只有所需领域读取、原生预览、执行及结果查询能力确实提供后，才进入以下流程：

1. 取得有关当前事实，核实例与世界身份、范围、单位、完整性及观察点；区分直接观测、推导结果、用户偏好和假设。
2. 精确解析目标对象或区域，核对所需形状、边界、朝向、高程、衔接与附属，不能只凭名字或一个矩形判断。有实际取舍时比较少量有用方案。
3. 通过对应可信能力核当前正常游戏规则与资格，在实际支持的预览中检查几何、影响与费用；不绕过校验，也不拿数学计算或好看的概念图冒充实机结果。
4. 仅在用户当前允许范围和实际权限内执行，保留计划及原操作身份；使用实际提供的结果查询，适用时包括 \`operations.get\`，核进度与未知结果，不重放。
5. 回读实际生成对象、形状、衔接与效果，再判断是否完成；未验证影响、主观玩家评价和实测结果分别说明。证据要求修改时只修受影响的剩余部分，保留已证实完成的前缀。

## 让规划随城市发展继续有用

远期区域保留用途、关系和约束即可；默认细化近期区域及下一步所需依赖；用户明确要求的其他范围也可在预算内按需细化，不规定细节层数。粗略草图不是原生预览。相关领域能力实际提供后，对真正要施工的部分取得完整几何、影响和费用检查，不必先物化整座未来城市。

对重要设施或区域保留当前选择依据、是否临时／过渡以及已知复评或替换条件；不知道原理由就保留未知。增长、解锁、需求变化或实际问题可能使原先合适的布局过时。已建事实和原操作结果保留，当前是否适用另行评估；修订受影响的未来工作，不重放已完成前缀，也不静默改变已受理动作，影响不明则明确待复审。

任务要求持续服务或连接时，实际接替后才撤旧。明确受理的先拆后建可以允许中断，但须说明受影响使用者、费用、保护和恢复步骤；等级更高的设施不自动替代小设施的每一种独有功能。

接续时保留必要上层目标与约束、当前区域、已建事实、未解问题和接下来少量步骤，不要求每次调用读取完整会话或全部远期细节。查询与几何有界，打开规划页不代表要求模拟、施工或持续扫描全城。

## 核功能链，而不只数对象

以下是对实际提供的能力应提出的检查，不表示当前构建已经具备它们：

- 道路：按职责、明确增长假设、必要运动、冲突及下游容量比较方案；核真实断面、曲线、高程、分合流对齐和生成车道。公交专道不能悄悄挤掉普通车唯一必要方向；紧凑方案仍须承担相同方向，尽端可能已有正常原生掉头。没有普遍最优的路口类型或固定车道数。
- 场址与建筑：主体、附属、入口、资源、地形和环境影响联合比较；读取真实效果、叠加条件和成本，不只按名字或等级判断。积累型服务区分存量、剩余容量、处理／转移速率及清空去向。
- 出行与产业：在任务范围内核相关住宅、就业、学校、服务、枢纽和生产目的地；从入口、内部路径、换乘和派遣追到实际到达、处理或运输。配置容量与实测吞吐分开，一条路径通过不证明全部需求已覆盖。
- 修改：保留有意留白和保护对象，分别核请求形状、原生候选和实建结果。底层setter接受数值也须遵守当前正常限制；比较使用相容观测，缺证据明确说明，不把局部调查说成完整覆盖。

玩家操作可能在工作过程中改变事实，应重核受影响证据与资格，不夺取玩家鼠标、键盘、选择或活动工具。当前构建尚未验证游戏内并行隔离。`,
  },
  {
    guideId: 'module-runtime', revision: '2', language: 'en',
    title: 'Read the runtime module registry',
    summary: 'Capture and page one bounded loader observation, distinguish missing fields from false values, and preserve its instance, profile and coverage boundaries.',
    capabilityRefs: moduleRefs,
    bodyMarkdown: `# Observe the current process

\`system.modules.get\` (\`skylines_module_runtime_get\`, Read permission) observes executable entries registered in the current process's native module loader. It does not enumerate all installed or subscribed packages, prove which modules are enabled, list every asset pack, certify module health, or establish provider compatibility. Assembly names are the original loaded assembly identities, not guessed marketplace names. Database categories do not establish a store origin.

Use \`capabilities.describe\` if you do not already have this capability's current complete contract. Use \`system.context\` separately for current instance/world identity. A loader observation is not a city survey or a new permission grant.

## Capture, then page the same observation

1. Request \`{ "kind": "capture" }\`. Keep the returned \`snapshotId\`, \`instanceId\`, capture times, expiry and coverage together.
2. If \`nextOffset\` is non-null, request \`{ "kind": "page", "snapshotId": "<the returned ID>", "offset": <nextOffset> }\` on the same profile incarnation. Continue using that same ID; pages do not recapture the registry.
3. Each page contains at most 16 rows from an observation of at most 128 rows. The observation is retained for five minutes, subject to the game-side service lifetime. A newly published capture replaces that profile's prior observation, so old references become unavailable. Another profile cannot use those references; recreating the same profile name does not preserve its incarnation.

\`complete: true\` means the current registered sequence was exhausted during capture, not that every installation or initial load was covered. Read \`firstInitializationComplete\` separately; false does not diagnose a specific failure. \`restartRequired\` reports the loader's current flag and does not restart the game.

With \`TIME_LIMIT\` or \`ENTRY_LIMIT\`, the observation is a partial prefix and \`totalCount\` is unknown. \`nextOffset\` only traverses the captured prefix. A null next offset does not establish that no other modules exist. \`visited\` may exceed \`observedCount\` by one because a bounded lookahead or an unfinished row was not published. Do not merge pages from separate captures into one purported complete list.

## Interpret facts without filling gaps

Read raw \`loaderStateCode\`, its label, \`assemblyPresent\`, \`instanceCount\` and \`hasLoadError\` together. A present assembly does not prove successful initialization or working features. A future state code is \`unrecognized\`, not healthy. \`fieldIssues\` explains unavailable or invalid fields; a missing parent entry or asset covers its dependent null fields. An absent assembly naturally has no loaded name or version. Null is not false or zero.

\`assetGuid\` is an observed native value, not a stable marketplace package key or unique row identity. An ordinal identifies a row only within its snapshot. No filesystem paths, raw loader errors, credentials or dependency details are returned by this capability.

If unavailable, preserve its fixed reason. \`BUSY\` means the bounded service cannot accept that read now; \`REGISTRY_CHANGED\` means capture could not preserve the enumerated sequence. \`SNAPSHOT_UNAVAILABLE\` calls for a fresh capture; \`PAGE_OUT_OF_RANGE\` calls for checking the offset against the same observation. Cancelling a wait is not proof that native collection has ended. Reading this capability does not launch the game, change mods, load a save or take over player input.`,
  },
  {
    guideId: 'module-runtime', revision: '2', language: 'zh-CN',
    title: '读取运行时模块注册观察',
    summary: '捕获并分页读取同一次有界加载器观察，区分字段缺失与否，并保留实例、profile 和覆盖范围。',
    capabilityRefs: moduleRefs,
    bodyMarkdown: `# 观察当前进程

\`system.modules.get\`（\`skylines_module_runtime_get\`，Read 权限）观察当前进程原生模块加载器中已注册的可执行条目。它不等于全部安装或订阅目录，不证明模组已启用，不包含全部资产包，也不认证模组健康或 provider 适配。程序集名保留实际加载身份，不猜成市场名称；数据库类别也不证明商店来源。

尚未掌握当前完整契约时，先用 \`capabilities.describe\` 读取。当前实例和世界身份另用 \`system.context\` 核对；加载器观察不是城市调查，也不授予新权限。

## 先捕获，再读取同一观察的后续页

1. 请求 \`{ "kind": "capture" }\`，一起保留返回的 \`snapshotId\`、\`instanceId\`、捕获时间、期限与覆盖说明。
2. \`nextOffset\` 非空时，在同一 profile incarnation 下请求 \`{ "kind": "page", "snapshotId": "<返回的 ID>", "offset": <nextOffset> }\`。始终沿用同一 ID；分页不会重新捕获注册项。
3. 每页最多 16 行，一次观察最多 128 行，保留五分钟且受游戏侧服务存续期限制。新的 capture 成功发布后替换该 profile 的旧观察，旧引用随之不可用。其他 profile 不能使用这些引用；同名 profile 重新创建后也不是原 incarnation。

\`complete: true\` 只表示本次已遍历完当时的注册序列，不表示覆盖全部安装内容或所有初始加载。应另读 \`firstInitializationComplete\`；false 不能诊断某个具体失败。\`restartRequired\` 只是加载器当前标记，读取它不会重启游戏。

出现 \`TIME_LIMIT\` 或 \`ENTRY_LIMIT\` 时，只覆盖捕获到的前缀，\`totalCount\` 未知。\`nextOffset\` 只遍历这个前缀；next 为空不表示没有更多模块。一次有界探测或未完成行不会发布，故 \`visited\` 可比 \`observedCount\` 多一。不要把不同 capture 的页拼成声称完整的一份目录。

## 联合读取事实，不填补缺口

一起读取原始 \`loaderStateCode\`、对应标签、\`assemblyPresent\`、\`instanceCount\` 与 \`hasLoadError\`。程序集存在不证明初始化成功或功能正常；未来状态码标为 \`unrecognized\`，不能当健康。\`fieldIssues\` 说明字段不可用或无效；缺失父 entry 或 asset 已覆盖其依赖的空字段，不重复列问题。程序集不存在时，名称和版本自然为空；null 不等于 false 或零。

\`assetGuid\` 是观测到的原生值，不是稳定市场包键，也不保证行唯一。ordinal 只在该 snapshot 内标识一行。本能力不返回文件路径、加载错误原文、凭据或依赖详情。

不可用时保留固定原因：\`BUSY\` 表示有界服务暂不能受理；\`REGISTRY_CHANGED\` 表示捕获无法保住所枚举序列；\`SNAPSHOT_UNAVAILABLE\` 需要重新 capture；\`PAGE_OUT_OF_RANGE\` 应按同一观察核对 offset。取消等待不证明原生采集已经结束。读取不会启动游戏、改模组、加载存档或接管玩家输入。`,
  },
  {
    guideId: "building-inspection", revision: "1", language: "en",
    title: "Inspect one selected or precisely referenced building",
    summary: "Read native building, prefab and bounded direct relations while preserving scope, unknown cache age and the limits of metadata.",
    capabilityRefs: [
      { capabilityId: "buildings.inspect", schemaRevision: "1" },
      { capabilityId: "system.context", schemaRevision: "2" },
      { capabilityId: "capabilities.describe", schemaRevision: "1" },
    ],
    bodyMarkdown: `# Inspect one current building

The source remains \`SELECTION_SOURCE_NOT_READY\` until a native selection change is observed after this load. If the object was already selected before connecting, or the selection stays unchanged, the player may need to select another object once and then select the intended building. This tool never does that for the player.

Call \`buildings.inspect:1\` (\`skylines_building_inspect\`, Read permission) with \`{"target":{"kind":"selectedInfo"}}\` for the native information panel's last published selection cache. It does not refresh/filter/select, open the panel or take the active tool. Capture frames date this read; \`selectionSourceUpdatedFrame:null\` means the cache's last refresh is unknown, including after a load. The reply does not identify the instantaneous pointer target.

For a known object use \`{"target":{"kind":"entityRef","ref":{"instanceId":"<scope instance>","generation":"<scope generation>","index":42,"version":7}}}\`. All four fields are required. Returned object/prefab/road-edge/relation refs inherit the common scope. Add that exact instanceId and generation before reusing a relative ref. Names, coordinates or indices alone cannot identify an object across loads. Unknown cityId does not prevent a scoped read.

\`relationLimit\` defaults to 8, accepts 1 through 16 per group. subObjects/subNets/installedUpgrades report buffer presence, native length, returned prefix and completeness. Missing/failed is not empty. These direct buffers are not a complete facility or attachment tree: industry hubs may use other relationships. There is no retained snapshot or continuation; later calls are new observations.

Transform uses world metres and quaternion xyzw. prefabLocalBounds is ObjectGeometryData metadata in asset-local metres, not complete facility footprint or runtime world bounds. lotCells is declared lot cells. baseConstructionCost is prefab metadata in game currency, not a placement quote, installed total or refund. locked only reads the active Locked marker. RoadEdge/exists is a stored relation/entity-presence check, not an entrance, reachability, dispatch or operation verdict. Raw flags/masks preserve native fields. None establishes ownership, collisions, funds or normal placement qualification; PrefabSystem.IsAvailable is never called.

Preserve null and reason; oversized names/IDs are unavailable, never shortened. Native PrefabID does not prove installation or subscription. MENU/LOADING/NO_SELECTION/SELECTION_CHANGED/CONTEXT_MISMATCH/ENTITY_UNAVAILABLE/SCOPE_CHANGED remain distinct. ENTITY_UNAVAILABLE cannot distinguish disappearance from an old version. DELETED/PREVIEW/NOT_BUILDING describe observed unsupported targets. Under-construction is not automatically Temp. Never choose the nearest object or fall back to raw tool selection.

Reading or cancelling a wait does not build or change simulation. Actual collector/publisher work remains owned until finished. Synthetic tests do not prove a normal-city selectedInfo positive sample or player/AI coexistence; keep their runtime evidence separate.`,
  },
  {
    guideId: "building-inspection", revision: "1", language: "zh-CN",
    title: "检查信息面板选择或精确引用的单个建筑",
    summary: "按加载作用域读取原生建筑、资产和有界直接关系，保留缓存刷新时点、元数据口径与运营证据缺口。",
    capabilityRefs: [
      { capabilityId: "buildings.inspect", schemaRevision: "1" },
      { capabilityId: "system.context", schemaRevision: "2" },
      { capabilityId: "capabilities.describe", schemaRevision: "1" },
    ],
    bodyMarkdown: `# 检查本次加载中的一个建筑

本次读档后，先观察到原生选择变更才可读取，否则返回 \`SELECTION_SOURCE_NOT_READY\`。接入前已经选中，或选中对象一直没变时，可能需要玩家改选一次再选回目标；工具不会代选。

以 Read 权限，用 \`{"target":{"kind":"selectedInfo"}}\` 调用 \`buildings.inspect:1\`（\`skylines_building_inspect\`），读取原生信息面板最近发布的选择缓存。工具不刷新、过滤、选中、打开面板或接管活动工具。采样帧表示本次读取时刻；\`selectionSourceUpdatedFrame:null\` 表示缓存最后刷新时点未知，读档后也不能补造，不保证回复到达时光标下仍是该对象。

已有对象时，用 \`{"target":{"kind":"entityRef","ref":{"instanceId":"<捕获实例>","generation":"<加载代次>","index":42,"version":7}}}\`，四项缺一不可。返回的建筑、Prefab、路边及直接关系引用都相对于公共 scope，后续输入须补回该次 instanceId/generation；不能仅凭名称、坐标或序号跨读档复用。cityId 未知不阻止本次作用域读取。

\`relationLimit\` 默认 8，每组允许 1 至 16。subObjects/subNets/installedUpgrades 分别报告 buffer 是否存在、真实长度、返回前缀条数和完整性；缺失或失败不等于空。这三组不是完整设施或附属树，产业 Hub 可能通过其他关系连接。工具没有保留分页快照，再次调用是新观察。

Transform 使用世界米及 xyzw 四元数。prefabLocalBounds 是 ObjectGeometryData 的资产局部米制包围盒，不是完整设施轮廓或运行时世界包围盒。lotCells 是声明的地块格数；baseConstructionCost 是游戏货币计的资产基础元数据，不是场址报价、已装总价或退款；locked 只读 Locked 标记。RoadEdge/exists 只说明存储关系和实体存在，不证明入口、可达、派遣或运营；flags/mask 保留原生字段。以上都不证明内容拥有权、碰撞、资金或正常放置资格，也不调用 PrefabSystem.IsAvailable。

字段 null 与 reason 联合保留，超长名称/ID 不静默截断，原生 PrefabID 不等于安装/订阅结论。MENU/LOADING/NO_SELECTION/SELECTION_CHANGED/CONTEXT_MISMATCH/ENTITY_UNAVAILABLE/SCOPE_CHANGED 分别保留；ENTITY_UNAVAILABLE 不能区分消失和旧版本。DELETED/PREVIEW/NOT_BUILDING 是观察到的不适用目标；施工中的正式建筑不自动等于 Temp。不按最近坐标换对象，也不回退原始工具选择。

读取或取消等待不会施工或改模拟，collector/publisher 真实结束前仍保有责任。合成测试不证明正常城市 selectedInfo 阳性或玩家与 AI 并行，二者须保留独立实机证据。`,
  },
] as const satisfies readonly PublicGuideTranslation[]);
