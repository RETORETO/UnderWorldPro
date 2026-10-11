using Godot;

/// <summary>
/// Web 平台字体兜底 —— 只在 web 上生效，桌面端行为完全不变。
///
/// 【为什么需要它（2026-10-11 实测）】
/// 工程里没有任何自带字体，UI 全靠引擎默认字体。桌面端 Godot 会**自动**给默认字体
/// 挂上「系统字体」作为回退，所以中文和 ⚔🔍💡 这类符号一直显示正常；
/// 但 Web 平台的 OS.get_system_font_path() 返回空 —— 没有任何系统字体可回退，
/// 于是所有汉字和符号都画成缺字占位框。实测导出后浏览器里主菜单
/// （开始游戏/联机对战/建立主机/编辑卡组/退出游戏）五个按钮**全是方框**。
///
/// 【做法】
/// 把三份开源字体打包进工程，运行时按顺序挂到 ThemeDB 默认字体的 fallbacks 链末尾：
///   1. NotoSansSC-Regular.ttf  （10.11 MB / 30890 码点）—— 汉字（CJK 全量 + 中文标点）
///   2. NotoEmoji-Regular.ttf   （ 0.85 MB /  1489 码点）—— emoji 图标 ❌✅⛔⚡🃏🔍…
///   3. DejaVuSans.ttf          （ 0.72 MB /  5918 码点）—— 箭头/数学/杂项符号 ⚑⟳↻⟺⊞↳✗✦
/// 已用脚本把工程里全部 .tscn/.tres/.cs/.xml/.json 出现过的字符跑过一遍，
/// 三份字体对工程实际用字覆盖率为 100%（汉字、emoji、符号一个不缺）。
/// 顺序不可颠倒：默认字体先试，缺字才逐个回退，所以 Latin/数字的字形与桌面端一致。
///
/// 授权：Noto 系列为 SIL OFL 1.1；DejaVu 为 Bitstream Vera + 公有领域。均可随游戏分发，
///       详见 fonts/字体授权说明.md。
///
/// Web 判定用 OS.HasFeature("web")，桌面版依旧走系统字体，不受影响。
/// </summary>
public partial class WebFontFallback : Node
{
    /// <summary>随工程打包的兜底字体，**顺序即回退优先级**</summary>
    private static readonly string[] FallbackFontPaths =
    {
        "res://fonts/NotoSansSC-Regular.ttf",
        "res://fonts/NotoEmoji-Regular.ttf",
        "res://fonts/DejaVuSans.ttf",
    };

    public override void _Ready()
    {
        // 【2026-10-12】把命令行开关的解析提前到最早上树的自加载：
        // `OS.GetCmdlineArgs()` 已实测**安全**（逐句埋点打到 "项数=0"、Parse 也跑完）。
        // Ensure() 是幂等的，MainMenu._Ready 里再调一次不会有副作用。
        WebPerfParams.Ensure();

        // 桌面 / 编辑器直接跳过，保持与改造前完全一致
        if (!OS.HasFeature("web"))
        {
            return;
        }

        // 【诊断开关】关掉后 Web 版中文会全部变成方框，仅用于定位"首帧绘制崩溃"。
        if (!DevFlags.FontFallback)
        {
            GD.Print("[WebFontFallback] 被 DevFlags.FontFallback=false 关闭（诊断用；中文会显示为方框）");
            return;
        }

        var extras = new Godot.Collections.Array<Font>();
        foreach (var path in FallbackFontPaths)
        {
            var font = ResourceLoader.Load<Font>(path);
            if (font == null)
            {
                GD.PushWarning($"[WebFontFallback] 未找到 {path}，相关字符在 Web 端会显示为方框");
                continue;
            }
            extras.Add(font);
        }

        if (extras.Count == 0)
        {
            GD.PushWarning("[WebFontFallback] 没有任何兜底字体可用，Web 端中文将显示为方框");
            return;
        }

        var baseFont = ThemeDB.FallbackFont;
        if (baseFont == null)
        {
            // 极端情况下连默认字体都没有：直接拿第一个当默认字体
            ThemeDB.FallbackFont = extras[0];
            GD.Print($"[WebFontFallback] 默认字体为空，已改用 {FallbackFontPaths[0]}");
            return;
        }

        var chain = baseFont.Fallbacks ?? new Godot.Collections.Array<Font>();

        int added = 0;
        foreach (var font in extras)
        {
            bool exists = false;
            for (int i = 0; i < chain.Count; i++)
            {
                if (chain[i] == font)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                chain.Add(font);
                added++;
            }
        }

        if (added == 0)
        {
            GD.Print("[WebFontFallback] 兜底字体已全部在链上，跳过");
            return;
        }

        baseFont.Fallbacks = chain;
        GD.Print($"[WebFontFallback] 已挂上 {added} 个兜底字体（回退链共 {chain.Count} 项）");
    }
}
