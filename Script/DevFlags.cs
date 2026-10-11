/// <summary>
/// 【开发期开关 —— 编译期默认值】
///
/// 【2026-10-12 更正】本文件原先写着「`OS.GetCmdlineArgs()` 会把 Web 打崩，所以开关必须做成编译期常量」。
/// 那个结论**已被推翻**：逐句埋点后查清，当时崩的是后面 `String.StartsWith(string)` 触发的
/// .NET 全球化/ICU 路径（详见 Script/IO/WebSafeText.cs）。同一封送路径上的
/// `DirAccess.GetDirectories()` 实测可正常返回 11 项，`OS.GetCmdlineArgs()` 属于同一路径、实测安全。
/// 因此 `WebPerfParams` 已恢复「从 index.html 的 GODOT_CONFIG.args 读开关」，
/// 本文件的三个开关退化为**没有命令行参数时**的兜底默认值。
///
/// ⚠️ **交付前必须把下面三项改回 false**（否则发布版会自己跳进卡组编辑、并狂打日志）。
/// </summary>
public static class DevFlags
{
    // 【为什么是 static readonly 而不是 const】
    // const 会被编译期折叠，`if (!DevFlags.FontFallback) { ...; return; }` 这种写法
    // 在值为 true 时会直接报 CS0162「检测到无法访问的代码」，且改起来容易被忽略；
    // static readonly 保留为一个真正的运行期分支，改开关所见即所得。
    // 只有一处轻微代价：改成字段读取而非常量内联，对性能无影响。

    /// <summary>启动 1.2 秒后自动进入「编辑卡组」。仅用于无头浏览器实测（省掉模拟点击）。
    /// **交付前必须为 false**（已设 false）。需要时用运行时开关 `--wp-auto=deck` 临时打开。</summary>
    public static readonly bool AutoEnterDeck = false;

    /// <summary>
    /// 卡组界面首帧探针（帧级时间戳）。**交付前必须为 false**（已设 false）。
    /// 需要时用运行时开关 `--wp-probe` 临时打开。
    ///
    /// 【2026-10-12 重大警告】**这个探针曾经让整个 Web 版一进卡组编辑就 wasm abort。**
    /// 根因不是探针本身，而是它连接了 `RenderingServer.FramePostDraw` ——
    /// 在 Web 上从托管代码连接该信号**必崩**（`--wp-hooks=post` 复现，`pre`/`proc` 均正常）。
    /// 已在 `DeckBuilder.StartFirstFrameProbe()` 里用 `OS.HasFeature("web")` 做硬保险，
    /// 现在开着它 Web 也不会崩。
    /// </summary>
    public static readonly bool FrameProbe = false;

    /// <summary>打印逐项明细日志（[PERF] 行）。**交付前必须为 false**（已设 false）。
    /// 需要时用运行时开关 `--wp-perf` 临时打开。</summary>
    public static readonly bool VerbosePerf = false;

    /// <summary>
    /// Web 端中文字体回退。<b>交付前必须为 true</b>（关掉后 Web 版中文全是方框）。
    /// 仅用于定位"首帧绘制崩溃"：若关掉后能进界面，就说明崩在字形栅格化。
    /// </summary>
    public static readonly bool FontFallback = true;
}
