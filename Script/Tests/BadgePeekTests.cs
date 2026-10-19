namespace Script.Tests
{
    /// <summary>
    /// 【2026-10-18 用户口径·角标展开面板】锁定两条**交互契约**（源码级，headless 下点不了 UI，故用文本锚点）：
    ///
    /// ① **封印卡与附属卡分开展示**——两者来自两个不同区域，绝不能混在一个列表里：
    ///    每类角标（`封×N` / `附×N`）各自是独立 hover 命中区，悬浮哪一类就只展开那一类。
    /// ② **面板里的卡双击＝发动激活能力**（如 JC020 引匕的飞刀：横置自身 + 横置目标暗藏者）：
    ///    附属/封印卡被折叠成角标后，场上已经没有可双击的卡面 ⇒ 双击入口必须由展开面板补回来，
    ///    且必须走**既有双击链路**（`_onAttachmentDoubleClickedForward` / `OnAttachmentDoubleClicked`），
    ///    不能自己另开一条绕开门禁的通道。
    ///
    /// 为什么值得单测：这两条都是"看着像小 UI、实际决定效果能不能用"的契约。
    ///   · 混排 ⇒ 玩家在"附"里点开却看到封印卡，选错对象；
    ///   · 双击入口丢失 ⇒ JC020 这类附属彻底变成死卡（用户实测反馈的原始问题）。
    /// </summary>
    public class BadgePeekTests : TestRunner
    {
        private static string Read(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            if (f == null) return string.Empty;
            return f.GetAsText();
        }

        /// <summary>① 角标分家：每类角标独立 hover，回调带 `CardBadgeKind`。</summary>
        public void TestBadge_封印与附属各自独立悬浮()
        {
            string src = Read("Script/UI/CardBadges.cs");
            Assert.IsFalse(string.IsNullOrEmpty(src), "必须能读到 CardBadges.cs");

            Assert.IsTrue(src.Contains("enum CardBadgeKind"), "必须用枚举区分角标种类（封印/附属）");
            Assert.IsTrue(src.Contains("CardBadgeKind.Seal") && src.Contains("CardBadgeKind.Attach"),
                "封印与附属必须各成一种角标");
            Assert.IsTrue(src.Contains("OnBadgeHover?.Invoke(capturedHost, capturedKind)"),
                "hover 回调必须携带角标种类（否则视图无法只展开对应那一种）");
            Assert.IsTrue(src.Contains("MouseFilter = Control.MouseFilterEnum.Ignore"),
                "角标根节点只作布局容器（Ignore），hover 必须由两个标签各自接收");
        }

        /// <summary>② 展开列表必须按种类过滤：封印只认 SealedBy，附属只认 AttachedTo；禁止无过滤混排。</summary>
        public void TestBadge_展开列表按种类过滤()
        {
            foreach (var path in new[] { "Script/UI/Arena.BadgePeek.cs", "Script/UI/ArenaOl.cs" })
            {
                string src = Read(path);
                Assert.IsFalse(string.IsNullOrEmpty(src), $"必须能读到 {path}");

                Assert.IsTrue(src.Contains("kind == CardBadgeKind.Seal"),
                    $"{path}：必须按角标种类分支（封印 / 附属）");
                Assert.IsTrue(src.Contains("ReferenceEquals(s.SealedBy, inst)"),
                    $"{path}：封印卡必须校验 SealedBy == 宿主（不混入封印于地区的卡）");
                Assert.IsTrue(src.Contains("ReferenceEquals(m.AttachedTo, inst)"),
                    $"{path}：附属卡必须校验 AttachedTo == 宿主（不混入结附于地区的卡）");
                Assert.IsFalse(src.Contains("if (m != null) members.Add(m);"),
                    $"{path}：**禁止**无过滤地把附属塞进列表（旧实现＝封印/附属混排，用户已否决）");
            }
        }

        /// <summary>③ 双击＝激活：面板卡片双击必须转调既有双击激活链路，单击仍只置选中。</summary>
        public void TestBadge_面板内双击发动激活能力()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsFalse(string.IsNullOrEmpty(peek), "必须能读到 CardBadgePeek.cs");

            Assert.IsTrue(peek.Contains("Action<CardUI> onActivate"),
                "展开面板必须暴露 onActivate 出口（双击发动）");
            Assert.IsTrue(peek.Contains("mb.DoubleClick") && peek.Contains("_onActivate?.Invoke(capturedUi)"),
                "双击（DoubleClick）必须走 onActivate；单击仍走 onPick（只置选中、不提交）");

            string arena = Read("Script/UI/Arena.BadgePeek.cs");
            Assert.IsTrue(arena.Contains("_onAttachmentDoubleClickedForward?.Invoke(ui)"),
                "单机：双击必须复用既有双击链路（来源判定/能力枚举/门禁日志一致），不得另开通道");

            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("OnAttachmentDoubleClicked(ui)"),
                "联机：双击必须走上行激活意图的既有链路（服务端权威校验）");
        }

        // ============================================================
        // 【2026-10-19】"hover 展开附属卡后点那张卡，窗口直接关、JC020 发不动" 的回归契约。
        //
        // 事故是三处叠加，缺任何一处的锁，问题都会复发：
        //  ① 面板里的卡是真 `CardUI`，`MouseEntered` 会申请**整卡预览**（同层 830、后建 ⇒ 盖住面板），
        //     鼠标落点变成"在预览上" ⇒ 面板被判成"鼠标离开"自动收起；
        //  ② 联机端把**单击**出口写成了"收起面板"，而双击的第一次按下就是单击 ⇒ 第二次按下没面板可点；
        //  ③ 两个视图的 `_Input` 先于 `_GuiInput`，命中场上卡时 `AcceptEvent()` 把左键整条吞掉。
        // ============================================================

        /// <summary>① 面板内卡面只作画、不抢鼠标：输入必须由命中槽转交（否则整卡预览会盖住面板）。</summary>
        public void TestBadge_面板内卡面只作画不抢鼠标()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsFalse(string.IsNullOrEmpty(peek), "必须能读到 CardBadgePeek.cs");

            Assert.IsTrue(peek.Contains("slot.GuiInput += ev => HandleMemberInput"),
                "点击/双击必须由**命中槽**转发（CardUI 自己收会连带触发整卡预览浮层，把面板盖住）");
            Assert.IsTrue(peek.Contains("ui.MouseFilter = MouseFilterEnum.Ignore"),
                "内层 CardUI 必须压成 Ignore（只作画）：否则 MouseEntered 会弹出整卡预览与面板抢同一层");
            Assert.IsTrue(peek.Contains("MouseFilter = MouseFilterEnum.Stop"),
                "命中槽必须是 Stop（Pass/Ignore 都收不到 GuiInput）");
        }

        /// <summary>② 单击**绝不关闭**面板：双击的第一次按下就是单击，关了就没有第二次。</summary>
        public void TestBadge_单击不关闭展开面板()
        {
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsFalse(ol.Contains("_ => _badgePeek?.HideNow()"),
                "联机：禁止把单击出口写成『收起面板』——双击的第一次按下会被它吃掉（用户实测根因）");
            Assert.IsTrue(ol.Contains("ShowNear(host, title, members, OnPileMemberPicked, OnBadgeMemberActivated)"),
                "联机：单击出口必须是『置选中/纯浏览』，不能收起面板");

            string arena = Read("Script/UI/Arena.BadgePeek.cs");
            Assert.IsTrue(arena.Contains("_badgePeek.ShowNear(host, title, members, OnBadgeMemberPicked, OnBadgeMemberActivated)"),
                "单机：单击出口必须保持『只置选中、不提交』，且不收起面板");
        }

        /// <summary>③ 双击判定要自带时间窗（`_Input` 吞掉第一次按下时，事件上的 DoubleClick 拿不到）。</summary>
        public void TestBadge_双击判定自带时间窗()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsTrue(peek.Contains("DoubleClickMs"),
                "必须自持双击时间窗常量（事件标记可能被全局输入吞掉）");
            Assert.IsTrue(peek.Contains("mb.DoubleClick || sameCard"),
                "双击＝事件标记 **或** 同一张卡在时间窗内的第二次按下");
            Assert.IsTrue(peek.Contains("_lastClickUid = isDoubleClick ? null : uid"),
                "双击后必须清记账，避免把第三次点击又算成一次双击");
        }

        /// <summary>④ 视图全局输入必须对『面板内落点』放行（`_Input` 先于 `_GuiInput` 且会 AcceptEvent）。</summary>
        public void TestBadge_全局输入放行面板内落点()
        {
            foreach (var path in new[] { "Script/UI/Arena.cs", "Script/UI/ArenaOl.cs" })
            {
                string src = Read(path);
                Assert.IsFalse(string.IsNullOrEmpty(src), $"必须能读到 {path}");
                Assert.IsTrue(src.Contains("_badgePeek.ContainsGlobalPoint(peekMb.GlobalPosition)"),
                    $"{path}：落点在可见展开面板内时，全局输入必须 return 放行给 GUI");
            }
        }

        /// <summary>⑤ 展开面板必须**照实表现横置**（旋转 90° + 槽位换成 (卡高, 卡宽)）。</summary>
        public void TestBadge_展开面板显示横置卡()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsTrue(peek.Contains("bool tapped = inst.IsTapped"),
                "面板必须读卡的横置状态");
            Assert.IsTrue(peek.Contains("float slotW = tapped ? cardH : cardW"),
                "横置卡的槽位必须是 (卡高, 卡宽)（视觉包围盒换成横的）");
            Assert.IsTrue(peek.Contains("ui.SetTapped(true)") && peek.Contains("ui.RotationDegrees = 90f"),
                "横置卡必须真的旋转 90°（玩家靠它判断本回合还能不能横置自身，如 JC020 的代价）");
        }

        // ============================================================
        // 【2026-10-19】两条"看着像布局、实际决定功能能不能用"的契约：
        //  A. **链式选择窗必须先关本窗、再跑回调**——否则下一级窗（墓地行动的"选择地区"）
        //     会被上一级仍然可见的模态 popup 压掉，用户看到的就是"点了没有任何反应"。
        //  B. **面板内可激活的卡必须画绿框**——成员卡被折叠成角标后场上没有可看的卡面，
        //     玩家只能从这个面板判断"这张能不能发动"，不画就等于不可发现。
        // ============================================================

        /// <summary>A. 链式选择窗：`Hide()` 必须在 `chosen` 回调之前执行（单机/联机两份都要守）。</summary>
        public void TestWindow_链式选择窗先关本窗再开下一窗()
        {
            string online = Read("Script/UI/OnlineActivationChoiceWindow.cs");
            Assert.IsFalse(string.IsNullOrEmpty(online), "必须能读到 OnlineActivationChoiceWindow.cs");
            int hideIdx = online.IndexOf("private void ChooseAndClose", System.StringComparison.Ordinal);
            Assert.IsTrue(hideIdx >= 0, "必须能定位 ChooseAndClose（联机选项窗的唯一选择出口）");
            int hideCall = online.IndexOf("Hide();", hideIdx, System.StringComparison.Ordinal);
            int invoke = online.IndexOf("chosen?.Invoke();", hideIdx, System.StringComparison.Ordinal);
            Assert.IsTrue(hideCall > 0 && invoke > 0, "ChooseAndClose 里必须同时有 Hide() 与 chosen?.Invoke()");
            Assert.IsTrue(hideCall < invoke,
                "联机：必须先 Hide() 再跑 chosen 回调（QueueFree 是帧末延迟释放，挡不住 popup 秩序）");

            string offline = Read("Script/UI/ActivationChoiceWindow.cs");
            int offlineInvoke = offline.IndexOf("_items[captured].OnChosen?.Invoke();", System.StringComparison.Ordinal);
            Assert.IsTrue(offlineInvoke > 0, "必须能定位单机 OnChosen 回调");
            int from = System.Math.Max(0, offlineInvoke - 200);
            string beforeInvoke = offline.Substring(from, offlineInvoke - from);
            Assert.IsTrue(beforeInvoke.Contains("Dismiss();"),
                "单机：必须保持『先 Dismiss 再跑 OnChosen』（A74 既有结论，勿回退）");

            string ol = Read("Script/UI/ArenaOl.cs");
            int openWin = ol.IndexOf("private void OpenOnlineChoiceWindow", System.StringComparison.Ordinal);
            Assert.IsTrue(openWin >= 0, "必须能定位 OpenOnlineChoiceWindow");
            int oldHide = ol.IndexOf("_onlineChoiceWindow.Hide();", openWin, System.StringComparison.Ordinal);
            int oldFree = ol.IndexOf("_onlineChoiceWindow.QueueFree();", openWin, System.StringComparison.Ordinal);
            Assert.IsTrue(oldHide > 0 && oldFree > 0 && oldHide < oldFree,
                "联机：销毁旧选项窗必须先 Hide() 再 QueueFree()（否则新窗与旧窗共处一帧会被压掉）");
        }

        /// <summary>B. 面板内可激活的卡必须显示绿框（探针由视图注入，面板本身不判定规则）。</summary>
        public void TestBadge_面板内可激活卡显示高亮()
        {
            string peek = Read("Script/UI/CardBadgePeek.cs");
            Assert.IsTrue(peek.Contains("ActivatableProbe"),
                "面板必须暴露 ActivatableProbe 探针（规则判定留在视图/Controller，面板只负责画）");
            Assert.IsTrue(peek.Contains("SetActivatableHighlight("),
                "造卡后必须调用 SetActivatableHighlight（否则折叠进面板的卡永远是白框）");

            string arena = Read("Script/UI/Arena.BadgePeek.cs");
            Assert.IsTrue(arena.Contains("ActivatableProbe"),
                "单机：必须给面板注入可发动探针（与场上高亮同源）");
            string ol = Read("Script/UI/ArenaOl.cs");
            Assert.IsTrue(ol.Contains("ActivatableProbe"),
                "联机：必须给面板注入可发动探针（服务端投影优先）");
        }
    }
}
