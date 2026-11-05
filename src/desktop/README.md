# 桌面入口与离线准备

Launcher 只读活动发行，直接启动同包 Node/Host 并管理本次进程树。Setup 承担文件准备。共享 Deployment 库仅解析和核验受限清单，不引用 Setup、下载器或游戏程序集。

## 标准 IO 的实际所有权

Windows 的 Node fd1/2 pipe 写可能同步堵住 event loop，`process.stdout.destroy()` 也不代表句柄关闭。因此客户端仍用稳定 Launcher 的 stdio 命令；Launcher 在启动前创建两条仅本用户、本次 child PID 可接入的异步管道，Node 经非秘密 nonce 连接这两个固定方向的字节流。它们不解析或改写 MCP，也不建立业务状态。Node 的三个标准句柄接 NUL，不从 stdout/stderr 旁路写日志；HTTP 进程组合另按原设计实现。

Launcher 两个固定 worker 各持 16 KiB 缓冲。外侧只接受 pipe，覆盖普通同步及 overlapped 两种创建方式；文件或控制台重定向明确拒绝。同步 IO 用本线程的 `CancelSynchronousIo`，异步在途用本请求的 `CancelIoEx`，均等真实完成和 Join 后才释放缓冲/OVERLAPPED。早于实际入 IO 的取消可能返回 1168，coordinator 不据此结束清理。Close/Dispose 共用首次关闭的绝对期限，不因重入延长。

当前实验期限为启动 5 秒、外侧单次写和关闭各 2 秒。EOF 沿官方 SDK 语义中止尚未回答的请求；仅给已进入内侧管道的字节有限转发机会，不承诺完整交付。输出超时结束本次 Node 进程树并回收自己的 IO，不停止游戏、不重放操作。健康收尾保 child 退出码；74 表示输出超时，75 表示清理未完整证明，76 表示启动接线失败，77 表示其他 relay IO/收尾失败。产品不通过可能阻塞的 stderr 打印任意异常；测试报告由独立 ProcessProbe 在实际返回后写入自有夹具。

依据：[Node 标准 IO](https://nodejs.org/api/process.html#a-note-on-process-io)、[Node 26.13 固定实现](https://github.com/nodejs/node/blob/v26.13.0/lib/net.js)、[Microsoft 取消同步 IO](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio)。这些取消 API 不是任意设备驱动的完成保证；当前只对本地管道实证。

当前命令供开发验证使用，尚不是完整玩家安装流程：

```text
SkylinesLink.Setup.exe verify-core <核心目录>
SkylinesLink.Setup.exe prepare-core <核心目录> <环境标识>
```

`prepare-core` 从持有读取句柄的源包，按清单白名单准备到统一用户根 `.skylineslink/environments/<environment>/releases/<version>`。它不写 `activation.json`、授权、客户端配置或游戏模组目录；返回 `prepared` 或 `already-prepared` 仅说明文件已准备。哈希一致不是发布者认证，完整 Mod/Node/许可及平台接受仍须另验。

## 准备的完成边界

1. 核源清单、路径、大小和 SHA-256，保持源文件只读句柄。
2. 固定目标祖先目录，独占该环境的 `setup.lock`。不取得、不等待连接的 `authority.lock`。
3. 已有正式清单时，复核全包及额外内容；同版本不同清单或损坏内容拒绝复用。
4. 尚无正式清单时，只接受空目录，或与已核源清单原始字节哈希匹配的 `preparation-manifest.json`。损坏清单、无清单但非空、未知文件、两个清单同时存在均拒绝猜测或删除。
5. 已完成文件校验后复用；半写文件先通过实际句柄检查普通文件、单硬链接，再重建并 `Flush(true)`。拒绝重解析点，不改变权限或截断外部硬链接目标。
6. 验证器借用同一个准备清单句柄，持有全部载荷的只读句柄。最后仅将该清单按句柄改名为 `core-manifest.json`，禁止覆盖；完成树检查后才返回。载荷目录从不改名，文件保护不在发布点松开。

两个清单名及其子路径都属于保留路径，不能由载荷清单声明。`CorePackage.OpenPreparing` 是受信 Setup 的借用入口，流的固定名称与独占、单链接身份由唯一调用点保证；它不认证任意外部流的文件归属，也不关闭借用流。普通 Launcher 不使用该入口。

该结构来自真实反例：完整目录改名与载荷 `FileShare.Read` 保护冲突，先放开句柄又会留下替换窗口，因此改为固定载荷目录、最后发布完成清单。目录存在不能代表安装完成；清单发布也不承诺断电后的持久顺序，后续启动仍需完整验证。

Windows 边界依据：[CreateFile 的访问与共享规则](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)、[句柄文件属性与链接数](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information)、[FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)。准备清单的目标字符串显式带终止 NUL，长度字段不计该 NUL。

## 已有离线验证

`tests/integration/desktop/launcher.test.mjs` 复用实际 Setup 入口，覆盖中文/空格路径、白名单、同版复用与冲突、半写恢复、损坏元数据拒绝、硬链接保护、额外文件保留。独立进程另验证清单发布前后无法改写被持有的文件或替换目录；Setup 短锁竞争明确失败，而连接长锁不阻塞准备。测试仅使用带归属标记的随机环境，结束后清理本次夹具。

18 项当前集成还覆盖官方 SDK 两代协议经 Launcher/随包 Node/产品 Host/net48、撤权、Job 创建时归属、正常 EOF/退出码，以及两种外pipe停读和停读后EOF。默认 ProcessProbe 对同一 relay 观察真实 worker Join、余留容量及 child 结束；NativeAOT 验证中的这些慢读场景改用实际本地构建的 Launcher exe，并核其实际非零退出和子进程树消失，不拿 Managed 探针替代 AOT IO 结果。调用 `pnpm test:desktop:aot` 前先 `pnpm build:aot`。

NativeAOT 的 PE CodeView 只保 PDB 文件名；`tools/verification/nativeaot.ps1` 使用 BCL PEReader 和实际字节核验，拒绝目录式 PDB 引用及当前本机仓库/用户目录前缀。PDB 是本地调试产物，尚未列入玩家包；该局部检查不替代正式候选的完整二进制/符号/许可脱敏。

缺少真实普通用户/干净机器、官方 Mod 制品、游戏与 Host 成套激活、启动器自身更新、自动游戏目录识别、双渠道切换及平台订阅交付证据。当前并行 Setup 返回占用失败；设置页重复点击返回同一准备操作的体验需随其实际调用端完成。没有创建后台更新服务。
