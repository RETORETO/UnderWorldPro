# UWPro

1v1 卡牌对战游戏 —— Godot 4.7.2 + C#（.NET 9），桌面与 Web 同源。

> 仓库名 `UnderWorldPro`，工程内部代号 `UWPro`（`project.godot` 里的 `config/name`）。
> 本仓库只放源码、卡池与文档，**不含任何启动 / 打包脚本**，下面直接给命令行。

## 快速开始

先装 **.NET（mono）版** Godot 4.7.2 —— 标准版加载不了 C# 脚本。

### 只想玩：加入公开测试服

测试服常驻 `120.76.248.92:28950`（WebSocket 传输，桌面 / 浏览器同服）。

用 Godot 打开 `project.godot`（首次会还原 NuGet 依赖，需要联网），F5 进主菜单，在联机页填地址即可；
或者命令行直接落在大厅：

```
Godot_v4.7.2-stable_mono_win64.exe --path . res://Scenes/Net/NetEntry.tscn -- --lobby 120.76.248.92 28950 你的昵称
```

### 本地起一个大厅服务端

```
Godot_v4.7.2-stable_mono_console.exe --headless --max-fps 30 --path . res://Scenes/Net/NetServer.tscn -- --port 28950
```

> `--max-fps 30` 别省：headless 不限帧会吃满一整个核。

### 本地三开自测

起一个服务端（上面那条），再开两个客户端（昵称不同）指向 `127.0.0.1:28950`：

```
Godot_v4.7.2-stable_mono_win64.exe --path . res://Scenes/Net/NetEntry.tscn -- --lobby 127.0.0.1 28950 玩家A
```

## Web 版

Web 构建带线程（SharedArrayBuffer），浏览器要求页面处于**跨源隔离**状态，所以必须通过 HTTP 打开、并带 COOP/COEP 响应头（配置示例见 `web_deploy_headers/`），直接双击 `index.html` 会报错。

自己导出需要 **魔改版 Godot**（官方版不支持 C# 导出 Web）。三条硬约束与踩坑记录见 `文档/设计/Web/Web导出环境状态与下一步.md`，要点是：引擎锁 4.7.2-stable、`TargetFramework` 必须与引擎内静态链接的 mono 运行时同代、导出后确认 pck 里 `Cards/card_index.json` 与 `Decks/*.json` 都在。

## 工程结构

| 目录 | 内容 |
|---|---|
| `Script/` | 全部 C# 代码：`Manager/` 规则引擎、`Cards/` 卡牌效果、`Net/` 联机、`UI/` 界面、`Tests/` 自检 |
| `Scenes/` | 场景；`TestsRunner.tscn` 是自检入口 |
| `Cards/` | 卡池：`Data/` 为 `.tres` 卡数据，`Image/` 为卡面，`card_index.json` 为索引 |
| `Decks/` | 预置卡组（含各色全量测试卡组） |
| `制作指南/` | 各类效果 / 卡型的制作规范，改卡前先看这份 |
| `文档/` | 需求原文、设计稿、进度账本 |
| `工具/` | Web 包检查、压测、文档链接修正等辅助脚本（Python / Node） |
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
