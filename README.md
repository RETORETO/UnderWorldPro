# UWPro

1v1 卡牌对战游戏 —— Godot 4.7.2 + C#（.NET 9），桌面与 Web 同源。

> 仓库名 `UnderWorldPro`，工程内部代号 `UWPro`（`project.godot` 里的 `config/name`）。

## 快速开始

### 只想玩：加入公开测试服

测试服常驻 `120.76.248.92:28950`（WebSocket 传输，桌面 / 浏览器同服）。

1. 安装 **.NET（mono）版** Godot 4.7.2 —— 标准版加载不了 C# 脚本。
2. 双击根目录 `连服务器.bat`；或命令行：

   ```
   启动联机客户端.bat 120.76.248.92 28950 你的昵称
   ```

3. 进大厅开房 / 加入房间，双方到齐自动开局。

### 改代码：本地跑起来

1. 用 Godot 4.7.2 **mono** 版导入本目录的 `project.godot`
2. 首次打开会还原 NuGet 依赖（`Godot.NET.Sdk`、`Ater.DeepSeek.Core`），需要联网
3. F5

### 自己三开测联机

`一键联机三开.bat` —— 起 1 个大厅服务端 + 2 个客户端，本机自连。详见 `start_online_3.ps1` 顶部注释（支持 `-ServerOnly` / `-ClientOnly` 跨机联调）。

## 根目录脚本

| 脚本 | 用途 |
|---|---|
| `连服务器.bat` | 连公开测试服（地址写死在脚本内，与 `Script/IO/OnlineDefaults.cs` 同步） |
| `启动联机客户端.bat <IP> <端口> <昵称>` | 连指定服务器 |
| `一键联机三开.bat`（内部 `start_online_3.ps1`） | 本机 1 服务端 + 2 客户端自测 |
| `启动大厅服务端.bat` | 单独起大厅服务端 |
| `启动联机三进程测试.bat` | 自动冒烟三进程 |
| `启动Web预览.cmd` | 本地 HTTP 预览已有的 Web 构建 |
| `ResolveGodot.ps1` | 定位 Godot(mono) 可执行文件，上面几个脚本共用 |

> 打包 / 导出脚本（桌面包、Web 包）不在本仓库维护。想自己出包，环境搭建与三条硬约束见 `文档/设计/Web/Web导出环境状态与下一步.md`。

## Web 版

Web 构建带线程（SharedArrayBuffer），浏览器要求页面处于跨源隔离状态，所以**必须通过 HTTP 打开**，直接双击 `index.html` 会报错。已有构建用 `启动Web预览.cmd` 预览。

自己导出需要 **魔改版 Godot**（官方版不支持 C# 导出 Web），流程同样是三步：装魔改版引擎 → 确认 .NET 运行时版本与工程 `TargetFramework` 同代 → 导出后验证 pck 里 `Cards/card_index.json`、`Decks/*.json` 都在。细节见上面那份文档。

## 工程结构

| 目录 | 内容 |
|---|---|
| `Script/` | 全部 C# 代码：`Manager/` 规则引擎、`Cards/` 卡牌效果、`Net/` 联机、`UI/` 界面、`Tests/` 自检 |
| `Scenes/` | 场景；`TestsRunner.tscn` 是自检入口 |
| `Cards/` | 卡池：`Data/` 为 `.tres` 卡数据，`Image/` 为卡面，`card_index.json` 为索引 |
| `Decks/` | 预置卡组（含各色全量测试卡组） |
| `制作指南/` | 各类效果 / 卡型的制作规范，改卡前先看这份 |
| `文档/` | 需求原文、设计稿、进度账本 |
| `fonts/` | Web 端字体兜底与授权说明 |

## 自检

```bash
dotnet build UWPro.csproj -c Debug
"<Godot 4.7.2 console 版>" --headless --path . res://Scenes/TestsRunner.tscn
```

末行出现 `>>> 全部逻辑正确性测试通过` 即全绿（当前 160+ 套件，约 1~2 分钟）。

改了 `Cards/` 下的卡表后**必须**重生成索引，否则卡组构建器读不到新卡：

```bash
"<Godot console 版>" --headless --path . --script res://Script/Tools/CardIndexRunner.cs
```

## 卡牌素材

卡面美术与卡牌文本版权归**原作者**所有。本仓库是其非商业的电子化实现，仅用于开发与内部测试，不作商业分发；如原作者认为有不妥之处，请开 issue，会立即移除相关素材。

字体：Noto Sans SC / Noto Emoji（SIL OFL 1.1）、DejaVu Sans（Bitstream Vera），见 `fonts/字体授权说明.md`。

## 许可

代码暂未附开源协议（保留所有权利）。需要 MIT / Apache-2.0 之类的协议请开 issue 说明。
