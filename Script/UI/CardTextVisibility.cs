using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【2026-10-20·用户口径】**卡面"提示性文字"的全局显隐开关**。
    ///
    /// 口径原话：「原本卡片上有大量提示性文字（除了封/附以外几乎所有文字），这些文字取消显示，
    /// 只有打开 GM 输入框的时候显示」；「卡片悬浮 UI 也要显示这些提示性文字（仅打开 GM 输入框时显示）」。
    ///
    /// 归属范围（"提示性文字"＝与印刷卡图重复的说明，**不含数值**）：
    ///   · `CardUI`：实例 ID / 类别 / 子类型 / 领域；
    ///   · `AreaCardUI`：地区名 / 需求势力 / 分数 / 效果说明；
    ///   · `CouncilCardUI`：卡名 / 子类型 / 持续+行动效果全文。
    /// **不隐藏**（改为角标风格常显）：费用、调查/战斗/势力、防御、地区势力点、卡组/墓地张数。
    ///
    /// 为什么是"静态开关 + 变更事件"而不是"视图遍历卡片逐个改"：
    ///   · 卡 UI 会出现在**很多**地方：战场、手牌、牌库/墓地、选择窗、检视窗、角标展开面板、堆叠条目…
    ///     其中「选择窗 / 检视窗 / 展开面板 / 堆叠条目」里的卡是**按需新建**的浮层卡，
    ///     既不在 `Arena._cardUIMap` 也不在任何区域容器里，视图无从遍历；
    ///   · 所以开关放在静态类里，**每张 CardUI 自己在 `_Ready` 订阅、`_ExitTree` 退订**，
    ///     创建时读一次当前值、开关变化时收到一次事件 ⇒ 所有实例天然同步，新增卡位零改动。
    ///
    /// Web 安全红线遵守：不使用 ToLower*/无参 StartsWith/EndsWith/Sort/OrdinalIgnoreCase/Char.IsDigit 等。
    /// </summary>
    public static class CardTextVisibility
    {
        /// <summary>是否显示卡面提示性文字（默认关＝只显示卡图与角标/数值）。</summary>
        public static bool ShowHintText { get; private set; } = false;

        /// <summary>开关变化（订阅方按自己的 `IsFaceUp` 重跑一次 `SetFace` 即可）。</summary>
        public static event System.Action Changed;

        /// <summary>设开关；值未变时什么都不做（避免每次开关命令器都全量重绘）。</summary>
        public static void Set(bool on)
        {
            if (ShowHintText == on) return;
            ShowHintText = on;
            try { Changed?.Invoke(); }
            catch (System.Exception ex) { GD.PrintErr($"[CardTextVisibility] 通知订阅方失败：{ex.Message}"); }
        }

        /// <summary>翻转（GM 命令框每次打开/关闭时调用）。</summary>
        public static void Toggle() => Set(!ShowHintText);

        /// <summary>
        /// 【退出战场时的静默复位】开关是静态的，会活过切场景；离开战场时 GM 输入框已不存在，
        /// 必须复位成"不显示"，否则回到主菜单/卡组编辑器后所有卡都带着提示性文字。
        /// <para>为什么**不派发** `Changed`：本方法在 `_ExitTree` 里调用，此时卡 UI 正在随场景销毁
        /// （`_ExitTree` 尚未跑完、退订还没发生），若此时回调它们去改已被释放的子节点必然崩。
        /// 反正它们下一刻就一起没了，不需要重绘。</para>
        /// </summary>
        public static void ResetSilent() => ShowHintText = false;
    }
}
