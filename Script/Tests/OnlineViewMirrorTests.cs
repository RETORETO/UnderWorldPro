namespace Script.Tests
{
    /// <summary>
    /// 【P1·联机视角归一·2026-10-19】锁定"座位 → 界面槽位"必须走镜像映射，禁止按"本地=1 就用 P1 面板"直写。
    ///
    /// 问题现场（用户实测）：以**玩家 2** 进对局时，视角整体镜像（自己恒在下方），
    /// 但对手手牌的 hover 展开判定写成了 `_localPlayerId == 1 ? _p2Hand : _p1Hand` ——
    /// 镜像后本地座位 2 的手牌恰恰画在 `_p1Hand` 上 ⇒ 取到的是**自己**的手牌容器，
    /// 于是对手那排牌背永远不展开（而玩家 1 视角正常）。
    ///
    /// 为什么值得单测：这类错法 headless 下点不出来（要真跑联机双端），而代码看起来"很对称、很像对的"。
    /// 用源码锚点把它钉死：对手容器一律 `HandCtr(对手座位)`，镜像标志必须下发到 `AreaSlot`。
    /// </summary>
    public class OnlineViewMirrorTests : TestRunner
    {
        private static string Read(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            if (f == null) return string.Empty;
            return f.GetAsText();
        }

        /// <summary>① 对手手牌容器必须经 `HandCtr` 的镜像映射取，不能按面板名直取。</summary>
        public void TestMirror_对手手牌走镜像映射()
        {
            string src = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(src), "必须能读到 ArenaOl.cs");

            Assert.IsTrue(src.Contains("HandCtr(_localPlayerId == 1 ? 2 : 1)"),
                "对手手牌容器必须用 HandCtr(对手座位)：镜像后本地座位 2 的手牌画在 _p1Hand 上，" +
                "直取 _p1Hand 会拿到自己的手牌（玩家 2 视角下对手手牌永不展开）");

            Assert.IsFalse(src.Contains("_localPlayerId == 1 ? _p2Hand : _p1Hand"),
                "**禁止** `_localPlayerId == 1 ? _p2Hand : _p1Hand` 这种直取面板的写法（镜像视图下语义反了）");
        }

        /// <summary>② 地区统计/势力标记的座位→半边映射必须跟着镜像换，否则自己的数字显示在对手那半边。</summary>
        public void TestMirror_地区统计与标记跟镜像换半边()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("slot.MirrorSeats = ViewMirrored;"),
                "建地区槽位时必须把镜像标志下发（且要在 Setup 之前 —— Setup 末尾会算一次统计）");

            string slot = Read("Script/UI/AreaSlot.cs");
            Assert.IsFalse(string.IsNullOrEmpty(slot), "必须能读到 AreaSlot.cs");
            Assert.IsTrue(slot.Contains("public bool MirrorSeats"),
                "AreaSlot 必须暴露 MirrorSeats（单机恒 false，不改变单机表现）");
            Assert.IsTrue(slot.Contains("if (MirrorSeats)") && slot.Contains("ApplyStats(_player1StatsLabel, p2)"),
                "镜像时 Player1Area（下方=本方）的统计必须取座位 2 的数字");
            Assert.IsTrue(slot.Contains("_areaCardUI.UpdateMarks(p2, p1, localFirst: true)"),
                "镜像时势力标记的文案要改「本方/对手」（P1 此刻是对手，继续标 P1 会指错人）");

            string card = Read("Script/UI/AreaCardUI.cs");
            Assert.IsTrue(card.Contains("bool localFirst = false"),
                "AreaCardUI.UpdateMarks 需要 localFirst 开关（默认 false ⇒ 单机文案逐字不变）");
        }
    }
}
