using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.UI;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10-20·卡面信息重排】11 条 UI 需求的**可回归契约**。
    ///
    /// 这一批改动全是"显示层"的，视觉正确性只能靠人眼验收；但其中有一半逻辑
    /// 一旦写错就会**静默退化**（角标顺序错、指示物不下发、张数被卡压住、红蓝互换），
    /// 改一次就得重新人眼过一遍全部界面。所以把"判据"抽成断言固定住：
    ///
    ///  · 需求 1/2/6 → `CardTextVisibility`（默认关、开关幂等、事件派发一次）；
    ///  · 需求 3/8   → `CardBadges.BuildCounterTexts` 文案口径 + `CardBadges.Apply` 行序（指示物在封/附之上）
    ///                 与命中属性（指示物 `Ignore` ＝无 hover，封/附 `Pass`）；
    ///  · 需求 4     → `CardRoleMarker.ApplyFor`（只有"结附/封印于**地区**"才打身份标，其余清掉）；
    ///  · 需求 7     → `AreaCardUI.UpdateInfluence`（红蓝互斥、只显示非零一方、座位值不镜像）；
    ///  · 需求 8     → `CardBadgeStyle.ApplyZoneCountLabel`（抬到绝对层 `Layers.ZoneCountLabel`，否则被卡压住）；
    ///  · 需求 11    → `PlayerSide.Name`（座位 1/2 恒为红/蓝，不随镜像是本方/对手）；
    ///  · 需求 10 前置 → 指示物与"秘社来源"必须**随快照下发**（不下发客户端永远画不出来）。
    ///
    /// 纪律：本文件只建**游离节点**（不入树）做属性断言，不加载 `.tscn`（`_Ready` 依赖场景导出字段）。
    /// </summary>
    public class UiTextFixTests : TestRunner
    {
        private static CardInstance NewCard(string uid, CardZone zone, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = uid, CategoryFlag = category };
            return new CardInstance(tmpl, uid) { OwnerId = 1, ControllerId = 1, CurrentZone = zone };
        }

        // ============================================================
        // 需求 1 / 2 / 6：卡面提示性文字只在 GM 输入框打开时显示
        // ============================================================

        /// <summary>
        /// 默认必须是**关闭**：提示性文字（类别/子类别/魔性/地区名等）平时不显示。
        /// 这条若被改成默认 true，等于这一批改动的目的（卡面清爽）整体回退。
        /// </summary>
        public void TestTextVisibility_默认关闭()
        {
            CardTextVisibility.Set(false);
            Assert.IsFalse(CardTextVisibility.ShowHintText, "提示性文字默认不显示（只有开 GM 输入框才显示）");
        }

        /// <summary>开关幂等 + `Changed` 只在真实翻转时派发一次（重复 Set 同一值不得触发重排）。</summary>
        public void TestTextVisibility_幂等且只派发一次()
        {
            CardTextVisibility.Set(false);
            int fired = 0;
            System.Action handler = () => fired++;
            CardTextVisibility.Changed += handler;
            try
            {
                CardTextVisibility.Set(false);
                Assert.AreEqual(0, fired, "重复设为同一值不应派发 Changed");

                CardTextVisibility.Set(true);
                Assert.IsTrue(CardTextVisibility.ShowHintText, "Set(true) 后应处于显示态");
                Assert.AreEqual(1, fired, "真实翻转只派发一次");

                CardTextVisibility.Set(true);
                Assert.AreEqual(1, fired, "再次设为同一值不应重复派发");

                CardTextVisibility.Toggle();
                Assert.IsFalse(CardTextVisibility.ShowHintText, "Toggle 应关闭");
                Assert.AreEqual(2, fired, "Toggle 也是一次真实翻转");
            }
            finally
            {
                CardTextVisibility.Changed -= handler;
                CardTextVisibility.Set(false);   // 复位，避免污染后续用例
            }
        }

        // ============================================================
        // 需求 3：指示物以「名称×N」显示，且排在封/附**上方**
        // ============================================================

        /// <summary>文案口径：`名称×N`；计数 ≤ 0 / 空名不显示（用户口径："只显示数量"）。</summary>
        public void TestCounterTexts_名称乘N口径()
        {
            var c = NewCard("c1", CardZone.AreaZone);
            c.AddCounter("时间标志", 2);
            c.AddCounter("锁定标志", 1);

            var texts = CardBadges.BuildCounterTexts(c);
            Assert.AreEqual(2, texts.Count, "两种指示物应各占一行");
            Assert.AreEqual("时间标志×2", texts[0], "指示物文案＝名称×数量");
            Assert.AreEqual("锁定标志×1", texts[1], "指示物文案＝名称×数量");

            // 数量归零的条目不应出现（AddCounter 负数/清零路径）
            c.AddCounter("时间标志", -2);
            var after = CardBadges.BuildCounterTexts(c);
            for (int i = 0; i < after.Count; i++)
                Assert.IsFalse(after[i].StartsWith("时间标志", System.StringComparison.Ordinal),
                    "数量≤0 的指示物不显示");

            Assert.AreEqual(0, CardBadges.BuildCounterTexts(null).Count, "空实例返回空列表，不抛异常");
        }

        /// <summary>
        /// 行序契约：**指示物（可多行，按传入顺序）→ 封 → 附**，整列自下而上生长。
        /// 用户口径"三者同时存在时指示物在最上方，封/附保持现在的顺序"。
        /// 同时锁定命中属性：指示物 `Ignore`（纯显示、不开 hover），封/附 `Pass`（保留悬浮展开）。
        /// </summary>
        public void TestBadges_行序指示物最上且命中属性正确()
        {
            var host = new Control { Name = "Host" };
            try
            {
                CardBadges.Apply(host, attached: 1, sealedCount: 2,
                    counterTexts: new List<string> { "时间标志×2", "锁定标志×1" });

                var root = host.GetNodeOrNull<Control>("__CardBadges");
                Assert.IsNotNull(root, "有任一行为非零时应创建角标根节点");

                int rowCount = 0;
                foreach (var child in root.GetChildren())
                    if (child is Label) rowCount++;
                Assert.AreEqual(4, rowCount, "2 指示物 + 封 + 附 = 4 行");

                // 纵向顺序做断言的**唯一依据**是 OffsetTop：越小越靠上，整列自下而上生长
                var c0 = root.GetNodeOrNull<Label>("Counter0");
                var c1 = root.GetNodeOrNull<Label>("Counter1");
                var seal = root.GetNodeOrNull<Label>("Seal");
                var attach = root.GetNodeOrNull<Label>("Attach");
                Assert.IsNotNull(c0, "指示物第 1 种有独立行");
                Assert.IsNotNull(c1, "指示物第 2 种有独立行");
                Assert.IsNotNull(seal, "封印行存在");
                Assert.IsNotNull(attach, "附属行存在");

                Assert.AreEqual("时间标志×2", c0.Text, "指示物文案＝名称×数量");
                Assert.AreEqual("锁定标志×1", c1.Text, "指示物按传入顺序依次向下");
                Assert.AreEqual("封×2", seal.Text, "封印文案保持既有口径");
                Assert.AreEqual("附×1", attach.Text, "附属文案保持既有口径");

                // 指示物在最上：OffsetTop 最小；封在附之上
                Assert.IsTrue(c0.OffsetTop < c1.OffsetTop, "指示物第 1 种排在指示物第 2 种之上");
                Assert.IsTrue(c1.OffsetTop < seal.OffsetTop, "指示物整体排在封印之上（用户口径：指示物最上）");
                Assert.IsTrue(seal.OffsetTop < attach.OffsetTop, "封/附顺序保持原样（封在上、附在下）");

                Assert.AreEqual(Control.MouseFilterEnum.Ignore, c0.MouseFilter, "指示物行不接 hover（纯显示）");
                Assert.AreEqual(Control.MouseFilterEnum.Ignore, c1.MouseFilter, "指示物行不接 hover（纯显示）");
                Assert.AreEqual(Control.MouseFilterEnum.Pass, seal.MouseFilter, "封印行保留悬浮展开");
                Assert.AreEqual(Control.MouseFilterEnum.Pass, attach.MouseFilter, "附属行保留悬浮展开");
            }
            finally
            {
                host.Free();
            }
        }

        /// <summary>指示物减到 0 时旧行必须被清掉（否则角标会残留成"幽灵数量"）。</summary>
        public void TestBadges_指示物清空后不残留旧行()
        {
            var host = new Control { Name = "Host" };
            try
            {
                CardBadges.Apply(host, 0, 0, new List<string> { "时间标志×2", "锁定标志×1" });
                var root = host.GetNodeOrNull<Control>("__CardBadges");
                Assert.IsNotNull(root, "两种指示物应建出角标");
                Assert.IsNotNull(root.GetNodeOrNull<Label>("Counter1"), "第 2 种指示物有独立行");

                CardBadges.Apply(host, 0, 0, new List<string> { "时间标志×1" });
                Assert.IsNull(root.GetNodeOrNull<Label>("Counter1"), "指示物种类减少后旧行应被移除");
                Assert.IsNotNull(root.GetNodeOrNull<Label>("Counter0"), "仍存在的行保留");

                CardBadges.Apply(host, 0, 0, new List<string>());
                Assert.IsNull(host.GetNodeOrNull<Control>("__CardBadges"),
                    "所有行归零后整块角标应被移除（不留空节点累积）");
            }
            finally
            {
                host.Free();
            }
        }

        // ============================================================
        // 需求 4：结附/封印于**地区**的卡，在左下角标出身份
        // ============================================================

        /// <summary>
        /// 只有"结附于地区"（`AttachedZone` + `AttachedTo == null`）与"封印于地区"
        /// （`SealedZone` + `SealedBy == null`）才打身份标；
        /// 结附于**卡/秘社**的（`AttachedTo != null`）属于 cat-2 折叠角标，**不能**再打身份标。
        /// </summary>
        public void TestRoleMarker_只标地区附属封印()
        {
            // 结附于地区
            var attachArea = NewCard("a1", CardZone.AttachedZone);
            attachArea.IsAttached = true;
            attachArea.AttachedTo = null;
            var hostA = new Control { Name = "A" };
            try
            {
                CardRoleMarker.ApplyFor(attachArea, hostA);
                var pill = hostA.GetNodeOrNull<Panel>("__CardRoleMarker");
                Assert.IsNotNull(pill, "结附于地区的附属应在卡面上打身份标");
                var text = pill.GetNodeOrNull<Label>("Text");
                Assert.IsNotNull(text, "身份标要有文字");
                Assert.AreEqual("附属", text.Text, "结附于地区的文案是「附属」");
            }
            finally { hostA.Free(); }

            // 封印于地区
            var sealArea = NewCard("s1", CardZone.SealedZone);
            sealArea.IsSeal = true;
            sealArea.SealedBy = null;
            var hostS = new Control { Name = "S" };
            try
            {
                CardRoleMarker.ApplyFor(sealArea, hostS);
                var pill = hostS.GetNodeOrNull<Panel>("__CardRoleMarker");
                Assert.IsNotNull(pill, "封印于地区的封印应打身份标");
                Assert.AreEqual("封印", pill.GetNodeOrNull<Label>("Text").Text, "封印于地区的文案是「封印」");
            }
            finally { hostS.Free(); }

            // 结附于卡（cat-2，折叠成宿主角标）⇒ 不打身份标
            var attachCard = NewCard("a2", CardZone.AttachedZone);
            attachCard.IsAttached = true;
            attachCard.AttachedTo = NewCard("host", CardZone.AreaZone);
            var hostC = new Control { Name = "C" };
            try
            {
                CardRoleMarker.ApplyFor(attachCard, hostC);
                Assert.IsNull(hostC.GetNodeOrNull<Panel>("__CardRoleMarker"),
                    "结附于卡/秘社的附属是 cat-2（折叠进宿主角标），不该有身份标");
            }
            finally { hostC.Free(); }

            // 争夺区角色 ⇒ 无身份标
            var hostD = new Control { Name = "D" };
            try
            {
                CardRoleMarker.ApplyFor(NewCard("ch", CardZone.AreaZone), hostD);
                Assert.IsNull(hostD.GetNodeOrNull<Panel>("__CardRoleMarker"), "争夺区角色无身份标");
            }
            finally { hostD.Free(); }
        }

        // ============================================================
        // 需求 7：地区势力点 → 红方势力 / 蓝方势力（互斥、只显示非零一方）
        // ============================================================

        /// <summary>
        /// 规则上红蓝不可能在同一地区同时有势力（`AreaManager.AddPowerMark` 先抵扣对方），
        /// 所以显示只取非零的一方；两端都为 0 时不显示。
        /// 断言的是**绝对座位语义**：p1 恒为"红方"、p2 恒为"蓝方"，不做本方/对手镜像。
        /// </summary>
        public void TestAreaInfluence_红蓝单方显示()
        {
            var ui = new AreaCardUI { Name = "AreaUI" };
            try
            {
                ui.UpdateInfluence(3, 0);
                var lb = ui.GetNodeOrNull<Label>("__InfluenceBadge");
                Assert.IsNotNull(lb, "应创建势力点角标");
                Assert.AreEqual("红方势力：3", lb.Text, "座位 1 的势力显示为「红方势力：X」");
                Assert.IsTrue(lb.Visible, "有势力时应显示");

                ui.UpdateInfluence(0, 5);
                Assert.AreEqual("蓝方势力：5", lb.Text, "座位 2 的势力显示为「蓝方势力：X」");

                ui.UpdateInfluence(0, 0);
                Assert.IsFalse(lb.Visible, "双方都无势力时不显示");
            }
            finally
            {
                ui.Free();
            }
        }

        // ============================================================
        // 需求 8：卡组/墓地张数角标要抬到绝对层，防止被卡背压住
        // ============================================================

        /// <summary>
        /// 用户实测"P2 侧张数被卡片挡住"。根因：容器里的卡会拿到 `ZIndex = i`（0..N-1），
        /// 而张数标签原先没有 `z_index` ⇒ 被后画的卡盖住。
        /// 契约：`ZAsRelative=false` + `ZIndex = Layers.ZoneCountLabel`（绝对层），且宽度放宽到能容纳"卡组×30"。
        /// </summary>
        public void TestZoneCountLabel_抬绝对层且放宽宽度()
        {
            var lb = new Label { Name = "Count", AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetLeft = -20f, OffsetRight = 20f };
            try
            {
                CardBadgeStyle.ApplyZoneCountLabel(lb);
                Assert.IsFalse(lb.ZAsRelative, "张数角标必须走绝对层（否则被容器内的卡压住）");
                Assert.AreEqual(Layers.ZoneCountLabel, lb.ZIndex, "张数角标取 Layers.ZoneCountLabel");
                Assert.IsTrue(lb.ZIndex > 399, "必须高于卡片层（300~399），否则仍会被卡面覆盖");
                Assert.AreEqual(-50f, lb.OffsetLeft, "左侧放宽，容纳「卡组×30」");
                Assert.AreEqual(50f, lb.OffsetRight, "右侧放宽，容纳「卡组×30」");
                Assert.AreEqual(CardBadgeStyle.Gold, lb.GetThemeColor("font_color"), "与封/附同款暖金字");

                // 幂等：重复调用不得再次改偏移（排版循环每帧都会调）
                CardBadgeStyle.ApplyZoneCountLabel(lb);
                Assert.AreEqual(-50f, lb.OffsetLeft, "重复调用幂等");
            }
            finally
            {
                lb.Free();
            }
        }

        // ============================================================
        // 【2026-10-20·第二批】层级不变式：牌库/墓地面板 vs 分数区 vs 张数角标
        // ============================================================

        /// <summary>
        /// 用户口径：
        ///  · 「分数区层级调低，调整到卡组和墓地层级之下」（分数区里赢取地区卡的卡面是 300~340）；
        ///  · 「对手玩家的卡组数量被遮挡」（张数角标压在容器内卡背之上）。
        /// 两条合成一组层级不变式：任何人再动图层，这里会先红。
        /// </summary>
        public void TestLayers_牌库墓地面板压分数区且低于拖拽层()
        {
            Assert.IsTrue(Layers.ZoneContainerPanel > Layers.CardBase + 39,
                "牌库/墓地面板必须高于容器内最上层的卡背（CardBase+39），否则分数区会盖住牌叠");
            Assert.IsTrue(Layers.ZoneContainerPanel > 340,
                "牌库/墓地面板必须高于分数区里赢取地区卡的卡面（300~340）");
            Assert.IsTrue(Layers.ZoneContainerPanel < Layers.CardDragging,
                "牌库/墓地面板必须低于拖拽层，否则拖拽中的卡会钻到面板底下");
            Assert.IsTrue(Layers.ZoneCountLabel > Layers.ZoneContainerPanel,
                "张数角标必须在面板之上（否则回到「数量被卡背/面板盖住」的老问题）");
            Assert.IsTrue(Layers.ZoneCountLabel < Layers.CommandInput,
                "张数角标必须低于 PM 命令框（命令框永远在最上）");
        }

        /// <summary>
        /// 战场场景必须能被解析（本批大量改了 `.tscn` 的 z_index / 锚点，写错一个属性就会整场景加载失败）。
        /// 只 `Load<PackedScene>`（解析资源），不 `Instantiate`（不建节点树、不跑 `_Ready`），代价很小。
        /// 单机 `Arena.tscn` 此前**没有任何测试覆盖**，所以它最容易在手工改场景时静默坏掉。
        /// </summary>
        public void TestArenaScenes_可解析()
        {
            var single = ResourceLoader.Load<PackedScene>("res://Scenes/Arena/Arena.tscn");
            Assert.IsNotNull(single, "Arena.tscn 应可加载（解析无错误）");
            var online = ResourceLoader.Load<PackedScene>("res://Scenes/Net/ArenaOl.tscn");
            Assert.IsNotNull(online, "ArenaOl.tscn 应可加载（解析无错误）");
        }

        // ============================================================
        // 需求 11：P1/P2 → 红方/蓝方（绝对座位，不随镜像交换）
        // ============================================================        /// <summary>
        /// 座位 1 恒为红方、座位 2 恒为蓝方。这一条**不得**按"本方/对手"镜像——
        /// 联机里两个客户端看到的同一张卡必须写同一个颜色名（用户口径"和争夺区配色朝向一致"）。
        /// </summary>
        public void TestPlayerSide_红蓝绝对命名()
        {
            Assert.AreEqual("红方", PlayerSide.Name(1), "座位 1 = 红方");
            Assert.AreEqual("蓝方", PlayerSide.Name(2), "座位 2 = 蓝方");
            Assert.IsTrue(PlayerSide.Name(0).Length > 0, "非法座位也要有非空回退名（不得抛异常）");
            Assert.AreEqual(CardBadgeStyle.RedSide, PlayerSide.ColorOf(1), "红方色与角标红同源");
            Assert.AreEqual(CardBadgeStyle.BlueSide, PlayerSide.ColorOf(2), "蓝方色与角标蓝同源");
        }

        // ============================================================
        // 需求 3 / 10 的前置：数据必须随快照下发
        // ============================================================

        /// <summary>
        /// 指示物此前**完全不下发**（`ZoneCardRef` 没有该字段）⇒ 联机客户端永远画不出"时间标志×2"。
        /// 契约：`ZoneManager.ProjectCard` 逐项投影，`SnapshotSerializer` 往返不丢（`IncludeFields` + 字典）。
        /// </summary>
        public void TestSnapshot_指示物随投影下发且往返不丢()
        {
            var c = NewCard("c9", CardZone.AreaZone);
            c.AddCounter("时间标志", 2);
            c.AddCounter("锁定标志", 1);

            var r = ZoneManager.ProjectCard(c, CardZone.AreaZone, 1);
            Assert.IsNotNull(r, "投影不得为空");
            Assert.IsNotNull(r.Counters, "投影必须带指示物字典");
            Assert.AreEqual(2, r.Counters.Count, "两种指示物都要投影");
            Assert.AreEqual(2, r.Counters["时间标志"], "数量原样投影");

            var snap = new GameSnapshot();
            snap.Zones.Add(r);

            var back = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(snap));
            Assert.IsNotNull(back, "快照往返不得为 null");
            var backRef = back.Zones[0];
            Assert.IsNotNull(backRef.Counters, "往返后指示物不得丢失（否则联机卡面无角标）");
            Assert.AreEqual(2, backRef.Counters["时间标志"], "往返后数量一致");
        }

        /// <summary>
        /// 秘社发动的效果进堆叠时：来源模板 UID 必须能取到（秘社没有 `PlayerTemplate`），
        /// 且必须打上 `SourceIsCouncil` 标记 —— 客户端据此走 `CouncilTemplate` 卡图路径。
        /// 这是需求 10（"秘社效果入堆叠没有正确显示"）的数据侧契约。
        /// </summary>
        public void TestSnapshot_秘社来源标记可往返()
        {
            var snap = new GameSnapshot();
            snap.Stack = new List<StackEntrySnap>
            {
                new StackEntrySnap
                {
                    OwnerId = 1,
                    EffectText = "秘社效果",
                    IsCard = false,
                    SourceTemplateUid = "COUNCIL_X",
                    SourceIsCouncil = true,
                },
            };

            var back = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(snap));
            Assert.IsNotNull(back, "快照往返不得为 null");
            Assert.IsNotNull(back.Stack, "堆叠条目不得丢失");
            Assert.AreEqual("COUNCIL_X", back.Stack[0].SourceTemplateUid, "秘社来源模板 UID 必须随快照下发");
            Assert.IsTrue(back.Stack[0].SourceIsCouncil, "SourceIsCouncil 必须随快照下发（否则客户端按玩家卡拼路径取不到图）");
        }

        // ============================================================
        // 第二批（2026-10-20）：行动栏红蓝方 / 弹窗居中 / 数值只在场上显示 / 弃牌走新 UI
        // ============================================================

        /// <summary>
        /// 需求 1（行动栏）：回合状态四行里的"先手 / 行动权"必须是**红方 / 蓝方**，不得再出现 P1/P2。
        /// 这是单机与联机共用的唯一出口（`TurnManager.TurnStatusText`），改这里两端口径一起变。
        /// </summary>
        public void TestTurnStatusText_行动栏用红蓝方()
        {
            var tm = new global::TurnManager();
            tm.TurnCount = 3;
            tm.FirstPlayerId = 1;          // 座位 1 = 红方
            tm.PriorityPlayerId = 2;       // 座位 2 = 蓝方
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;

            Assert.AreEqual("第3回合\n先手：红方\n后手行动步骤\n行动权：蓝方", tm.TurnStatusText,
                "行动栏四行文案必须是红方/蓝方，且与座位号一一对应");
            Assert.IsFalse(tm.TurnStatusText.Contains("P1") || tm.TurnStatusText.Contains("P2"),
                "行动栏不得再出现 P1/P2");

            tm.FirstPlayerId = 2;
            Assert.AreEqual("先手：蓝方", tm.FirstPlayerDisplayText, "座位 2 = 蓝方");
        }

        /// <summary>
        /// 需求 3（数值只在场上）：`调/战/势/防` 只在**争夺区**与**附属/封印区**显示；
        /// 手牌/牌库/墓地/资产/分数/秘社一律不显示（费用不受此限，任何区域都显示）。
        /// 判据是卡实例的**逻辑区域**，所以附属卡挂在宿主里/被折叠成角标也照样算"在附属区"。
        /// </summary>
        public void TestCardUI_调战势防只在争夺区与附属封印区显示()
        {
            Assert.IsTrue(CardUI.ZoneShowsStatValues(CardZone.AreaZone), "争夺区角色显示调/战/势/防");
            Assert.IsTrue(CardUI.ZoneShowsStatValues(CardZone.AttachedZone), "附属区显示调/战/势/防");
            Assert.IsTrue(CardUI.ZoneShowsStatValues(CardZone.SealedZone), "封印区显示调/战/势/防");

            foreach (var zone in new[]
                     {
                         CardZone.Hand, CardZone.Deck, CardZone.Graveyard, CardZone.AssetZone,
                         CardZone.ScoreZone, CardZone.CouncilZone,
                     })
                Assert.IsFalse(CardUI.ZoneShowsStatValues(zone), $"{zone} 内不显示调/战/势/防");
        }

        /// <summary>
        /// 需求 2（弹窗居中）+ 需求 4（强制弃牌没有"不丢"按钮）的**结构契约**。
        ///
        /// 居中：过去把算式写在 `Window` 子类内部 → `GetViewport()` 返回窗口自己 ⇒ 居中算成 (0,0)。
        /// 现在唯一实现是 `WindowCenter`（取根视口），本用例锁死"它存在且可调用"，
        /// 并锁死 `OpenMultiPick` 的 `showCancel` 参数存在（强制流程靠它隐藏取消按钮）。
        /// 说明：本类纪律是不加载 `.tscn`、不弹真窗口（headless 下弹窗会抢焦点），故只做反射契约。
        /// </summary>
        public void TestWindowCenter_与强制多选参数契约()
        {
            var center = typeof(WindowCenter).GetMethod("CenterInRoot",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(center, "WindowCenter.CenterInRoot 必须存在（弹窗居中唯一实现）");
            Assert.AreEqual(typeof(Window), center.GetParameters()[0].ParameterType, "入参须是 Window");

            var popup = typeof(WindowCenter).GetMethod("PopupCentered",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(popup, "WindowCenter.PopupCentered 必须存在（先弹后居中）");

            var open = typeof(TargetSelectionWindow).GetMethod("OpenMultiPick");
            Assert.IsNotNull(open, "TargetSelectionWindow.OpenMultiPick 必须存在");
            bool hasShowCancel = false;
            foreach (var p in open.GetParameters())
                if (p.Name == "showCancel" && p.ParameterType == typeof(bool)) hasShowCancel = true;
            Assert.IsTrue(hasShowCancel,
                "OpenMultiPick 必须带 showCancel 参数 —— 手牌上限弃牌靠它隐藏『一张不丢』按钮");
        }

        // ============================================================
        // 【2026-10-20 二批】"不能不丢"＝不能取消丢弃 + 提示窗只能靠丢牌关闭
        // ============================================================

        private static string ReadSource(string resRelative)
        {
            using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                global::Godot.FileAccess.ModeFlags.Read);
            if (f == null) return string.Empty;
            return f.GetAsText();
        }

        /// <summary>
        /// 单机"恢复步骤手牌上限弃牌"必须**一次选够 N 张**（与开局调度同一套交互），
        /// 且**不能取消**：没有取消按钮、标题栏 X 也被忽略。
        ///
        /// 用户口径原文："手牌不能不丢体现在不能取消丢弃，丢弃提示窗不能通过丢牌以外方式关闭"。
        /// 旧实现的两个缺陷（本用例锁死，避免回潮）：
        ///  ① 单机是**逐张**弹一次窗（N 次），不是"和调度类似"的一窗多选；
        ///  ② 窗口的 X 只是 `Hide()`，流程仍在等选择 ⇒ 玩家会以为"已经取消丢弃"。
        /// </summary>
        public void TestDiscardToLimit_一窗选够N张且关不掉()
        {
            string tm = ReadSource("Script/Manager/TurnManager.cs");
            Assert.IsFalse(string.IsNullOrEmpty(tm), "必须能读到 TurnManager.cs");
            Assert.IsTrue(tm.Contains("UIHooks.PromptDiscardToLimit("),
                "单机手牌上限弃牌必须走『一次选够 N 张』的钩子（否则退回逐张弹窗）");
            Assert.IsFalse(tm.Contains("EE.UIHooks.PromptDiscard(discarderId, chooserPlayerId, remaining"),
                "单机不得再逐张调 PromptDiscard 循环（N 次弹窗＝不是调度的交互）");

            string hooks = ReadSource("Script/Interfaces/IGameUIHooks.cs");
            Assert.IsTrue(hooks.Contains("void PromptDiscardToLimit("),
                "IGameUIHooks 必须提供 PromptDiscardToLimit（带默认实现，测试桩无需逐个补）");

            string arena = ReadSource("Script/UI/Arena.cs");
            Assert.IsTrue(arena.Contains("public void PromptDiscardToLimit("),
                "Arena 必须覆写 PromptDiscardToLimit（场上手牌高亮 + 提示窗）");
            Assert.IsTrue(arena.Contains("allowCancel: false"),
                "手牌上限弃牌必须 allowCancel:false ⇒ 没有『取消/一张不丢』出口");

            string win = ReadSource("Script/UI/TargetSelectionWindow.cs");
            Assert.IsTrue(win.Contains("_closable = showCancel;"),
                "多选窗：没有取消出口时窗口必须不可关闭（X 被忽略）");
            Assert.IsTrue(win.Contains("_closable = allowCancel || !string.IsNullOrEmpty(extraLabel);"),
                "单选窗：既没取消出口也没该槽出口按钮时（强制取目标/强制弃牌）必须不可关闭");
            Assert.IsTrue(win.Contains("if (!_closable)"), "关闭请求必须先过 _closable 判定");
        }

        /// <summary>
        /// 首页封面＝`桌面4.jpg`（用户 2026-10-20 指定）。
        ///
        /// 双保险：① 源码锚点（`MainMenu.ApplyTheme` 用的是哪张图）；
        /// ② **真加载**（`ResourceLoader` 能取到纹理）—— 只写对字符串但素材导入坏了时，
        /// 首页会静默退化成纯色底，人眼只会觉得"背景没换"。
        /// 说明：本用例不加载 `.tscn`（纪律同文件头），只验资源本身。
        /// </summary>
        public void TestMainMenu_首页封面为桌面4()
        {
            string menu = ReadSource("Script/UI/MainMenu.cs");
            Assert.IsTrue(menu.Contains("res://Cards/Image/桌面4.jpg"),
                "首页封面必须是 桌面4.jpg（用户指定）");
            Assert.IsFalse(menu.Contains("res://Cards/Image/桌面3.jpg"),
                "首页不得再引用 桌面3.jpg（那是旧封面）");

            var tex = ResourceLoader.Load<Texture2D>("res://Cards/Image/桌面4.jpg");
            Assert.IsNotNull(tex, "桌面4.jpg 必须能被 ResourceLoader 加载（否则首页只剩纯色底）");
            Assert.IsTrue(tex.GetWidth() > 0 && tex.GetHeight() > 0,
                $"桌面4.jpg 尺寸异常：{tex.GetWidth()}×{tex.GetHeight()}");
        }
    }
}
