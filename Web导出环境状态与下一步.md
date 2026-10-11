# UWPro Web 导出：环境状态与操作手册

> 更新：**2026-10-12** ｜ 引擎版本：魔改版 **4.7.2-stable**（ComplexRobot/godot-dotnet-web-export）
> 本文取代 2026-10-09 的旧版内容。**4.8-dev7 已被证伪，不可用**，原因见第五节第 2、3 条。
>
> **2026-10-12 的三条重要变化**：
> 1. **Web / 桌面两条流水线已统一到同一个 4.7.2 引擎**（见第七节），csproj 不再被来回改写；
> 2. **新增两条「Web 上绝对不能写」的代码红线**：.NET 全球化 API、以及从托管代码连接
>    `RenderingServer.FramePostDraw` —— 两者都会让 wasm 直接 `Aborted()`，**详见第八节**；
> 3. 卡组编辑的"卡死"已定位并修复（就是第 2 条那条信号），`pck` 现为 259.3 MB。

---

## 一、结论

**环境已齐全，并已实测「在浏览器里真正跑起来」**：主菜单、卡组编辑（698 张卡池 + 分页 + 构筑限制 + 卡图）均可正常渲染；卡组编辑 **240 fps / 180 节点 / 0 未捕获异常**。

一句话链路：`魔改版引擎 4.7.2-stable` → 导出 Web 预设 → 引擎自动 `dotnet publish -c ExportRelease -r browser-wasm`（**必须关裁剪**，见第五节第 1 条）→ 打成 pck → 由带 COOP/COEP 头的 HTTP 服务打开 → 运行时由 pck 里的 `GodotPlugins.Game.Main` 引导 mono。

产物（`build/web/`）：`index.html + index.js + index.wasm 53.6MB + index.pck 259.3MB`（合计约 313MB）。

---

## 二、本机环境清单（全部已实测）

| 组件 | 位置 / 版本 | 状态 |
|---|---|---|
| 魔改版引擎（**在用**） | `D:\Godot\Godot_v4.7.2-stable_mono_web_export_win64\` | ✅ 只用 `*_console.exe` |
| Web 导出模板 | `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\` | ✅ |
| NuGet 本地源 | `%APPDATA%\NuGet\nuget\`（名 `NugetSource`，含 4.7.2 四个包） | ✅ |
| .NET 9 SDK（用户级，**在用**） | `D:\dotnet9` = 9.0.318 + **wasm-tools 9.0.20** | ✅ |
| .NET 10 SDK（用户级） | `D:\dotnet10` | ⚪ 仅 4.8-dev7 需要，现已无用，可删 |
| 桌面引擎（**已统一到 4.7.2**） | **同一个** `D:\Godot\Godot_v4.7.2-stable_mono_web_export_win64\`（由 `GodotPath.txt` 锁定；原 4.7.1 备份在 `GodotPath.txt.pre-4.7.2.bak`） | ✅ |
| 4.8-dev7 魔改版 | `D:\Godot\Godot_v4.8-dev7_mono_web_export_win64\` | ❌ **不可用，见第五节第 2 条**，留作参考 |
| 工程配置 | `UWPro.csproj` = Sdk **4.7.2** / **net9.0**；web 分支 `PublishTrimmed=false` | ✅ |
| 打包字体 | `fonts/`（Noto Sans SC + Noto Emoji + DejaVu Sans，共 11.7MB） | ✅ 仅 Web 端启用 |
| 字体兜底脚本 | `Script/Web/WebFontFallback.cs`（`project.godot` 中注册为首个 autoload） | ✅ |
| Web 导出预设 | `export_presets.cfg` 的 `preset.1`（platform=Web，thread_support=true） | ✅ |
| 脚本 | `导出Web版.ps1`、`Web预览服务器.py`、`启动Web预览.cmd`、`检查Web包.py`、`Web运行自检.mjs` | ✅ |
| 回滚备份 | `*.pre-web-4.8.bak`、`*.pre-webfix.bak` | ✅ |

> 为什么有「用户级 SDK」：本机当前**不是管理员**，`dotnet workload install` 无法写入 `C:\Program Files\dotnet`（MSI 需要提权）。改用「下载 SDK zip 解压到 D:\dotnet10 + 在用户目录装 workload」，完全免提权。

---

## 三、一键导出

```powershell
cd D:\MYPROJECT\UWPro
powershell -ExecutionPolicy Bypass -File .\导出Web版.ps1
```

脚本会自动：定位魔改版引擎（**强制 `*_console.exe`**）→ 校正 csproj 的 Sdk/TFM → 把 `D:\dotnet9` 放到 PATH 最前 → 校验 Web 预设与模板 → `--headless --export-release Web ./build/web/index.html` → 清点产物。

等价的手工命令（便于排查）：

```powershell
$env:PATH = "D:\dotnet9;$env:PATH"          # 千万别设 $env:DOTNET_ROOT
mkdir D:\MYPROJECT\UWPro\build\web           # 目标目录必须先存在
& "D:\Godot\Godot_v4.7.2-stable_mono_web_export_win64\Godot_v4.7.2-stable_mono_web_export_win64_console.exe" `
  --headless --path "D:\MYPROJECT\UWPro" --export-release Web "D:\MYPROJECT\UWPro\build\web\index.html"
```

> 导出完成后 Godot headless **不会自动退出**，看到 `[ DONE ] savepack` 且产物齐全即可 `taskkill`。
> 导出后要自己补回 `build/web/_headers`、`.htaccess`、`web.config`（源文件在 `web_deploy_headers/`），
> 它们会被 `--export-release` 覆盖掉整个输出目录时删掉。

### 导出后自检（强烈建议）

```powershell
python .\检查Web包.py           # 解包 index.pck，核对 .NET 程序集有没有被裁、引导类在不在
```

实测过程中就是靠它定位到「`UWPro.dll` 被 ILLink 裁成 4096 字节」。

要连「浏览器里到底跑没跑起来」一起验，用无头 Edge + CDP：

```powershell
# 1) 起一个常驻的无头 Edge（必须带 --remote-debugging-port）
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" `
  --headless=new --remote-debugging-port=9222 --user-data-dir=C:\tmp_ep `
  --enable-unsafe-swiftshader "http://127.0.0.1:8080/index.html"
# 2) 采集控制台输出并截图（零依赖，只用 Node 自带 fetch/WebSocket）
node .\Web运行自检.mjs --wait 100 --shot shot.png
```

---

## 四、本地预览（打开方式）

### 为什么会报 Cross-Origin Isolation / SharedArrayBuffer missing

本预设开了线程（`variant/thread_support=true`），Godot Web 运行时依赖 `SharedArrayBuffer`。
浏览器只在页面处于**跨源隔离**状态时才允许这个 API，而进入该状态**唯一**的途径是服务端返回：

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

`file://` 协议下没有 HTTP 响应头 —— 所以**双击 `index.html` 必然报错，与导出是否成功无关**。
这不是能靠改导出选项绕过的问题：魔改版的 `web_release.zip` 里只有 `godot.wasm` 单份（**无 `nothreads` 变体**），
也就是它只提供线程版运行时，单线程导出这条路不存在。**结论：必须用 HTTP 服务打开。**

### 推荐：双击 `启动Web预览.cmd`

会依次完成：找 Python → 起本地服务（自动带 COOP/COEP 头）→ 自动打开浏览器。
端口被占用会自动顺延（8080 → 8081 …），不会打开错地址。可带端口参数：`启动Web预览.cmd 9000`。

### 手工方式

```powershell
cd D:\MYPROJECT\UWPro
python .\Web预览服务器.py --dir .\build\web --port 8080 --open
# 不加 --open 就自己打开 http://127.0.0.1:8080
```

> 首次加载要在浏览器里编译 56MB 的 wasm，**耐心等 30 秒以上**，中途白屏属正常。

### 线上部署需要什么

| 环境 | 做法 |
|---|---|
| itch.io | 项目后台勾选 **SharedArrayBuffer 支持**（文件夹上传为 zip，入口 `index.html`） |
| Netlify / Cloudflare Pages | 用 `build/web/_headers`（已生成，直接生效） |
| Vercel | 把同样两条头写进 `vercel.json` 的 `headers` |
| IIS | 用 `build/web/web.config`（已生成） |
| Apache | 用 `build/web/.htaccess`（已生成） |
| GitHub Pages | ❌ 不能自定义响应头（拿不到 COOP/COEP），且单文件 100MB 上限挡住 259MB 的 pck —— 不推荐 |

---

## 五、踩过的坑（重要）

按「坑有多大」排序，前三条是**导出能成功但跑不起来**的隐形杀手。

### 1. 🌟 `dotnet publish` 默认开 ILLink 裁剪 → 游戏代码被整段删掉

browser-wasm 的发布**默认 `PublishTrimmed=true`**。裁剪从程序集入口（`Program.<Main>$`）出发做可达性分析，
而游戏代码是引擎**用反射**进来的（`gd_mono.cpp` 里 `mono_class_from_name(img, "GodotPlugins.Game", "Main")`），
ILLink 看不见这条路径，于是把 `GodotPlugins.Game` 连同整个游戏代码都删了。

实测解包 `index.pck` 对比构建产物：

| 文件 | 构建产物 | pck 里（被裁后） |
|---|---|---|
| `UWPro.dll` | 2,598,912 | **4,096**（只剩入口桩） |
| `System.Private.CoreLib.dll` | 4,652,368 | 4,333,056 |
| `System.Collections.Immutable.dll` | 255,312 | 18,432 |
| pck 里 .NET 文件总数 | 235 | **55** |

浏览器里表现为：

```
ERROR: .NET: Couldn't find class 'GodotPlugins.Game.Main'.
ERROR: .NET: Failed to initialize Mono runtime.
RuntimeError: null function
```

**修复**：`UWPro.csproj` 的 web 分支加

```xml
<PropertyGroup Condition=" '$(GodotTargetPlatform)' == 'web' ">
  <PublishTrimmed>false</PublishTrimmed>
  <PublishReadyToRun>false</PublishReadyToRun>
</PropertyGroup>
```

并加 `TrimmerRootAssembly Include="UWPro" / "GodotSharp"` 作双保险，然后**删掉 `.godot/mono/temp` 重新导出**。
代价：pck 从 227MB 涨到 251MB（约 +24MB），完全值得。

### 2. 4.8-dev7 魔改版自身是坏的，**不要用**

它的 web 模板里静态链接的 mono 运行时是 **.NET 9.0.20**（从 `godot.wasm` 里提取到的版本串 `9.0.2026.41315` / `9.0.20.0`），
但它的 `GodotSharp.4.8.0-dev.7.nupkg` **只有 `lib/net10.0`**（restore 直接报 NU1202），
强制 net10.0 后类库变成 **10.0.12** → 运行时与类库不同代 → mono 解析 internal call 失败：

```
Your mono runtime and class libraries are out of sync.
The out of sync library is: System.Private.CoreLib.dll
System.MissingMethodException ... at System.Buffer.MemmoveInternal(...)
RuntimeError: unreachable
```

**结论：用 4.7.2-stable**（运行时 9.0.19，配 net9.0 正好同代），这也与「预发布版还没对齐」的直觉一致。

### 3. 🌟 Web 端**没有任何系统字体**，不打包字体会满屏方框

桌面端 Godot 会自动给默认字体挂一份系统字体做回退，所以中文一直正常；
**Web 上 `OS.get_system_font_path()` 返回空**，没有回退字体的结果是：主菜单
（开始游戏/联机对战/建立主机/编辑卡组/退出游戏）五个按钮**全是缺字方框**，
`❌✅⛔⚡🃏🔍` 这些卡片图标也一样。

**修复**：打包三份开源字体（共 11.7MB）到 `fonts/`，由 `Script/Web/WebFontFallback.cs`
（注册为 `project.godot` 里**第一个** autoload）在 Web 端挂到 `ThemeDB.FallbackFont.Fallbacks` 链末尾。
`OS.HasFeature("web")` 判定，桌面端直接 return，**行为完全不变**。

| 字体 | 体积 | 码点 | 负责 |
|---|---|---|---|
| NotoSansSC-Regular.ttf | 10.11MB | 30,890 | 汉字、中文标点 |
| NotoEmoji-Regular.ttf | 0.85MB | 1,489 | ❌✅⛔⚡🃏🔍… |
| DejaVuSans.ttf | 0.72MB | 5,918 | ⚑⟳↻⟺⊞↳✗✦… |

三份字体对工程里 `.tscn/.tres/.cs/.xml/.json` 出现过的**全部字符覆盖率 100%**（已用 fontTools 逐字核对）。
若以后新增了没收录的字符、Web 端又出现方框，用同样的脚本重新核对并补一份字体即可。
字体授权见 `fonts/字体授权说明.md`（OFL 1.1 + Bitstream Vera，都可随游戏分发）。

### 4. 必须用 `*_console.exe`，不能用图形版 exe

图形版 `Godot_*.exe` 启动后会**脱离父控制台**：`*> $log` 收到的是 0 字节，
`$LASTEXITCODE` 恒为 0，脚本会误判成「导出成功但没产物」。脚本里已强制改写成 `_console.exe`。

### 5. 不能设 `DOTNET_ROOT`

引擎自身也是 .NET 应用；一旦把它指到用户级 SDK 目录，引擎会找不到自己的运行时并崩溃
（`hostfxr_initialize_for_runtime_config failed`）。**只把 `D:\dotnet9` 加进 PATH。**

### 6. PowerShell 脚本必须存成 **UTF-8 带 BOM**

`导出Web版.ps1` 原先是无 BOM 的 UTF-8（LF 换行）。Windows PowerShell 5.1 没有 BOM 时会**按 ANSI 代码页解析**，
中文注释被解成乱码，可能吞掉引号/括号，报出一堆莫名其妙的
`语句块或类型定义中缺少右"}"`。已统一改成 **UTF-8 BOM + CRLF**。
`打包发布.ps1` / `ResolveGodot.ps1` 本来就有 BOM（所以一直正常），`AnalyzeEffects.ps1` 也已补齐。

### 7. Web 预设里 `variant/thread_support=true`，所以必须走 HTTP

见第四节。`file://` 打开必然报 Cross-Origin Isolation 缺失，与导出成败无关。

### 8. 引擎会自动改工程

用**官方 4.7.1 编辑器**打开过工程后，Godot 会把 csproj 里的
`Sdk="Godot.NET.Sdk/x.y.z"` 改写成它自己的版本（TFM 不动），
于是 Web 构建又回到错误组合。`导出Web版.ps1` 每次导出前会自动改回 `Godot.NET.Sdk/4.7.2` 并先存 `.bak`。

### 9. 导出目录必须预先存在，且导出会清空它

否则报「目标文件夹不存在或无法访问」；同时 `--export-release` 会重写整个输出目录，
放在里面的 `_headers` / `.htaccess` / `web.config` 需要重拷（源文件放在 `web_deploy_headers/`）。

### 10. 网络

本机 `github.com` 被代理拦截：GitHub Release 走 `https://gh-proxy.com/https://github.com/...`；
`raw.githubusercontent.com`、`api.github.com`、`cdn.jsdelivr.net` 可直连。
`dotnet-install.ps1` 在本机静默失败，改用 `https://builds.dotnet.microsoft.com/dotnet/Sdk/<ver>/dotnet-sdk-<ver>-win-x64.zip` 直链。

### 11. 目录迁移遗留

工程已从 `D:\UWPro` 迁到 `D:\MYPROJECT\UWPro`，多份旧脚本注释里仍写 `D:\UWPro`。

### 12. `ProjectSettings` 里 `packages/` 与 `build/` 会被 Godot 扫描进 pck

`RestorePackagesPath=./packages` 把 NuGet 包放在工程内，Godot 会把它当资源目录扫描并导入其中的 `.png`；
`build/` 同理。已各放一个 `.gdignore` 让 Godot 整目录跳过（否则 pck 里会多出 40+ 条无用条目）。

---

## 六、下一步：卡图体积（首屏硬伤）

当前 `index.pck` **259.3MB**、`index.wasm` **53.6MB**，合计 **约 313MB** —— 对普通玩家不可接受。

> pck 的体积构成（用 `检查Web包.py` 可以看到 Top 占用）：
> `.godot/exported/**`（卡图的导入产物）约 **249MB**，`Cards/**` 源图 1.3MB，
> `fonts/` 约 12MB，`.godot/mono/publish/**`（.NET 程序集，关裁剪后）约 24MB。

按 `卡图Web化加载方案.md` 执行：
1. 794 张卡图（`Cards/` 共 663MB）转 **WebP**、长边 ≤512、q≈78 → 预计降到 **30~60MB**；
2. 同步改 `Script/UI/CardUI.cs:764-765` 中硬编码的 `.jpg` 判断（建议改为按 UID 匹配、忽略扩展名）；
3. 若仍慢，再叠加 CDN 按需拉取（预设里用 `exclude_filter` 排除 `Cards/Image/`）。

---

## 七、回退与并行方案

| 目的 | 操作 |
|---|---|
| csproj 被编辑器改坏（Sdk/TFM 不对） | `Copy-Item .\UWPro.csproj.pre-webfix.bak .\UWPro.csproj -Force`，或直接跑 `导出Web版.ps1`（它会自愈） |
| 想彻底回到 4.8 迁移前的 csproj | `Copy-Item .\UWPro.csproj.pre-web-4.8.bak .\UWPro.csproj -Force`（之后需手动补回 net9.0 与 `PublishTrimmed=false`） |
| 想改 csproj 的 `PublishTrimmed=false` | 别改——改了就会出现第五节第 1 条的「找不到 `GodotPlugins.Game.Main`」 |
| 桌面打包 | **已统一到 4.7.2**：`GodotPath.txt` 指向 4.7.2 魔改版，直接 `. \打包发布.ps1 -Verify` 即可。回退到 4.7.1 用 `Copy-Item .\GodotPath.txt.pre-4.7.2.bak .\GodotPath.txt -Force` |
| Web 端字体又出现方框 | 用第五节第 3 条的脚本重新核对用字，补 `fonts/` 里的字体并加进 `WebFontFallback.cs` 的数组 |
| 换别的 4.x 魔改版引擎 | **先提取 `web_release.zip` 里 `godot.wasm` 的 runtime 版本串**，确认与所选 `GodotSharp` 的 TFM 同代，再改 `UWPro.csproj` 的 Sdk/TFM |

> **【2026-10-12 已统一】两条流水线现在用同一个 4.7.2 引擎，csproj 不再被来回改写。**
> 依据：`%APPDATA%\Godot\export_templates\4.7.2.stable.mono\` 里**同时装着**
> 魔改版的 `web_release.zip`/`web_debug.zip` **和官方 4.7.2 的 `windows_release_x86_64.exe` 等桌面模板**；
> 而魔改版 `mono_web_export` 引擎本身就是完整编辑器 → 一套引擎吃两条流水线。
> `GodotPath.txt` 已改指 4.7.2（备份 `GodotPath.txt.pre-4.7.2.bak`）。
> `打包发布.ps1` 的参数本来就是 `EngineSdk=4.7.2 / TargetFramework=net9.0`，无需改动。
>
> **`打包发布.ps1` 为此做了两处适配**（2026-10-12，备份 `打包发布.ps1.pre-4.7.2.bak`）：
> 1. **强制用 `*_console.exe`**：4.7.2 魔改版的图形版 exe 会脱离父控制台，
>    `& $Godot ... *> $log` 拿到 **0 字节日志**、`$LASTEXITCODE` 恒为 0，
>    脚本会把一次成功的导出误报成「导出结束但没有产出 exe」；
> 2. **改用「savepack 完成 + 宽限 25 秒」收尾**：4.7.2 的 `--headless` 在
>    `[ DONE ] savepack` 之后**不会自己退出**（实测 >300s），所以判据不能是退出码。
>    做法与 `导出Web版.ps1` 完全一致。
> 3. 另外修了一个引号坑：`Start-Process -ArgumentList` 是**空格拼接、不自动加引号**，
>    含空格的预设名 `"Windows Desktop"` 必须自己带引号，否则报
>    `ERROR: Invalid export preset name: Windows`。

---

## 八、Web 端「绝对不能写」的代码红线（2026-10-12 实测确定）

魔改版引擎把 mono 运行时**静态链接**进 wasm，两条路径在 Web 上**必然 abort**
（`RuntimeError: unreachable` / `Aborted()`，画面直接冻住、看起来像"卡死"）。
桌面端完全不受影响，所以**同一份代码在桌面上永远测不出来**。

### 红线 1：不要碰 .NET 全球化 / ICU 路径

| 写法 | Web 上 | 替代 |
|---|---|---|
| `"ABC".ToLower()` / `.ToUpper()` / `.ToLowerInvariant()` / `.ToUpperInvariant()` | ❌ abort | `WebSafeText.ToLowerAscii()` |
| `"BH".StartsWith(".")`（单参数 `string` 重载） | ❌ abort | `WebSafeText.StartsWithOrdinal()` |
| `"...".EndsWith("x")`（同上） | ❌ abort | `WebSafeText.EndsWithOrdinal()` |
| **`"..." + (-1)` / `$"{-1}"` / `(-1).ToString()`** —— **格式化负整数** | ❌ abort | `WebSafeText.Int(-1)` |
| **浮点插值/`ToString()`**（如 `$"{1.25f}"`） | ❌ abort | `WebSafeText.Float(1.25f)` |
| `list.Sort()`（无参，文化敏感比较器） | ❌ abort | `list.Sort(StringComparer.Ordinal)` |
| `DateTime.Now.ToString("HHmmss")`（带格式串） | ❌ abort | `DeckBuilder.NowHHmmss()` |
| **正整数** 格式化（`$"{12}"`、`"a" + 12`） | ✅ 安全（走 `Int32ToDecStr` 纯算术快路径） | — |
| `StartsWith/EndsWith(..., StringComparison.Ordinal/OrdinalIgnoreCase)` | ✅ 安全 | — |
| `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)` | ✅ 安全 | — |
| `string.CompareOrdinal` | ✅ 安全 | — |

> ⚠️ **`InvariantGlobalization` 不管用**：`AppBundle/UWPro.runtimeconfig.json` 里
> `System.Globalization.Invariant: true`、`Hybrid: false`、`PredefinedCulturesOnly: true` **本来就都有**，照样崩。
> 所以只能在代码层绕开。
>
> ⚠️ **"正整数看着没事"是最容易踩的陷阱**：曾经有一版 banner 能正常打印，是因为它的值全是 `const`、
> 被编译器**常量折叠成了字面量**，运行期一次格式化调用都没有；改成 `static` 字段后立刻崩。
> **判断某个 API 是否安全，必须确认它在运行期真的被调用了。**
>
> 汇总的编码规范与实现见 `Script/IO/WebSafeText.cs`（类头有完整记录）。
> **引擎自带的 ICU 仍可用**：`index.wasm` 里有 `ICU`×48 / `BreakIterator`×9 / `TextServerAdvanced`×9，
> 中文断行是引擎自己做的，与 .NET 那套全球化无关。

### 红线 2：不要从托管代码连接 `RenderingServer.FramePostDraw`

**这一条曾经让整个 Web 版"一进卡组编辑就卡死"**（wasm abort 后 canvas 冻住、点不动任何东西），
且极具迷惑性：排查时把所有内容都摘掉了（不建卡牌面板、不加载纹理、隐藏全部节点、
`_Ready` 立刻返回、甚至根节点下 **0 个子节点**）**仍然照崩** —— 因为崩因根本不在场景内容里。

**决定性 A/B（同一份构建，只改 `index.html` 的 `GODOT_ARGS`）**：

| 连接的信号 | 结果 |
|---|---|
| `RenderingServer.FramePostDraw`（只这一条） | ❌ **首帧 3~13ms 后 `Aborted()`**，心跳 0 条 |
| `RenderingServer.FramePreDraw`（只这一条） | ✅ 存活 16s / 240fps |
| `SceneTree.ProcessFrame`（只这一条） | ✅ 存活 19s / 240fps |
| 全不连接 | ✅ 存活 39~49s / 240fps |
| 连接 `FramePostDraw` 但**回调体什么都不做** | ❌ **仍然崩** |

⇒ **崩因是「连接该信号」这个动作本身**，与回调里写什么无关。

**已落地的硬保险**：`DeckBuilder.StartFirstFrameProbe()` 里

```csharp
bool isWeb = OS.HasFeature("web");
_postConnected = !isWeb;                       // Web 上永不连接 FramePostDraw
if (_postConnected) RenderingServer.Singleton.FramePostDraw += OnProbeFramePostDraw;
RenderingServer.Singleton.FramePreDraw += OnProbeFramePreDraw;   // 这两条 Web 上安全
GetTree().ProcessFrame += OnProbeProcessFrame;
```

**帧级计时怎么办**：用 `_Process` / `_PhysicsProcess` 里的 `Time.GetTicksMsec()`。
注意 `Time.GetTicksMsec()` 是**引擎启动以来的毫秒数**，不是绝对时钟。

---

## 九、运行时诊断开关（无需重新导出）

`build/web/index.html` 里的 `"args": [ ... ]` 就是给引擎的命令行参数，
**改一行刷新页面即可生效**（导出后该字段会被重置为 `[]`）。
解析代码在 `Script/Web/WebPerfParams.cs`，由 `Script/Web/WebFontFallback.cs`（首个 autoload）提前解析。

| 开关 | 作用 |
|---|---|
| `--wp-perf` / `--wp-noperf` | 打开/关闭 `[PERF]` 阶段计时日志（默认关） |
| `--wp-probe` / `--wp-noprobe` | 打开/关闭首帧探针与每秒心跳 `[BEAT]`（默认关） |
| `--wp-auto=deck\|lobby\|room\|lobbyonline\|roomonline\|mainmenu` | 启动 N 秒后自动切到指定场景（默认不切） |
| `--wp-delayms=1200` | 自动切换前的等待毫秒数 |
| `--wp-notex` | 完全不加载卡图（量"摘掉纹理后还剩多少耗时"） |
| `--wp-pagesize=12` | 卡池每页行数（量"行数 ↔ 耗时是否线性"） |
| `--wp-nowrap` / `--wp-nocards` / `--wp-sync` / `--wp-prog` | 换行模式 / 不建卡牌面板 / 强制同步加载 / 渐进填图 |

`DevFlags.cs` 里的四个编译期开关**交付时全部为 false**（`FontFallback=true` 除外），
所以发布版完全静默、不会自动跳场景、也不会连任何绘制信号。

---

## 十、本机自检脚本（无头浏览器实跑）

```powershell
# 浏览器 9222 与预览服务器 8080 常驻时，直接：
node .\Web运行自检.mjs --wait 45 --shot logs\shot.png > logs\shot.txt 2>&1
```

> ⚠️ **CDP 的 `Runtime.enable` 会回放上一次的缓存日志**，所以"日志里出现过 Aborted"
> 不等于"这一次崩了"。判断本轮是否存活，请**只取最后一个 `Godot Engine v4.7.2` 之后**的日志，
> 或者直接看 `--wp-probe` 的每秒心跳 `[BEAT]`。
>
> ⚠️ `logs/` 下已放 `.gdignore`：否则实验截图会被 Godot 当纹理导入（生成 `.import`）并**打进 pck**
> （实测会白涨约 1.6MB）。
