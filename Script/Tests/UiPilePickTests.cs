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

        /// <summary>
        /// ②d 【JC085 修复】联机"墓地行动"第二级选地区必须取 `ClientAreaManager.GetOrderedAreaUids()`，
        /// 不得用 `ClientZoneManager.GetZoneCards(AreaZone, 0)`——联机快照里地区卡注册在 `AreaManager._areas`，
        /// 不在 ZoneManager 的 `_area` 桶，后者拿不到 ⇒ areas.Count==0 ⇒ 选地区窗永远弹不出来。
        /// </summary>
        public void TestPile_联机墓地行动选地区走AreaManager()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");
            Assert.IsTrue(ol.Contains("ClientAreaManager?.GetOrderedAreaUids()"),
                "联机墓地行动选地区必须取 ClientAreaManager.GetOrderedAreaUids()（与单机 OpenGraveyardAreaChoice 同源）");
            Assert.IsFalse(ol.Contains("clientZone?.GetZoneCards(CardZone.AreaZone"),
                "联机墓地行动禁止用 ClientZoneManager.GetZoneCards(AreaZone) 拿地区（快照里地区卡注册在 AreaManager，不在该桶）");
        }

        /// <summary>
        /// ②e 【JC107 修复】选卡窗"**点窗外 ≠ 取消**"：去掉 `FocusExited` 订阅，取消只走显式"取消"按钮/关闭按钮。
        /// 用户口径："点击 HOVER 区域内卡片 UI 等效窗口内选择了同一张卡"——点 hover 面板卡走
        /// `OnPileMemberPicked → SelectByUid`（选中窗口内同一张卡），而不是触发 `FocusExited` 把选卡窗取消掉。
        /// </summary>
        public void TestPick_选卡窗点窗外不取消()
        {
            string win = Read("Script/UI/CardPickWindow.cs");
            Assert.IsFalse(string.IsNullOrEmpty(win), "必须能读到 CardPickWindow.cs");
            Assert.IsFalse(win.Contains("FocusExited += OnFocusExited"),
                "选卡窗禁止订阅 FocusExited（点窗外会触发取消，把『点 hover 面板卡』变成『取消发动』）");
            Assert.IsFalse(win.Contains("private void OnFocusExited()"),
                "选卡窗不得再有 OnFocusExited 取消处理（取消只走显式按钮）");
            Assert.IsTrue(win.Contains("public void SelectByUid(string uid)"),
                "选卡窗必须保留 SelectByUid（hover 面板点选等效窗口内选中同一张卡）");
        }

        /// <summary>
        /// ②f 【JC085 修复】联机"墓地行动"第二级选地区必须走**场上高亮地区 + 点击**（对齐单机
        /// `RequestActivateTargetSelection(isArea: true)`），不再弹「地区1/地区2/地区3」文字按钮窗。
        /// </summary>
        public void TestPile_联机墓地行动选地区走场上高亮()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");
            Assert.IsTrue(ol.Contains("_pendingGraveyardPlayUid = inst.UID"),
                "联机墓地行动选地区必须记录待打出的墓地卡 UID（地区点击后据此上行 PlayCard）");
            Assert.IsTrue(ol.Contains("请点击场上高亮的地区完成打出"),
                "联机墓地行动选地区必须走『场上高亮地区 + 点击』（用户口径），提示窗文案应引导点地区而非列文字按钮");
            Assert.IsTrue(ol.Contains("OnAreaTargetPicked"),
                "地区点击必须走统一入口 OnAreaTargetPicked（据上下文分发：服务端回 ChoiceResult / 墓地行动上行 PlayCard）");
        }

        /// <summary>
        /// ②g 【JC005 修复】选卡窗打开期间，点场上的**候选卡**（cat-1，如结附于地区的附属）⇒
        /// 等效窗口内选中同一张卡（`SelectByUid`），而不是走默认点击（拖拽/双击激活）导致"点场上卡无反馈"。
        /// </summary>
        public void TestPick_选卡窗打开时点场上候选卡等效选中()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(string.IsNullOrEmpty(ol), "必须能读到 ArenaOl.cs");
            Assert.IsTrue(ol.Contains("_onlinePickWindow.HasCard(hitCard.Instance.UID)"),
                "联机：选卡窗打开期间点场上候选卡必须走 SelectByUid（JC005 点结附于地区的附属）");

            string arena = Read("Script/UI/Arena.cs");
            Assert.IsFalse(string.IsNullOrEmpty(arena), "必须能读到 Arena.cs");
            Assert.IsTrue(arena.Contains("_activePickWindow.HasCard(hitCard.Instance.UID)"),
                "单机：选卡窗打开期间点场上候选卡必须走 SelectByUid（两端口径一致）");
        }

        /// <summary>
        /// ②h 【游戏结束不了】`CheckGameOver` 必须有平局出口：双方牌库均耗尽 / 双方总分相等时，
        /// 旧实现 `return 0`（不判胜），而 CheckGameOver 只在"卡进分数区"时触发 ⇒ 对局永久卡死。
        /// </summary>
        public void TestGameOver_平局出口不卡死()
        {
            string pm = Read("Script/Manager/PlayerManager.cs");
            Assert.IsFalse(string.IsNullOrEmpty(pm), "必须能读到 PlayerManager.cs");
            Assert.IsTrue(pm.Contains("BreakTieByFirst"),
                "游戏结束判定必须有平局出口（双方牌库均耗尽/双方总分相等时不能再 return 0 卡死）");
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

        /// <summary>④ 开局调度：候选＝自己手牌（全 cat-1）⇒ 场上手牌多选，**不弹卡面网格窗**。</summary>
        public void TestMulligan_调度按cat1走场上手牌多选()
        {
            string win = Read("Script/UI/CardMultiPickWindow.cs");
            Assert.IsFalse(string.IsNullOrEmpty(win),
                "CardMultiPickWindow 仍保留（分类异常 / 选目标窗不可用时的兜底）");
            Assert.IsTrue(win.Contains("public void Init(CardMultiPickRequest req)"),
                "多选窗必须走 `AddChild` 之后再 `Init`（CardUI._Ready 会 SetFace(false) 盖掉正面）");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("CardPickChannel.Classify(cards, localSeat)"),
                "调度必须先按 cat-1/cat-2 分类（自己手牌＝全 cat-1 ⇒ 不弹窗）");
            Assert.IsTrue(ol.Contains("BeginMulliganFieldPick"),
                "cat-1 调度必须走『场上手牌高亮 + 左键切换 + 提示窗确认』");
            Assert.IsTrue(ol.Contains("OnMultiConfirmed = () => SubmitMulliganPick"),
                "提示窗的确认必须落到调度上报");
            Assert.IsTrue(ol.Contains("PlayerIntent.Kind.MulliganReport"),
                "回包仍是 MulliganReport 意图（协议零改动）");
            Assert.IsTrue(ol.Contains("new CardMultiPickWindow()"),
                "CardMultiPickWindow 只作兜底，不能删");
        }
    }
}
