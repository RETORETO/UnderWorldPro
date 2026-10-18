namespace Script.Tests
{
    /// <summary>
    /// 【2026-10-19】UI 收尾批次的三条**源码级契约**（headless 下点不了 UI，故用文本锚点）：
    ///
    /// ① 资产区折叠：`ArrangeContainerCards(..., pile: true)` 必须走 `CardLayoutHelper.LayoutPile`
    ///    并挂 `CardCountBadge` 的「×N」——否则一堆资产会各自平铺把 400×210 的区挤爆。
    /// ② 资产区 / 墓地区悬停展卡：两个视图都必须每帧轮询（`TickPilePeek`）。
    ///    理由与对手手牌同一课：面板是浮层，"从面板移到空白处"没有任何 hover 事件，
    ///    只用 `mouse_entered/exited` 会让面板永久挂住。
    /// ③ 联机选卡窗：`SelectZoneCard` / `SelectHandCard` / `SelectDeckCard` 必须转到
    ///    `OpenOnlineCardPickWindow`（真卡面 + 区域标签），且回包仍是 `ChoiceResult.SelectedUid`
    ///    ——协议零改动；隐藏占位由 `RebuildFromRef` 建 `IsHidden` 实例，窗内自动只画卡背。
    /// </summary>
    public class UiPilePickTests : TestRunner
    {
        private static string Read(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            if (f == null) return string.Empty;
            return f.GetAsText();
        }

        /// <summary>① 资产区折叠成一摞 + 数量角标。</summary>
        public void TestPile_资产区折叠为一张且带数量角标()
        {
            string helper = Read("Script/UI/CardLayoutHelper.cs");
            Assert.IsTrue(helper.Contains("public static void LayoutPile("),
                "必须有通用的 LayouPile（资产区复用；对手手牌是它的语义别名）");
            Assert.IsTrue(helper.Contains("=> LayoutPile(container, cards)"),
                "LayoutCompactBacks 必须委托 LayoutPile（同一份实现，避免两处漂移）");

            string arena = Read("Script/UI/Arena.Layout.cs");
            Assert.IsTrue(arena.Contains("ArrangeContainerCards(_assetContainer1, pile: true)"),
                "单机资产区必须走折叠分支");
            Assert.IsTrue(arena.Contains("CardLayoutHelper.LayoutPile(container, list)"),
                "单机资产区折叠必须调 LayoutPile");
            Assert.IsTrue(arena.Contains("CardCountBadge.Apply("),
                "折叠后必须挂「×N」角标（否则玩家读不出有几张）");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("CardLayoutHelper.LayoutPile(container, cards)"),
                "联机资产区也必须折叠");
            Assert.IsTrue(ol.Contains("CardCountBadge.Apply(topUi, cards.Count)"),
                "联机折叠后同样要挂数量角标");
        }

        /// <summary>② 悬停展卡必须每帧矩形轮询（两个视图都要）。</summary>
        public void TestPile_资产与墓地悬停展卡走每帧轮询()
        {
            foreach (var path in new[] { "Script/UI/Arena.BadgePeek.cs", "Script/UI/ArenaOl.cs" })
            {
                string src = Read(path);
                Assert.IsFalse(string.IsNullOrEmpty(src), $"必须能读到 {path}");
                Assert.IsTrue(src.Contains("TickPilePeek"),
                    $"{path}：资产/墓地悬停必须有每帧轮询入口");
                Assert.IsTrue(src.Contains("TryAssetPile") && src.Contains("TryGravePile"),
                    $"{path}：必须同时覆盖资产区与墓地区");
                Assert.IsTrue(src.Contains("GetGlobalRect().HasPoint(mouse)"),
                    $"{path}：命中判定必须按矩形（不能用 mouse_entered/exited —— 面板浮层不受事件驱动约束）");
            }
        }

        /// <summary>③ 联机选卡窗：三种"从区域选一张"都走 CardPickWindow，回包仍是 SelectedUid。</summary>
        public void TestPick_联机选卡窗走真卡面且协议不变()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");

            Assert.IsTrue(ol.Contains("Kind.SelectZoneCard") && ol.Contains("Kind.SelectHandCard")
                          && ol.Contains("Kind.SelectDeckCard") && ol.Contains("OpenOnlineCardPickWindow(req)"),
                "墓地/手牌/牌库三种选一张必须转到联机选卡窗");

            Assert.IsTrue(ol.Contains("mgr.RebuildFromRef(r)"),
                "候选卡面必须由 OptionCardRefs 重建（隐藏占位 ⇒ IsHidden ⇒ 窗内只画卡背）");
            Assert.IsTrue(ol.Contains("SelectedUid = inst?.UID"),
                "回包必须仍是 ChoiceResult.SelectedUid（协议零改动）");
            Assert.IsTrue(ol.Contains("Cancelled = true"),
                "取消出口必须回 Cancelled（否则服务端要等超时）");
            Assert.IsTrue(ol.Contains("AllowCancel = allowCancel"),
                "是否可取消必须跟 ForceChoice 走，不能一律给取消按钮");
        }
    }
}
