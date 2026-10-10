# 卡图按需加载方案（Web 版前置工作）

> 与方案选型无关，**四种 Web 化方案（魔改版 / dn2cpp / GDScript / PixiJS）全都躲不开这一步**。
> 建议现在就开始，与引擎选型并行推进。

---

## 一、问题量化

| 项目 | 现状 |
|---|---|
| 卡图数量 | 794 张 jpg |
| 总体积 | **654 MB** |
| 单张最大 | 6.4 MB（`BQ064_tiquyangben.jpg`） |
| 单局实际需要 | **约 40 张**（两名玩家各 ~20 张牌组 + 若干地区/秘社） |

**关键矛盾**：Godot 的 `.pck` 是整包下载，浏览器无法只拉取 pck 内的单个文件。
654 MB 的首屏加载，对"非技术玩家 + 居家宽带"是不可接受的——大概率打不开页面就流失。

---

## 二、方案对比

### 方案 1：WebP 转码 + 尺寸压缩（**建议先做**）

**思路**：卡牌在 1920x1080 下实际显示尺寸远小于原图。降到实际分辨率后单张可压到几十 KB。

**预估**：

| 压缩策略 | 单张 | 总量 |
|---|---|---|
| 原图（现状） | ~800 KB | 654 MB |
| WebP q80 + 限制长边 512px | ~60-80 KB | ~50-60 MB |
| WebP q75 + 限制长边 400px | ~35-50 KB | ~30-40 MB |

**优点**：改动极小。不需要改加载逻辑，不需要 CDN，Godot 原生支持 `.webp` 导入。
**缺点**：仍然是整包下载，30-40 MB 首屏在较差网络下仍需等待。

**适用判断**：如果你的玩家网络条件普遍较好（家庭宽带为主），30 MB 完全可接受，**方案 1 就够了**。

---

### 方案 2：外置 CDN + 按需拉取（**长期方案**）

**思路**：卡图不打包进 pck，放到静态目录 / 对象存储，客户端用 `HTTPRequest` 按需拉取。

**你的架构已经具备前提条件**——我核实过 `Script/Net/GameSnapshot.cs:156-185` 的 `ZoneCardRef`：

```csharp
public string Uid;
public string TemplateUid;   // 指向 Card.xml / .tres 静态数据  ← 按需加载的钥匙
public bool IsHidden;        // 对手手牌只下发占位，客户端画卡背  ← 天然契合
```

这意味着：
1. 客户端**能确切知道**本局需要哪些卡（`TemplateUid`）
2. 对手隐藏卡只发占位、**不需要真实卡图**（`IsHidden=true` 时只画卡背）

所以单局实际拉取量 = 双方可见牌 + 地区牌 + 秘社牌 ≈ **40-60 张**，而非 794 张。

**实现要点**：

```
【服务端】
  开局时把本局所需的 TemplateUid 清单通过 lobby_res / room_state 下发
  （或客户端本地按牌组 json 计算——Decks/*.json 已有 19 个牌组文件）

【客户端】
  1. 进入房间 → 拿到本局卡牌清单
  2. 对比本地缓存（IndexedDB / localStorage），只拉缺失的
  3. 用 HTTPRequest 逐张拉取，边到边显示占位卡背
  4. 用 TextureLoader 的 loadRemote() 或 HTTPRequest + ImageTexture
```

**导出配置配合**：在 Web 导出预设的 `exclude_filter` 中排除 `Cards/Image/*.jpg`：

```
exclude_filter="Cards/Image/*.jpg, Cards/Image/**/*.jpg"
```

**优点**：首屏极快（只需引擎 wasm ~5-9 MB + 卡牌数据 JSON）。
**缺点**：需要静态托管（对象存储 + CDN）、需要 HTTPS、处理离线/弱网。

---

### 方案 3：混合（**推荐路径**）

**第一步**：先做方案 1（WebP + 压缩），把总量打到 30-40 MB。此时**不依赖任何服务端改动**，风险极低，先拿到一个"能用的版本"。

**第二步**：如果实测首屏加载仍然慢（可用真实玩家网络验证），再叠加方案 2 的按需拉取。压缩后的图即使外置，单张也只几十 KB，拉取成本可接受。

---

## 三、无论走哪个方案都要做的：图片格式与压缩参数

**Godot 4 原生支持 WebP**，无需插件，`.import` 里改 `lossy_compression` 即可。

建议参数：

| 用途 | 格式 | 建议参数 |
|---|---|---|
| 卡牌主图 | WebP | quality 0.78，长边 ≤ 512px |
| 卡背 | WebP | quality 0.85，长边 ≤ 256px |
| UI 图标/装饰 | PNG | 无损（本来就小） |

**转码工具**：Godot 内置批量导入 + 命令行，或用 `cwebp` / ImageMagick 批处理。

```bash
# ImageMagick 批量转 WebP 示例
for f in Cards/Image/**/*.jpg; do
  magick "$f" -resize 512x512\> -quality 78 "${f%.jpg}.webp"
done
```

**注意**：转换后要更新 `.import` 文件或让 Godot 重新导入，并修改代码中硬编码 `.jpg` 的路径判断。

---

## 四、已知需要同步修改的代码位置

我在代码中定位到这些硬编码 `.jpg` 的地方，转 WebP 后必须同步改：

| 文件 | 行 | 内容 |
|---|---|---|
| `Script/UI/CardUI.cs` | 764-765 | `fileName.EndsWith(".jpg")` / `StartsWith(uid)` |
| `Script/IO/ResourceDirScan.cs` | 85-99 | `.tres` 后缀判断，同类资源扫描 |

**建议做法**：优先改为「按 UID 匹配文件名、忽略扩展名」，这样以后换格式（WebP/AVIF）就不用再改代码：

```csharp
// 改前
fileName.StartsWith(uid, StringComparison.OrdinalIgnoreCase)

// 改后（提取 UID 前缀，与扩展名解耦）
Path.GetFileNameWithoutExtension(fileName).StartsWith(uid, ...)
```

---

## 五、行动清单

- [ ] 备份原图（转换是可逆的，但建议保留 `Cards/Image_backup/`）
- [ ] 评估抽样：用 ImageMagick 转 20 张代表性卡图，测体积与画质
- [ ] 全量转 WebP + 限制长边
- [ ] 修改 `CardUI.cs:764-765` 的扩展名硬编码
- [ ] 在 Web 导出预设中排除 `Cards/Image/`（如果走方案 2）
- [ ] 用真实玩家的网络条件实测加载时间

---

## 关联文档

- Web 导出环境准备：`准备Web导出环境.ps1`
- 引擎选型讨论见会话记录（魔改版 vs dn2cpp vs GDScript vs PixiJS）