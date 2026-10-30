# 模组市场与同步发布调研

调研日期：2026-10-30。已经核对官方入口、本机随游戏提供的模板、上传器帮助与指定发布逻辑；未执行账号登录、模组上传或订阅测试。

## 发布渠道

《城市：天际线 II》采用 **Paradox Mods** 作为官方模组平台。官方开发日志明确该选择，当前 Steam 商店功能列表也未提供 Steam Workshop 支持。不能套用一代的创意工坊发布流程，Steam 客户端处于打开状态也不等于 Paradox 发布账号已经可用。

来源：[官方平台说明](https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-1-paradox-mods)、[Steam 商店](https://store.steampowered.com/app/949230/Cities_Skylines_II/)、[Paradox Mods](https://mods.paradoxplaza.com/)。

| 渠道 | SkylinesLink 的用途 |
|---|---|
| Paradox Mods | 玩家订阅、启用及随平台更新完整模组，包含内置 MCP 所需的可分发依赖 |
| GitHub | MPL-2.0 源码、规格、问题反馈、相同版本的模组包、许可与校验信息；不成为玩家必需的第二次下载 |
| Steam Workshop | 不作为天际线 II 的官方模组发布入口 |

单订阅交付是确定要求。默认设计为游戏内 C# 模组直接提供 MCP HTTP 服务，不要求安装独立 Host、运行时或桥接工具。GitHub 与模组市场同步指维护者的一次发布流程，不是让玩家从两处安装。

## 官方代码模组上传路径

[官方 Code Modding 说明](https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding)给出的入口是游戏提供的模组模板和 Visual Studio / Rider 的发布操作。当前随游戏安装的工具链还提供命令行 `ModPublisher`，可供后续统一发布程序调用。

本机模板和上传器核实的三类操作：

| 用途 | 模板发布配置 | 上传器命令 |
|---|---|---|
| 首次创建模组条目 | `PublishNewMod.pubxml` | `Publish` |
| 已有条目发布新版本 | `PublishNewVersion.pubxml` | `NewVersion` |
| 修改已有页面资料 | `UpdatePublishedConfiguration.pubxml` | `Update` |

首次发布后保存平台返回的 ModId，后续沿用该条目。日常更新使用 NewVersion；Update 的实现针对元数据，不应代替二进制新版本发布。发布超时必须先核对平台状态，不能盲目重试 Publish 而创建重复条目。

`Properties/PublishConfiguration.xml` 支持名称、简介、长描述、缩略图、截图、标签、模组版本、支持的游戏版本、依赖、DLC、更新日志、讨论链接、外部链接和可见性。上传器校验简介、长描述、缩略图及版本等必需项；默认包含 Code Mod 标签。模板支持 GitHub 外部链接，以及 Public / Private / Unlisted 可见性；具体平台效果仍需真实上传验证，不能据此假定拥有服务端草稿或原子发布功能。

上传器递归收集内容目录中的文件；本地规则仅排除某些构建包，并不会替项目过滤所有敏感或不可分发文件。必须提供经过审核的独立制品目录，不能把项目根、游戏安装目录或带日志的开发输出直接作为上传目录。

## 账号、环境与审核

- 使用发布者的 Paradox 账号。当前上传器可以尝试复用游戏内登录，也暴露邮箱/密码或登录数据文件参数；本次只核查帮助和逻辑，没有读取账号凭据或验证登录状态。
- 不将密码放入命令行、公开 workflow、项目配置或日志。优先验证官方本机登录复用；不能复用时再设计受控认证存储，不以读取现有私密认证文件替代正常流程。
- 上传器以 .NET 6 为工具目标；本机用已有较新运行时的单次 roll-forward 成功显示帮助。这只证明帮助入口可运行，未证明完整登录、上传及 SDK 路径兼容。开发者的发布工具依赖不传给玩家。
- 官方声明上传模组会经过扫描，并保留管理和移除权；没有找到适用于本项目的固定审核时长或逐版人工审核承诺，不能保证即时上架。[扫描说明](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/traffic-breach-statement)、[适用用户协议](https://legal.paradoxplaza.com/eula)。
- 官方开发日志链接的 Wiki `Modding_Toolchain` 页面本次返回 401；模板、命令及元数据来自当前本机官方工具，未把无法访问的网页当作已读依据。
- 本次未找到明确允许随代码模组捆绑并启动任意外部 Host 的官方规则。内置 MCP 的方案不依赖该假设；上传器能收集某种文件不等于平台允许该行为。

## GitHub 与 Paradox Mods 同步设计

统一维护版本号、源码提交和一份更新日志。一次发行只构建一套可分发模组内容，两渠道使用同一份内容清单和逐文件校验值；平台重新压缩或加入平台元数据后，不能仅比较外层 ZIP 字节判断是否同版。

建议的单次发布流程：

1. 锁定源码提交与版本，构建、验证，并对全部公开内容和提交信息脱敏。
2. 创建或复用该版本的 GitHub Release 草稿，上传已经审核的模组包、校验清单、许可证及实际验证记录。
3. 通过官方 ModPublisher 创建首次条目或给既有 ModId 发布新版本；查询并记录平台的实际版本与可见状态。
4. 核实 Paradox Mods 版本后发布对应 GitHub Release，附模组市场链接；最后回读两处结果，确认同版。
5. 一侧失败或回执未知时记录已完成步骤和实际远端标识，核实后仅继续缺失步骤，不重建成功版本、不删除重建模组条目、不覆写同一版本的不同二进制。

两个平台没有共同事务，不能承诺严格同一瞬间上线；目标是同版、同内容、一次操作及失败后可继续。GitHub 的草稿能力有[官方 Release API](https://docs.github.com/en/rest/releases/releases#create-a-release)支持；Paradox 侧仍须以实际发布回执和可见性为准。

初期可在拥有合法游戏与官方工具链的 Windows 发布环境运行统一命令。需要 GitHub Actions 时再接受控发布环境，不将游戏程序集上传到公共仓库来满足云端构建，也不在带发布权限的本机运行任意外部 PR。当前没有安装 runner、设置凭据或启用自动发布。

## 首发前尚需完成

- 内置 MCP 在实际游戏运行时的完整依赖、监听与生命周期验证。
- 只装游戏和目标 AI 客户端的环境中，真实订阅后读取城市，完成无额外安装验收。
- 官方上传器实际认证、首次上传、NewVersion 更新及更新后订阅端加载验证。
- 两渠道同步、超时事实核对、失败续发及版本内容一致性验证。
- 按实际制品复核平台条款、MPL 源码获取、第三方通知、缩略图和所有公开附件的脱敏。

这些是明确的后续验证项，当前没有声称已经发布模组、占用市场条目或实现自动同步。
