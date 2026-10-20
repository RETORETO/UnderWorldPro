namespace Script.UI
{
    /// <summary>
    /// 【2026-10-20·用户口径】对局内"座位称呼"的唯一来源：**红方 / 蓝方**。
    ///
    /// 口径原话：「游戏中大多数的 P1/P2 需要替换为红方/蓝方，和游戏争夺区配色朝向一致」。
    /// 争夺区两个半区的归属色由 `AreaSlot.Seat1Color`（红）/ `Seat2Color`（蓝）给出，
    /// 故 **座位 1 ＝ 红方、座位 2 ＝ 蓝方**（与"本方/对手"不同：红蓝是**绝对身份**，
    /// 不随本地视角镜像而交换）。
    ///
    /// 使用位置（凡是给玩家看的"第几个玩家"都走这里，禁止再写字面量）：
    ///  · `Arena.PlayerNameOf` 的兜底名（选择窗/区域标签）；
    ///  · `CardZoneLabel` 的兜底名；
    ///  · 单机结算文字（"红方胜利！"）；
    ///  · 单机"让过"按钮文案由场景直接写死为红方/蓝方。
    ///
    /// 联机端的 `ArenaOl.SeatName` 仍用「本方/对手」——那是**视角**称呼（本地座位 2 时本方＝蓝方），
    /// 与"红/蓝"是两套语义，不在此替换。
    /// </summary>
    public static class PlayerSide
    {
        public const string Red = "红方";
        public const string Blue = "蓝方";

        /// <summary>座位号 → 称呼（1＝红方、2＝蓝方；其它原样兜底）。</summary>
        public static string Name(int playerId)
        {
            if (playerId == 1) return Red;
            if (playerId == 2) return Blue;
            return "玩家" + Script.IO.WebSafeText.Int(playerId);
        }

        /// <summary>座位号 → 归属色（与争夺区边框同源，小字提亮以便压在卡图上可读）。</summary>
        public static Godot.Color ColorOf(int playerId)
        {
            if (playerId == 1) return CardBadgeStyle.RedSide;
            if (playerId == 2) return CardBadgeStyle.BlueSide;
            return CardBadgeStyle.Gold;
        }
    }
}
