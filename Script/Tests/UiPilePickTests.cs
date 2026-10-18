namespace Script.Tests
{
    using Script.UI;

    /// <summary>
    /// 【2026-10-19】UI 收尾批次的**源码级契约**（headless 下点不了 UI，故用文本锚点）。
    ///
    /// ① 折叠口径（**同日二次修订**，以用户最新口径为准）：
    ///    · **资产区**：回退为**层叠平铺**（`LayoutStacked`，每张露 36px），**不再**叠成一摞 + 「×N」；
    ///    · **对手手牌**：回退为**常规平铺**（`LayoutCardsEvenly`），折叠整段（`ArrangeOpponentHandBacks` /
    ///      `LayoutCompactBacks` / `_opponentHandExpanded`）必须彻底消失，否则会又塌成一摞。
    /// ② 资产区 / 墓地区悬停展卡：两个视图都必须每帧轮询（`TickPilePeek`）——资产区回退成层叠后，
    ///    卡片互相压着，必须靠这个列表才能一次看全所有卡面。
    ///    理由与对手手牌同一课：面板是浮层，"从面板移到空白处"没有任何 hover 事件，
    ///    只用 `mouse_entered/exited` 会让面板永久挂住。
    /// ③ 联机选卡窗：`SelectZoneCard` / `SelectHandCard` / `SelectDeckCard` 必须转到
    ///    `OpenOnlineCardPickWindow`（真卡面 + 区域标签），且回包仍是 `ChoiceResult.SelectedUid`
    ///    ——协议零改动；隐藏占位由 `RebuildFromRef` 建 `IsHidden` 实例，窗内自动只画卡背。
    /// ④ 开局调度：必须走 `CardMultiPickWindow`（真卡面多选），不得退回勾选框列表。
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

        /// <summary>① 资产区恢复层叠平铺、对手手牌恢复常规平铺（折叠整段不得残留）。</summary>
        public void TestPile_资产与对手手牌均已回退为平铺()
        {
            string arena = Read("Script/UI/Arena.Layout.cs");
            Assert.IsFalse(string.IsNullOrEmpty(arena), "必须能读到 Arena.Layout.cs");
            Assert.IsFalse(arena.Contains("ArrangeContainerCards(Control container, bool pile"),
                "资产区排布函数不得再有 pile 参数（折叠成一摞已按用户口径回退）");
            Assert.IsFalse(arena.Contains("CardLayoutHelper.LayoutPile("),
                "单机资产区不得再调 LayoutPile");
            Assert.IsTrue(arena.Contains("CardLayoutHelper.LayoutStacked(container, list)"),
                "资产区/分数区都必须走 LayerStacked（每张露 36px 边）");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");
            Assert.IsFalse(ol.Contains("CardLayoutHelper.LayoutPile(container, cards)"),
                "联机资产区不得再调 LayoutPile");
            Assert.IsTrue(ol.Contains("CardLayoutHelper.LayoutStacked(container, cards)"),
                "联机资产区必须走 LayoutStacked");

            // 对手手牌：折叠相关的六个符号必须全部消失（留任何一个都可能被重新接回）
            foreach (var token in new[]
                     {
                         "ArrangeOpponentHandBacks", "AreAllHiddenBacks", "IsOpponentHandCtr",
                         "UpdateOpponentHandHover", "_opponentHandExpanded", "OpponentHandEdge",
                     })
            {
                Assert.IsFalse(ol.Contains(token),
                    $"对手手牌折叠残留：{token}（用户口径要的是「折叠UI回退」）");
            }
            Assert.IsTrue(ol.Contains("CardLayoutHelper.LayoutCardsEvenly(container, cards)"),
                "对手手牌必须回到常规均匀平铺（逐张牌背并排）");

            // 几何函数本身保留（有 P5LayoutMathTests 锁定），只是当前无调用点
            string helper = Read("Script/UI/CardLayoutHelper.cs");
            Assert.IsTrue(helper.Contains("public static void LayoutPile("),
                "LayoutPile 作为几何工具保留（将来做真·牌堆时复用）");
        }

        /// <summary>①b 牌叠错位封顶：张数越多步长越小，避免整叠斜出面板。</summary>
        public void TestPile_牌叠错位按总位移封顶()
        {
            // 3 张、基准 0.5、总位移上限 10 ⇒ 步长仍是 0.5（(3-1)*0.5=1 ≤ 10）
            float few = CardLayoutHelper.PileStep(3, 0.5f, 10f);
            Assert.IsTrue(global::Godot.Mathf.Abs(few - 0.5f) < 1e-4f,
                $"张数少时保留基准步长（实得 {few}）");

            // 40 张、基准 2、上限 10 ⇒ 步长被压到 10/39
            float many = CardLayoutHelper.PileStep(40, 2f, 10f);
            Assert.IsTrue(global::Godot.Mathf.Abs(many - 10f / 39f) < 1e-4f,
                $"张数多时步长压缩到 10/39（实得 {many}）");
            Assert.IsTrue(many * 39f <= 10f + 1e-3f, "总位移不得越过上限");

            float single = CardLayoutHelper.PileStep(1, 2f, 10f);
            Assert.IsTrue(global::Godot.Mathf.Abs(single) < 1e-4f, "单张牌堆没有错位");
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

        /// <summary>②b 资产/墓地列表的**双击出口**必须分派到正确链路（JC085 墓地行动 / 其它激活）。</summary>
        public void TestPile_资产与墓地列表双击分派到墓地行动与激活链路()
        {
            // 单机：分派到 _onGraveyardPlayRequestedForward（墓地行动）或 _onAttachmentDoubleClickedForward（激活）
            string arena = Read("Script/UI/Arena.BadgePeek.cs");
            Assert.IsFalse(string.IsNullOrEmpty(arena), "必须能读到 Arena.BadgePeek.cs");
            Assert.IsTrue(arena.Contains("ShowNear(host, title, members, OnPileMemberPicked, OnPileMemberActivated)"),
                "单机：资产/墓地列表的双击出口必须是 OnPileMemberActivated（不得是空实现 `_ => { }`）");
            Assert.IsTrue(arena.Contains("_onGraveyardPlayRequestedForward?.Invoke(ui)"),
                "单机：墓地行动卡双击必须走 _onGraveyardPlayRequestedForward（从墓地正面打出，JC085）");
            Assert.IsTrue(arena.Contains("_onAttachmentDoubleClickedForward?.Invoke(ui)"),
                "单机：非墓地行动卡双击必须走 _onAttachmentDoubleClickedForward（激活链路）");
            Assert.IsTrue(arena.Contains("CurrentZone == CardZone.Graveyard"),
                "单机：分派判据必须是『在墓地』（其它区域走激活链路）");

            // 联机：分派到 OnGraveyardPlayRequested（上行打出意图）或 OnAttachmentDoubleClicked（上行激活意图）
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");
            Assert.IsTrue(ol.Contains("ShowNear(host, title, members, OnPileMemberPicked, OnPileMemberActivated)"),
                "联机：资产/墓地列表的双击出口必须是 OnPileMemberActivated（不得是空实现 `_ => { }`）");
            Assert.IsTrue(ol.Contains("OnGraveyardPlayRequested(ui)"),
                "联机：墓地行动卡双击必须走 OnGraveyardPlayRequested（服务端校验打出）");
            Assert.IsTrue(ol.Contains("OnAttachmentDoubleClicked(ui)"),
                "联机：非墓地行动卡双击必须走 OnAttachmentDoubleClicked（上行激活意图）");
        }

        /// <summary>②c 墓地错位必须与牌库同源（`PileStep(count, 0.25f, 10f)`），不得写死 5px。</summary>
        public void TestPile_墓地错位与牌库同源()
        {
            string grave = Read("Script/UI/GraveyardZone.cs");
            Assert.IsFalse(string.IsNullOrEmpty(grave), "必须能读到 GraveyardZone.cs");
            Assert.IsTrue(grave.Contains("CardLayoutHelper.PileStep(_graveyardCards.Count, 0.25f, 10f)"),
                "墓地错位必须与牌库一致（基准 0.25px/张 + 总位移封顶 10px），不得写死 5px 导致斜出面板");
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

        /// <summary>④ 开局调度改用多选卡面窗（与选卡窗同一套观感）。</summary>
        public void TestMulligan_开局调度走多选卡面窗()
        {
            string win = Read("Script/UI/CardMultiPickWindow.cs");
            Assert.IsFalse(string.IsNullOrEmpty(win),
                "必须存在 CardMultiPickWindow（多选卡面窗）");
            Assert.IsTrue(win.Contains("public void Init(CardMultiPickRequest req)"),
                "多选窗必须走 `AddChild` 之后再 `Init`（CardUI._Ready 会 SetFace(false) 盖掉正面）");
            Assert.IsTrue(win.Contains("SetPickedHighlight(true)") && win.Contains("SetPickedHighlight(false)"),
                "点卡必须能来回切换选中（再点一次＝取消选中）");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("new CardMultiPickWindow()"),
                "ArenaOl 的调度必须弹出多选卡面窗");
            Assert.IsTrue(ol.Contains("ConfirmLabel = \"确认调度\""),
                "确认按钮文案保持「确认调度」");
            Assert.IsTrue(ol.Contains("Kind.MulliganReport"),
                "回包仍是 MulliganReport 意图（协议零改动）");
            Assert.IsTrue(ol.Contains("AllowCancel = false"),
                "开局调度是固定流程：不给取消出口");
        }
    }
}
