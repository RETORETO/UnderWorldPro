# Web 导出环境：当前状态与下一步

> 生成时间：2026-10-09

## 一、环境检查结果

| 项目 | 状态 | 说明 |
|---|---|---|
| `wasm-tools` workload | ✅ 已安装 | 10.0.112/10.0.100 |
| `wasm-tools-net9` workload | ✅ 已安装 | 10.0.112/10.0.100 |
| Web 导出模板 | ✅ 已安装 | `%AppData%\Godot\export_templates\4.8.dev7.mono\`（web_debug.zip 14.9MB / web_release.zip 13.8MB） |
| `TargetFramework` | ✅ net9.0 | 已从net8.0 切换，备份在 `UWPro.csproj.bak-net8` |
| 魔改版引擎 | ⚠️ **版本不匹配** | 见下方问题 |

---

## 二、⚠️ 关键问题：引擎版本与项目不匹配

### 现状

| | 版本 |
|---|---|
| 已安装引擎 | `4.8.dev7.mono.web_export.af6fa8881`（**开发快照**） |
| 引擎内置 C# SDK | `Godot.NET.Sdk 4.8.0-dev.7` |
| 项目使用的 SDK | `Godot.NET.Sdk/4.7.2` |
| `project.godot` 声明 | `config/features=PackedStringArray("4.7", ...)` |

**用 4.8-dev7 打开 4.7.2 的项目，Godot 会要求升级项目，且 C# SDK 版本对不上。**

### 为什么这是问题

1. **4.8-dev7 是开发快照**，不是稳定版。API 可能在任何两个提交之间变化
2. 你当前正处于 **JC 卡包效果验证阶段**——此时切换引擎版本风险很高
   - `project.godot` 会被改写（features 4.7 → 4.8）
   - 可能有 API 破坏性变更需要修代码
   - 出了问题很难区分是「JC 效果 bug」还是「引擎升级引入的 bug」

### 解决方案：换成匹配版本的构建

好消息是上游提供了**与项目完全匹配的 4.7.2 稳定版**：

```
tag: 4.7.2-stable （标记为 Latest，非预发布）
发布时间: 2026-08-18
文件: Godot_v4.7.2-stable_mono_web_export_win64.zip (159.2 MB)
https://github.com/ComplexRobot/godot-dotnet-web-export/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_web_export_win64.zip
```

**建议换用这个版本。** 项目不用做任何引擎层改动，风险降到最低。

---

## 三、行动清单

### 步骤 1：下载并安装 4.7.2 版本的魔改引擎（推荐）

```powershell
# 下载（159 MB）
Invoke-WebRequest -Uri "https://github.com/ComplexRobot/godot-dotnet-web-export/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_web_export_win64.zip" `
  -OutFile "D:\Godot\Godot_v4.7.2_web_export.zip"

# 解压
Expand-Archive -Path "D:\Godot\Godot_v4.7.2_web_export.zip" -DestinationPath "D:\Godot\"
```

解压后目录名应为 `Godot_v4.7.2-stable_mono_web_export_win64`。

### 步骤 2：清理旧的 Web 导出模板（4.8.dev7 的）

```powershell
# 确认已下载 4.7.2 版本后再执行，避免删错
Remove-Item -Recurse -Force "$env:AppData\Godot\export_templates\4.8.dev7.mono"
```

> 也可保留不动——Godot 按版本目录查找模板，4.7.2 会用自己的目录，互不干扰。

### 步骤 3：运行 4.7.2 版本的 install.bat

```powershell
cd D:\Godot\Godot_v4.7.2-stable_mono_web_export_win64
.\install.bat
```

它会做三件事：
- 把 `web_release.zip` / `web_debug.zip` 复制到 `%AppData%\Godot\export_templates\4.7.2.stable.mono\`
- 添加 NuGet 源
- 安装 wasm-tools workload（你已经有了，应该会秒过）

### 步骤 4：更新 GodotPath.txt

编辑 `D:\UWPro\GodotPath.txt`，把路径改为新引擎：

```
D:\Godot\Godot_v4.7.2-stable_mono_web_export_win64\Godot_v4.7.2-stable_mono_web_export_win64.exe
```

> 这样 `打包发布.ps1`、`启动大厅服务端.bat` 等脚本都会自动用新引擎。

### 步骤 5：给项目添加 Program.cs（必需）

魔改版要求项目包含一个带顶层语句的 `Program.cs`。最简单的形式是空实现：

```csharp
// Program.cs（项目根目录）
{}
```

> 注意：不要放进命名空间，不要写 `class`，就是一个裸的顶层语句块。

### 步骤 6：首次打开项目

用新引擎打开 `D:\UWPro`，确认：
- 没有弹出「升级项目到 4.8」之类的提示（若弹出说明引擎版本还是不对）
- C# 项目能正常编译

### 步骤 7：新建 Web 导出预设

在 Godot 中：`项目 → 导出 → 添加… → Web`

建议设置：
- **线程（Threads）**：关闭。开启需要服务端发 `SharedArrayBuffer` 响应头，否则白屏
- **渐进式网页应用（PWA）**：关闭（首版不需要）
- **导出路径**：建议先导出到空目录观察产出文件

---

## 四、导出产物说明

成功导出会得到 4 类文件：

```
index.html          入口页
xxx.wasm            引擎 + C# 运行时（体积大头）
xxx.pck             项目数据（含你的卡牌 .tres 与图片）
xxx.js              胶水代码
```

**本地测试必须用 HTTP 服务，不能直接双击 `index.html`**（浏览器会拦截 wasm 加载）：

```powershell
# 在导出目录起个本地服务
python -m http.server 8080
# 然后浏览器访问 http://localhost:8080
```

---

## 五、已知风险与限制（来自官方 README）

> Games exported for the web lack support for some features.
> Does not support GDExtensions due to the .NET runtime being built without position-independent code.
> * Globalization doesn't work, so for now we're always enabling **invariant mode**.
> * (...) some BCL APIs will not work (i.e.: **crypto APIs**)

对照你的项目：
- ✅ **GDExtension**：你的项目没用 GDExtension，无影响
- ✅ **invariant mode**：我核实过，你的代码全部用 `StringComparison.Ordinal` / `ToLowerInvariant()`，**不受影响**
- ✅ **crypto API**：我核实过，`System.Security.Cryptography` 零引用，无影响
- ⚠️ **上传 itch.io 时**需在项目设置里开启 **SharedArrayBuffer 支持**

---

## 六、还剩的必做项：卡图体积

654 MB 卡图是 Web 首屏的硬伤，**与引擎选型无关，必须处理**。

详见 `卡图Web化加载方案.md`。推荐路径：
1. 先做 WebP + 限制长边 512px → 654 MB 降到 **30-40 MB**
2. 实测加载慢再叠加 CDN 按需拉取

注意转 WebP 后要同步改 `Script/UI/CardUI.cs:764-765` 硬编码的 `.jpg` 判断。

---

## 七、备份与回退

| 文件 | 说明 |
|---|---|
| `UWPro.csproj.bak-net8` | TargetFramework 回退用：`Copy-Item .\UWPro.csproj.bak-net8 .\UWPro.csproj -Force` |
| `D:\Godot\Godot_v4.7.2-stable_mono_win64\` | 原官方引擎，`GodotPath.txt` 改回去即可切回桌面版 |

> **建议**：Web 验证期间保留两个引擎目录并存，`GodotPath.txt` 一行就能切换。
> 这样 JC 效果验证继续用 4.7.2 官方版，Web 导出时才切到魔改版。