# 显式目标的游戏菜单只读验证

`readonly-client.mjs` 不启动游戏、Steam、窗口或 AI 客户端，不修改客户端配置，也不注册、提交或重放任何游戏写操作。默认没有目标即拒绝。只在主代理已确认专用菜单探针 Ready 后执行：

```powershell
node tests/integration/game/readonly-client.mjs --target <私有配置绝对路径>
```

私有 JSON 放在 `.local` 等不提交的位置，且只能包含下列字段；不放 token、密钥、玩家存档路径或客户端认证信息：

```json
{
  "environment": "explicit-probe-environment",
  "profile": "Explicit.Read.Profile",
  "coreManifestSha256": "由本次发行或明确探针提供的64位小写十六进制hash",
  "expectedGamePID": 12345,
  "expectedGameStartTimeUtc": "2026-11-01T00:00:00.0000000Z",
  "productLauncherPath": "C:/absolute/SkylinesLink.Launcher.exe",
  "readySummaryPath": "C:/absolute/private-menu-status.json"
}
```

`readySummaryPath` 可省略，届时不声称游戏侧连接/载荷计数已回零。其余字段必填，`productLauncherPath` 必须是绝对 `.exe` 路径；旧 `productHostPath` 和 `nodePath` 明确拒绝。不能从其他环境或旧发现记录补默认值。进程时间必须是 Windows `Get-Process.StartTime.ToUniversalTime().ToString('o')` 的精确值，防 PID 重用。环境必须已规范为小写，profile 大小写保留。

运行前须由控制者明确准备该环境的完整匹配 Core 与 `activation.json`，菜单探针的 core hash 必须属于同一 Core；脚本不安装、激活或修改它们。预检复用产品有限文件读取边界和 generated `ActivationRecord` 验证当前 `USERPROFILE/.skylineslink/environments/<environment>/activation.json`，要求版本/hash 与目标一致，启动前及结束再次核对固定记录。Launcher 独立验证实际 Core 清单和文件，脚本的预检不替代其校验。

流程另核现有游戏进程名称、PID、创建时间；从同一固定环境的 `connection/discovery.json` 复用产品读取边界与单一契约，再固定 instance/epoch/core。脚本不读取 authorization。两个官方 SDK 客户端分别固定 2025/2026 协议，通过稳定 Launcher 的 `--profile/--environment` 入口使用已激活 Core：只核两个读工具、菜单 `world=null`、ASCII/Unicode 操作 ID 的健康 absent、未签发提交键的 unavailable、错误参数，以及结构化/文本结果同义。没有 core hash 命令行 override 或不匹配发行模式；错误 core、只读失败保原 selector/ID 且省略 `mayHaveApplied` 的证据继续保留在离线 Host 产品链/预算测试，不在实机环境伪造发行。

官方 stdio SDK 的 `close()` 会在 EOF 等待后强制清子进程，因此测试专用透明 relay 记录其直接自有 Launcher 的实际退出，回执为 `launcherPid`，不是 Node Host PID。1500ms EOF 观察期限内必须正常 exit 0、无信号、无 stderr；超时只清理这个自有子进程并判失败，不算 SDK 正常关闭。总输入、输出各限 1MiB，stderr 限 8KiB，单 relay 最长 25 秒；不通过 SDK 私有字段操作子进程。该 relay 不是产品组件，也不证明背压输出安全；真正 Native Launcher 的同步/overlapped 取消、Join 和背压由桌面测试另验。

可选状态摘要只接受主代理定义的公开事实，必须与真实进程、discovery、MCP 结果同时一致：`schemaVersion=1`、`productId`、`processId`、`processStartTimeUtc`、`userDataPathVerified=true`、`gameState=menu`、`world=null`、`targetFrameRate=30`、`vSyncCount=0`、`runInBackground=true`、`contextInstanceId`、`lifecyclePhase=active`、`windowHiddenVerified=true` 与四个连接/载荷计数。起始须为零，运行中须观察到两个已认证连接，结束回零；静态 startup 摘要不能充当动态资源证据，文件本身也不能代替 MCP 或进程事实。操作查询使用当前 capability revision 4 对应的 `OperationQueryV3` / `OperationLookupV3`，明确核 selector 回显及 live 实例。此入口尚未实机运行，必须使用匹配修订 4 的完整候选；不能把先前修订 3 的 AOT 候选重新标成当前构建。

正常 stdout 仅一条脱敏 JSON 结果，异常不打印任意 SDK、文件或子进程错误。前后核 Launcher 字节 hash 不变。通过只证明这一组官方 SDK → 稳定 Launcher → 同核产品 Host → 指定实载菜单读链，不代表真实 Codex/DSH、普通权限、实际 Load、城市写入或完整性能验收。

无需游戏的入口检查：`node --check tests/integration/game/readonly-client.mjs`、`--help`、无参数退出码 2。实际连接验证必须另等目标 Ready，不纳入默认离线测试入口。
