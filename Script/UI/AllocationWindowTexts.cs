namespace Script.UI
{
    /// <summary>
    /// 【2026-10 用户口径·JC104】"牌库顶 / 牌库底"两区**分配窗的可复用文案**。
    ///
    /// 同一个窗口机制（`InvestigationWindow`：拖拽把牌分到"牌库顶 / 牌库底"两区）被两种动作复用：
    ///  · **调查对抗**（胜者分配牌库顶 X 张，确认后抽 1 张）；
    ///  · **预测X**（检视牌库顶 X 张，任意数量置顶、其余置底，**不抽牌**）。
    ///
    /// 用户 2026-10 口径：**"UI 复用了调查对抗 UI，但是显示上不能是调查对抗 UI，应该做成可复用的预测动作 UI"**
    /// ⇒ 窗标题（含操作系统窗口标题栏 `Window.Title`）、窗内标题、两区标题、提示行**全部按场景给出**
    /// （数据驱动），任何预测类效果（预测1/预测2/预测3…）自动拿到"预测"文案，
    /// 不再显示"调查对抗"字样。复用窗口机制的那份实现（拖拽/排序/确认）保持单一。
    /// </summary>
    public sealed class AllocationWindowTexts
    {
        /// <summary>操作系统窗口标题栏文案（`Window.Title`）。</summary>
        public string WindowTitle = "";
        /// <summary>窗内标题行（`RootControl/TitleLabel`）。</summary>
        public string Title = "";
        /// <summary>上方区域（牌库顶）标题。</summary>
        public string TopZoneHeader = "";
        /// <summary>下方区域（牌库底）标题。</summary>
        public string BottomZoneHeader = "";
        /// <summary>操作提示行。</summary>
        public string Hint = "";
        /// <summary>日志前缀（如"调查窗口"/"预测窗口"），便于排查时区分同一份实现的两个入口。</summary>
        public string LogTag = "分配窗口";

        /// <summary>调查对抗（`areaNo` ≤ 0 时不带地区号）。</summary>
        public static AllocationWindowTexts Investigation(int areaNo) => new AllocationWindowTexts
        {
            WindowTitle = "调查对抗",
            Title = areaNo > 0 ? $"{areaNo}地区调查对抗获胜" : "调查对抗获胜",
            TopZoneHeader = "⬆ 放到牌库顶（左侧卡在上方）",
            BottomZoneHeader = "⬇ 放到牌库底（左侧卡在上方）",
            Hint = "💡 拖拽卡牌到上方或下方区域",
            LogTag = "调查窗口",
        };

        /// <summary>预测X（不抽牌；置顶顺序＝左侧卡在上方）。</summary>
        public static AllocationWindowTexts Prediction(int count) => new AllocationWindowTexts
        {
            WindowTitle = $"预测{count}",
            Title = $"🔮 预测{count}：检视牌库顶 {count} 张，任意数量置于牌库顶（其余置于牌库底）",
            TopZoneHeader = "⬆ 置于牌库顶（左侧卡在上方）",
            BottomZoneHeader = "⬇ 置于牌库底（左侧卡在上方）",
            Hint = "💡 拖拽卡牌到上方或下方区域（预测：牌库顶/底的顺序按左侧在上的顺序）",
            LogTag = "预测窗口",
        };
    }
}
