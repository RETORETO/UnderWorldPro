# UWPro

1v1 卡牌对战游戏 —— Godot 4.7.2 + C#（.NET 9），桌面与 Web 同源。

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

## Web 版

Web 导出必须用 **魔改版 Godot**（官方版不支持 C# 导出 Web）：

- 导出：`powershell -ExecutionPolicy Bypass -File 导出Web版.ps1`
- 预览：`启动Web预览.cmd`

Web 构建带线程（SharedArrayBuffer），浏览器要求页面处于跨源隔离状态，所以**必须通过 HTTP 打开**，直接双击 `index.html` 会报错。

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
