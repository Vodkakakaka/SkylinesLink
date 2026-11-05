# SkylinesLink brand assets / 天际互联品牌素材

Original identity and concept artwork for SkylinesLink. The vector files are marks and wordmarks; the separately listed city illustration is generated promotional concept art, not a game screenshot. They do not indicate affiliation with a game publisher or AI provider, or a registered trademark. Copyright © 2026 Vodka and SkylinesLink contributors; [MPL-2.0](../../LICENSE) and the [brand guidelines](../../BRANDING.md) apply.

本目录包含 SkylinesLink 原创矢量标识及另行列明的生成式概念插画，均非游戏截图，不代表游戏厂商、AI 厂商背书或已注册商标。版权、许可和品牌边界见以上链接。

## Files / 文件

| File | ViewBox | Use / 用途 |
| --- | --- | --- |
| [mark.svg](mark.svg) | `0 0 64 64` | Transparent mint/cyan mark for navy backgrounds; authoritative geometry / 深色底用透明双色标识，几何源 |
| [mark-monochrome.svg](mark-monochrome.svg) | `0 0 64 64` | Transparent single navy ink for light backgrounds / 浅色底用透明单色标识 |
| [app-icon.svg](app-icon.svg) | `0 0 64 64` | Navy square with the same mark / 同一标识的深蓝方形应用图标 |
| [wordmark-en.svg](wordmark-en.svg) | `0 0 400 112` | English primary, Simplified Chinese secondary; transparent / 英文主名、简体中文辅助，透明底 |
| [wordmark-zh-CN.svg](wordmark-zh-CN.svg) | `0 0 400 112` | Simplified Chinese primary, English secondary; transparent / 简体中文主名、英文辅助，透明底 |

| Raster file | Actual dimensions | Use / 用途 |
| --- | --- | --- |
| [app-icon-1024.png](app-icon-1024.png) | 1024×1024 | Large app/market icon candidate / 大尺寸应用与市场图标候选 |
| [app-icon-256.png](app-icon-256.png) | 256×256 | Small app/icon export / 小尺寸应用图标导出 |
| [wordmark-en.png](wordmark-en.png) | 800×224 | English vector raster export / 英文字标栅格导出 |
| [wordmark-zh-CN.png](wordmark-zh-CN.png) | 800×224 | Simplified Chinese vector raster export / 简体中文字标栅格导出 |
| [city-link-concept.png](city-link-concept.png) | 1774×887 | Original city-connection concept hero, not game output / 原创城市互联概念主视觉，非游戏输出 |

The icon and wordmark PNGs were technically rasterized from the SVGs with an existing headless Chrome renderer at device scale 1, without adding dependencies. A local contact sheet was visually inspected at native 16/24/32px sizes, single ink and both wordmark languages: the shape remained readable and the text was not cropped. This is artifact inspection, not an in-game accessibility or marketplace acceptance test. Dimensions above are chosen export targets or actual generated dimensions, not verified platform requirements.

图标和字标 PNG 使用已有 Chrome 无窗口栅格化，设备比例为1，未添加依赖。已直接检查16/24/32px原尺寸、单色与双语字标，形状可辨且文字未裁切；这不等于游戏内无障碍或市场接受验收。以上是自主导出目标或实际生成尺寸，不冒认平台强制要求。

The city hero was generated with the built-in image generation tool and inspected as concept artwork. The exact generation prompt and constraints are in [generation-prompts.md](generation-prompts.md). It contains no product UI, logos, text, or claimed gameplay results. Promotional layouts must keep the name readable and the artwork subordinate to the product identity; do not turn a dense city illustration into the 16px toolbar icon.

城市主视觉由内置图像生成工具生成并按概念插画检查，完整提示词及约束见链接。它不展示产品界面、第三方标志、文字或实机结果。宣传布局优先保证名称可读，不用复杂城市插画代替16px工具栏图标。

## Geometry and color / 几何与颜色

The two paths in `mark.svg` are the geometry authority. The stepped upper contour suggests the city; the uninterrupted lower bar connects both sides. The form has no small nodes or interior text. Derivatives reproduce those two paths and their stroke settings exactly, so standalone SVG consumers need no external file reference. Change the source and derivatives together.

`mark.svg` 中的两条路径是唯一几何来源：上部阶梯轮廓表达城市，下部连续横线表达互联。没有小节点或图形内文字。各文件保留完全相同的两条路径及描边设置，使单个 SVG 不依赖外链；调整时须同步源和派生文件。

- Palette / 色板: navy `#081B26`, mint `#4DE1B4`, ice cyan `#60C8E8`, light surface `#EDF4F4`.
- Stroke / 描边: 8 units, round caps and joins. At 16px, the mark uses a 2px stroke; at 32px, 4px. This is a geometric property, not a completed platform-icon acceptance result.
- Clear space / 留白: the mark's painted bounds are `x=8..56`, `y=12..52` inside the 64-unit square. Keep at least 8 units of additional clear space around the mark when placing it beside unrelated content. Do not crop the SVG to the painted bounds.
- Small sizes / 小尺寸: use the mark or app icon at 16–32px, not the wordmark. Wordmarks are intended for widths of 200px or greater.
- One ink / 单色: the complete shape survives in one ink; use `mark-monochrome.svg`, or change its one group stroke to `#EDF4F4` for a dark surface. Color is not required to interpret the form.
- Fonts / 字体: wordmarks use editable SVG text with installed-font fallbacks; no font binary or outlines are embedded. Appearance and text width depend on the renderer's fonts. Check the exported result for the target platform; do not claim pixel-identical typography across systems.

## Export without installing software / 无需安装的导出方式

SVG files can be consumed directly. Raster export is optional and does not change the source. No rasterizer dependency is added to the project.

可直接使用 SVG，不必先转 PNG；项目未新增栅格化依赖。已有 Chromium/Chrome 的环境可通过独立临时 profile 的 headless 模式渲染本地 SVG 或本地 HTML。`System.Drawing` 本身不能读取 SVG，不能把它当作 SVG 转换器；项目不要求 sharp/resvg。

For an existing Chromium browser, render a local HTML wrapper containing only the chosen SVG, with a fixed canvas and no margins. For a 256px icon, explicitly set the HTML/body and SVG to `width:256px; height:256px`. A small requested browser viewport may have a larger minimum layout width; percentage sizing alone can center and crop the SVG. Inspect the actual exported PNG, not just the SVG/contact sheet. Use a newly created private profile and an explicit output path:

```text
<existing-chromium-executable> --headless --disable-gpu --no-first-run --no-default-browser-check --disable-extensions --hide-scrollbars --user-data-dir=<new-owned-profile> --window-size=256,256 --screenshot=<explicit-output.png> <local-wrapper-file-url>
```

The placeholders are intentional: do not reuse the user's normal browser profile. The caller owns the temporary profile, wrapper, process, and output. Render locally without external resources, wait for the real process exit, inspect the PNG, then remove only the temporary items created for that export. Use `--default-background-color=00000000` when exporting a transparent asset; keep the navy background contained in `app-icon.svg` for the app icon. See the official [Headless documentation](https://developer.chrome.com/docs/automation-and-testing/headless) and [screenshot/background switches](https://github.com/chromium/chromium/blob/main/components/headless/command_handler/headless_command_switches.cc).

占位参数必须换成明确路径，不能使用玩家正常浏览器 profile。调用者负责本次临时目录、HTML、进程与输出；页面不引用外部资源，等待真实退出并检查图片后，仅清理本次临时材料。透明素材导出可用 `--default-background-color=00000000`；应用图标使用其 SVG 自带深蓝背景。

Repeat the small-size and wordmark inspection when geometry, typography or the renderer changes; target-platform contrast/scaling and actual upload acceptance remain release checks. The source SVG alone does not establish marketplace acceptance.

几何、字体或渲染器改变后重查小尺寸和双语字标；目标平台对比度/缩放与实际上传接受仍属发行检查。SVG 源文件本身不代表模组市场验收。
