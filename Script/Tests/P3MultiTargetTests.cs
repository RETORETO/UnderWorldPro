using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【K15·多目标槽位 + 确认面板】按用户 2026-09 口径实现并锁定：
    ///  · 卡文"选择目标本方角色**和/或**目标敌方角色"＝**两个可留空的槽**（本方角色 / 敌方角色），
    ///    但**至少选 1 个**（`MinChosenSlots`＝1，按"卡目标槽"计数，不含地区槽）；
    ///  · **目标地区由玩家选**（地区槽，必选）；FAQ **禁止原地移动** → 已在目标地区内的角色**不出现在候选里**；
    ///  · 逐槽选完后**确认面板**通过才把效果加入堆叠；
    ///  · 结算：`Move{"目标"}` 把每个被选中的角色移动到地区槽指定的地区（一次结算内逐个执行完）；
    ///  · D16：多目标中**任一目标版本号变化 → 整条不结算**。
    /// 覆盖卡：**JC021 狭路相逢**。
    /// </summary>
    public class P3MultiTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(2);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static List<string> Areas(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? new List<string>(uids) : new List<string> { "AreaSlot_1" };
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character,
            CardColor? color = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            if (color.HasValue) tmpl.Colors.Add(color.Value);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>给某玩家放 n 张"颜色3、无色费用"的资产（未横置）→ 满足 JC021 的费用(2)与颜色忠诚(3:1)。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_k15_asset_{playerId}_{i}", CardCategory.Attachment, (CardColor)3);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>【2026-09】给指定颜色的资产（供需要颜色忠诚的卡，如 JC062「颜色忠诚 4:1」）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_k15_asset_c{playerId}_{i}", CardCategory.Attachment, color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData Jc021Effect(PlayerCardData tmpl)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == "JC021_1_MoveTargetsToChosenArea") return e;
            return null;
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① JC021 结构 ====================

        public void TestK15_JC021结构()
        {
            var tmpl = Load("JC021_xialuxiangfeng");
            Assert.IsNotNull(tmpl, "JC021 模板应可加载");
            Assert.IsTrue(tmpl.LimitToActionPhaseOnly, "『只能在行动阶段打出』→ 限行动阶段");

            var e = Jc021Effect(tmpl);
            Assert.IsNotNull(e, "JC021 应有『移动角色』效果");
            Assert.AreEqual(3, e.TargetSlots.Count, "三个槽：目标地区 + 本方角色 + 敌方角色");
            Assert.AreEqual(1, e.MinChosenSlots, "至少选 1 个目标（角色）");

            var areaSlot = e.TargetSlots[0];
            Assert.AreEqual(TargetKind.Area, areaSlot.Spec.Kind, "槽0 = 目标地区");
            Assert.IsFalse(areaSlot.Optional, "目标地区必选");

            var ownSlot = e.TargetSlots[1];
            Assert.AreEqual(TargetKind.Character, ownSlot.Spec.Kind, "槽1 = 角色");
            Assert.IsTrue(ownSlot.Optional, "本方角色槽可留空（卡文『和/或』）");
            Assert.IsTrue(ownSlot.ExcludeInChosenArea, "本方角色槽排除已在目标地区内的角色（FAQ 禁止原地移动）");
            Assert.IsTrue(ownSlot.Spec.Restrictions.Contains(TargetRestriction.Friendly), "本方角色→Friendly");

            var foeSlot = e.TargetSlots[2];
            Assert.IsTrue(foeSlot.Optional, "敌方角色槽可留空");
            Assert.IsTrue(foeSlot.ExcludeInChosenArea, "敌方角色槽排除已在目标地区内的角色");
            Assert.IsTrue(foeSlot.Spec.Restrictions.Contains(TargetRestriction.Enemy), "敌方角色→Enemy");

            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.Move, e.Actions[0].Verb, "动作＝移动");
            Assert.AreEqual("目标", e.Actions[0].Args[0], "移动对象＝目标（不是来源自身）");

            // 打出侧：需要选目标，且允许的大类含 地区 + 角色
            Assert.IsTrue(tmpl.GetRequiresTarget(), "JC021 打出时必须选目标（多目标槽）");
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Area), "打出允许目标含地区");
            Assert.IsTrue(allowed.Contains(TargetKind.Character), "打出允许目标含角色");
        }

        // ==================== ② 候选：分槽 + 目的地排除 ====================

        public void TestK15_候选按槽与目的地排除()
        {
            var game = NewGame();
            var areas = Areas(game);
            Assert.IsTrue(areas.Count >= 2, "前置：至少 2 个地区");
            string a1 = areas[0], a2 = areas[1];

            var mineInA1 = game.CardManager.CreateCardInstance(Probe("TS_k15_mine1"), 1);
            game.ZoneManager.MoveToArea(mineInA1, a1, 1);
            var mineInA2 = game.CardManager.CreateCardInstance(Probe("TS_k15_mine2"), 1);
            game.ZoneManager.MoveToArea(mineInA2, a2, 1);
            var foeInA1 = game.CardManager.CreateCardInstance(Probe("TS_k15_foe1"), 2);
            game.ZoneManager.MoveToArea(foeInA1, a1, 2);
            var foeInA2 = game.CardManager.CreateCardInstance(Probe("TS_k15_foe2"), 2);
            game.ZoneManager.MoveToArea(foeInA2, a2, 2);

            var e = Jc021Effect(Load("JC021_xialuxiangfeng"));
            var choices = new List<TargetChoice> { TargetChoice.Area(a2) };

            // 地区槽候选＝当前所有地区
            var areaCand = MultiTargetSlots.CollectSlot(e, 0, 1, choices, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(areaCand.IsArea, "槽0 候选是地区");
            Assert.IsTrue(areaCand.AreaUids.Contains(a2), "地区候选含目标地区");

            // 本方角色槽：含 a1 的本方角色，**不含**已在 a2 的本方角色（禁止原地移动）
            var ownCand = MultiTargetSlots.CollectSlot(e, 1, 1, choices, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(ownCand.Cards.Exists(c => ReferenceEquals(c, mineInA1)), "本方角色候选含 a1 的本方角色");
            Assert.IsFalse(ownCand.Cards.Exists(c => ReferenceEquals(c, mineInA2)),
                "**已在目标地区(a2)的本方角色不进候选**（FAQ 禁止原地移动）");
            Assert.IsFalse(ownCand.Cards.Exists(c => ReferenceEquals(c, foeInA1)), "不含敌方角色");

            // 敌方角色槽：含 a1 的敌方角色，不含已在 a2 的敌方角色、不含本方角色
            var foeCand = MultiTargetSlots.CollectSlot(e, 2, 1, choices, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(foeCand.Cards.Exists(c => ReferenceEquals(c, foeInA1)), "敌方角色候选含 a1 的敌方角色");
            Assert.IsFalse(foeCand.Cards.Exists(c => ReferenceEquals(c, foeInA2)), "已在 a2 的敌方角色不进候选");
            Assert.IsFalse(foeCand.Cards.Exists(c => ReferenceEquals(c, mineInA1)), "不含本方角色");

            // 换一个目的地（a1）→ 排除关系随之翻转（证明是"按已选目的地"动态过滤）
            var choices2 = new List<TargetChoice> { TargetChoice.Area(a1) };
            var ownCand2 = MultiTargetSlots.CollectSlot(e, 1, 1, choices2, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.IsFalse(ownCand2.Cards.Exists(c => ReferenceEquals(c, mineInA1)), "目的地换成 a1 后 a1 的本方角色被排除");
            Assert.IsTrue(ownCand2.Cards.Exists(c => ReferenceEquals(c, mineInA2)), "a2 的本方角色成为候选");
        }

        // ==================== ③ 组合校验：必选地区 / 至少 1 个目标 / 禁止原地 ====================

        public void TestK15_组合校验()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_v_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var mineInA2 = game.CardManager.CreateCardInstance(Probe("TS_k15_v_mine2"), 1);
            game.ZoneManager.MoveToArea(mineInA2, a2, 1);

            var e = Jc021Effect(Load("JC021_xialuxiangfeng"));

            string r1 = MultiTargetSlots.Validate(e, new List<TargetChoice> { TargetChoice.Empty, TargetChoice.Empty, TargetChoice.Empty },
                1, game.ZoneManager, game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsNotNull(r1, "什么都不选 → 不合法");
            Assert.IsTrue(r1.Contains("必选"), $"未选必选地区槽应被拦下（原因：{r1}）");

            string r2 = MultiTargetSlots.Validate(e, new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Empty, TargetChoice.Empty },
                1, game.ZoneManager, game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsNotNull(r2, "只选地区、不选角色 → 不合法（至少 1 个目标）");
            Assert.IsTrue(r2.Contains("至少"), $"应提示至少选 1 个目标（原因：{r2}）");

            string r3 = MultiTargetSlots.Validate(e,
                new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(mineInA2.UID), TargetChoice.Empty },
                1, game.ZoneManager, game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsNotNull(r3, "选了一个已经在目的地的角色 → 不合法（禁止原地移动）");
            Assert.IsTrue(r3.Contains("原地"), $"应提示不能原地移动（原因：{r3}）");

            string r4 = MultiTargetSlots.Validate(e,
                new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Empty },
                1, game.ZoneManager, game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsNull(r4, "选地区 + 1 个合法角色 → 合法");

            string r5 = MultiTargetSlots.Validate(e,
                new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Card(mine.UID) },
                1, game.ZoneManager, game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsNotNull(r5, "同一个角色占两个槽 → 不合法");
        }

        // ==================== ③b 交互形态：场上看得见的槽走高亮点选（2026-09 用户口径 A86） ====================

        /// <summary>
        /// 【2026-09 用户口径·修复】"选择查看的暗藏者应该选择**场上的高亮暗藏者**，而不是弹出选择框"
        /// （JC062），并裁定**通用**应用于所有"多目标卡的场上卡槽"（既有 A86 口径：
        /// "场上能看到的卡一律用场上高亮选，不用列表窗"）。
        /// 本用例锁定**判定规则**（`MultiTargetSlots.ShouldPickOnField`）与随请求下发的同一判定
        /// （`ChoiceRequest.SlotFieldVisible`）；实际高亮/点击的 UI 属 View，headless 无法覆盖。
        /// </summary>
        public void TestK15_场上可见槽走高亮点选()
        {
            var game = NewGame();
            var areas = Areas(game);

            // JC021 三个槽：地区槽（高亮地区槽位）+ 两个"本方角色"槽（场上卡高亮）
            var e = Jc021Effect(Load("JC021_xialuxiangfeng"));
            var chosen = new List<TargetChoice> { TargetChoice.Empty, TargetChoice.Empty, TargetChoice.Empty };

            var areaCand = MultiTargetSlots.CollectSlot(e, 0, 1, chosen, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(areaCand.IsArea, "槽 1＝目标地区（地区槽）");
            Assert.IsTrue(MultiTargetSlots.ShouldPickOnField(areaCand), "地区槽 → 走**地区槽高亮 + 点击**");

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_field_mine"), 1);
            game.ZoneManager.MoveToArea(mine, areas[0], 1);
            var cardCand = MultiTargetSlots.CollectSlot(e, 1, 1, chosen, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);
            Assert.AreEqual(1, cardCand.Cards.Count, "前置：本方角色槽有 1 个候选");
            Assert.IsTrue(cardCand.FieldVisible, "争夺区角色＝场上看得见");
            Assert.IsTrue(MultiTargetSlots.ShouldPickOnField(cardCand),
                "场上看得见的卡槽 → 走**场上高亮 + 点击**（不再弹候选列表窗）");

            // 候选为空（如对手没有任何暗藏者）→ 必须回退按钮列表窗：那里才有"留空 / 取消（不打出）"出口，
            // 否则玩家会卡在一个"点不动的提示窗"上（不可接受）。
            var emptyFieldSlot = new SlotCandidates { FieldVisible = true };
            Assert.IsFalse(MultiTargetSlots.ShouldPickOnField(emptyFieldSlot), "无候选 → 保留按钮窗出口");

            // 区域牌堆槽（墓地/手牌/牌库…）没有场上卡 UI → 按钮列表窗
            var pileSlot = new SlotCandidates { Cards = { mine }, FieldVisible = false };
            Assert.IsFalse(MultiTargetSlots.ShouldPickOnField(pileSlot), "区域牌堆槽 → 按钮列表窗");

            // 玩家槽：玩家不是场上卡节点（无 CardUI）→ 按钮列表窗
            var playerSlot = new SlotCandidates { IsPlayer = true };
            playerSlot.PlayerIds.Add(2);
            Assert.IsFalse(MultiTargetSlots.ShouldPickOnField(playerSlot), "玩家槽 → 按钮列表窗");
        }

        /// <summary>
        /// 【2026-09 用户口径】多目标槽位的**取目标窗形状**（决定"一个窗多选"还是"逐槽流程"）：
        ///  · JC062（两个"目标暗藏者"槽）⇒ **一个窗多选**（左键选择/右键取消，至少 0 个）；
        ///  · JC021（"目标地区"必选 ＋"本方/敌方角色"槽）⇒ **一个窗多选卡＋地区**
        ///    （每方至多 1 个角色、地区必选、无地区或无角色不能确认）；
        ///  · JC092/JC133（地区 ＋ **我方墓地角色牌**＝区域牌堆、没有场上 UI）⇒ 仍走逐槽流程。
        /// </summary>
        public void TestK15_取目标窗形状判定()
        {
            var jc062 = MultiTargetSlots.FindMultiTargetEffect(Load("JC062_diaochadangan"), EffectTiming.OnSorceryCast);
            Assert.IsNotNull(jc062, "JC062 应有 TargetSlots 效果");
            Assert.AreEqual(MultiTargetSlots.MultiSlotShape.FieldMultiPick, MultiTargetSlots.ClassifyShape(jc062),
                "JC062（两个『目标暗藏者』场上卡槽）⇒ 一个窗多选");

            var jc021 = Jc021Effect(Load("JC021_xialuxiangfeng"));
            Assert.IsNotNull(jc021, "JC021 应有 TargetSlots 效果");
            Assert.AreEqual(MultiTargetSlots.MultiSlotShape.FieldMultiPick, MultiTargetSlots.ClassifyShape(jc021),
                "JC021（目标地区＋本方/敌方角色）⇒ 一个窗多选（卡＋地区）");

            var jc092 = MultiTargetSlots.FindMultiTargetEffect(Load("JC092_silingzhaohuan"), EffectTiming.OnSorceryCast);
            Assert.IsNotNull(jc092, "JC092 应有 TargetSlots 效果");
            Assert.AreEqual(MultiTargetSlots.MultiSlotShape.PerSlot, MultiTargetSlots.ClassifyShape(jc092),
                "JC092（地区＋**墓地**角色牌＝区域牌堆）⇒ 逐槽流程（墓地没有场上 UI）");

            var jc133 = MultiTargetSlots.FindMultiTargetEffect(Load("JC133_caokongsishi"), EffectTiming.OnSorceryCast);
            Assert.IsNotNull(jc133, "JC133 应有 TargetSlots 效果");
            Assert.AreEqual(MultiTargetSlots.MultiSlotShape.PerSlot, MultiTargetSlots.ClassifyShape(jc133),
                "JC133 同 JC092 ⇒ 逐槽流程");
        }

        // ==================== ④ 端到端：打出并移动 ====================

        /// <summary>选地区 + 本方与敌方各 1 个角色 → 两个角色都移动到目标地区。</summary>
        public void TestK15_打出_两个角色都移动()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_e_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_e_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice>
            {
                TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Card(foe.UID),
            };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "多目标合法 → 应能打出");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "事务卡进入堆叠等待结算（单机下卡面仍在手牌，由 View 呈现在堆叠区）");

            var entry = game.StackManager.Entries[0];
            Assert.AreEqual(a2, entry.TargetAreaId, "条目记录了地区槽（目标地区）");
            Assert.AreEqual(2, entry.TargetCardUids.Count, "条目按卡槽对齐记录了 2 个卡目标");
            Assert.AreEqual(mine.UID, entry.TargetCardUids[0], "槽1（本方角色）＝所选本方角色");
            Assert.AreEqual(foe.UID, entry.TargetCardUids[1], "槽2（敌方角色）＝所选敌方角色");
            Assert.AreEqual(2, entry.TargetVersionIds.Count, "逐目标登记了入栈版本号");

            ResolveStackOnce(game);

            Assert.AreEqual(a2, mine.ZoneDetailId, "本方角色移动到目标地区");
            Assert.AreEqual(a2, foe.ZoneDetailId, "敌方角色移动到目标地区");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡结算后进墓地");
        }

        /// <summary>只选 1 个（敌方）角色也成立——卡文"和/或"。</summary>
        public void TestK15_打出_只选一个目标()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_o_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_o_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Empty, TargetChoice.Card(foe.UID) };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "只选敌方角色也应能打出");
            ResolveStackOnce(game);

            Assert.AreEqual(a2, foe.ZoneDetailId, "被选中的敌方角色移动了");
            Assert.AreEqual(a1, mine.ZoneDetailId, "未选中的本方角色原地不动");
        }

        /// <summary>非法组合（选了已在目的地的角色）→ 整次打出被拒，且**不扣费**。</summary>
        public void TestK15_打出_非法组合被拒且不扣费()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var inA1 = game.CardManager.CreateCardInstance(Probe("TS_k15_b_mine1"), 1);
            game.ZoneManager.MoveToArea(inA1, a1, 1);
            var inA2 = game.CardManager.CreateCardInstance(Probe("TS_k15_b_mine2"), 1);
            game.ZoneManager.MoveToArea(inA2, a2, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var bad = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(inA2.UID), TargetChoice.Empty };
            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, bad), "非法组合（原地移动）应被拒绝");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "被拒绝后卡仍在手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒绝时**不扣费**（校验在付费之前）");
            Assert.IsTrue(game.StackManager.IsEmpty, "被拒绝时未入堆叠");

            // 什么都不选（只给必选地区槽留空）同样被拒
            var none = new List<TargetChoice> { TargetChoice.Empty, TargetChoice.Empty, TargetChoice.Empty };
            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, none), "全空选择应被拒绝");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "仍不扣费");
        }

        /// <summary>
        /// D16（用户 2026-09 口径）：多目标中**只跳过"丢失的那一个"**，其余目标照常结算（卡仍进墓地）。
        /// </summary>
        public void TestK15_多目标部分丢失只跳过丢失的那个()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_d_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_d_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice>
            {
                TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Card(foe.UID),
            };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "前置：成功打出");

            // 堆叠期间**敌方角色**被移回手牌（版本号变化 → 该目标丢失）
            game.ZoneManager.MoveToHand(foe, 2);

            ResolveStackOnce(game);

            Assert.AreEqual(a2, mine.ZoneDetailId, "**未丢失的目标照常移动**（本方角色 → 目标地区）");
            Assert.AreEqual(CardZone.Hand, foe.CurrentZone, "丢失的目标保持原样（未被移动）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡仍进墓地");
        }

        /// <summary>若**全部**目标都丢失：效果仍结算但没有任何目标被移动（卡进墓地）。</summary>
        public void TestK15_多目标全部丢失则无人移动()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_d2_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_d2_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice>
            {
                TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Card(foe.UID),
            };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "前置：成功打出");

            game.ZoneManager.MoveToHand(mine, 1);
            game.ZoneManager.MoveToHand(foe, 2);

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Hand, mine.CurrentZone, "全部目标丢失 → 无人被移动");
            Assert.AreEqual(CardZone.Hand, foe.CurrentZone, "全部目标丢失 → 无人被移动");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡仍进墓地");
        }

        /// <summary>打出侧门禁：没有逐槽选择就打出（旧调用方式）会被拒绝，避免"跳过取目标"。</summary>
        public void TestK15_未提供逐槽选择则拒绝打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_n_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, null),
                "多目标卡未提供逐槽选择 → 拒绝打出（不允许绕过取目标）");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
        }

        // ==================== ⑤ 确认面板流程（单机：逐槽取目标 → 确认 → 才入堆叠） ====================

        /// <summary>确认面板桩：按槽脚本回包，记录"逐槽询问"与"确认面板"两阶段。</summary>
        private sealed class MultiTargetStub : Script.Interfaces.IGameUIHooks
        {
            /// <summary>逐槽选择脚本：返回 `"AREA:&lt;地区UID&gt;"` / `"CARD:&lt;卡UID&gt;"` / `"SKIP"` / null（不选）。</summary>
            public System.Func<int, List<TargetChoice>, SlotCandidates, string> Script;
            public bool Confirm = true;
            public int SlotCalls;
            public int ConfirmCalls;
            public readonly List<string> SlotLabels = new List<string>();
            public readonly List<List<string>> SeenCandidateNames = new List<List<string>>();

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid)
                => onPickedAreaUid?.Invoke(options != null && options.Count > 0 ? options[0].AreaUid : null);
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed)
                => onConfirmed?.Invoke(cards, new List<CardInstance>());

            /// <summary>【K16】本套件不涉及模式分支：视为取消。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed)
            {
                var choices = new List<TargetChoice>();
                for (int i = 0; i < data.TargetSlots.Count; i++) choices.Add(TargetChoice.Empty);
                for (int i = 0; i < data.TargetSlots.Count; i++)
                {
                    var cand = candidatesForSlot?.Invoke(i, choices);   // 每次都基于"当前已选"重取
                    SlotCalls++;
                    SlotLabels.Add(cand?.Label ?? "");
                    var names = new List<string>();
                    if (cand != null)
                    {
                        if (cand.IsArea) names.AddRange(cand.AreaUids);
                        else foreach (var c in cand.Cards) names.Add(c?.UID ?? "");
                    }
                    SeenCandidateNames.Add(names);

                    string key = Script?.Invoke(i, choices, cand);
                    if (string.IsNullOrEmpty(key) || key == "SKIP") continue;
                    int sep = key.IndexOf(':');
                    string kind = sep > 0 ? key.Substring(0, sep) : key;
                    string uid = sep > 0 ? key.Substring(sep + 1) : "";
                    if (kind == "AREA") choices[i] = TargetChoice.Area(uid);
                    else choices[i] = TargetChoice.Card(uid);
                }
                ConfirmCalls++;
                onConfirmed?.Invoke(Confirm ? choices : null);
            }
        }

        /// <summary>
        /// 【2026-09 用户口径】JC021 卡文"**和/或**" ⇒ 两个角色目标**互相独立**：
        /// 结算时其中一个目标已**不合法**（本例：结算前被移入目标地区，违反"不能原地移动"；
        /// 用户举的例子是"获得屏障"）→ **只跳过它自己**，另一个目标照常执行；
        /// 事务卡照常结算进墓地（不得整条丢弃）。
        /// </summary>
        public void TestK15_JC021部分目标不合法时其余照常结算()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_p_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_p_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var stub = new MultiTargetStub
            {
                Script = (slot, _, __) => slot == 0 ? $"AREA:{a2}"
                    : (slot == 1 ? $"CARD:{mine.UID}" : (slot == 2 ? $"CARD:{foe.UID}" : null)),
            };
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.BeginMultiTargetPlay(1, card.UID);

            Assert.AreEqual(1, game.StackManager.Entries.Count, "前置：确认后效果入堆叠");
            var entry = game.StackManager.Entries[0];
            Assert.AreEqual(mine.UID, entry.TargetCardUids[0], "前置：本方角色目标已记录");
            Assert.AreEqual(foe.UID, entry.TargetCardUids[1], "前置：敌方角色目标已记录");

            // 结算前让"敌方角色"这个目标变得**不合法**：在同一个争夺区内移动不刷新版本号（D37）
            // ⇒ 它不算"丢失"，只能靠结算时的**逐目标合法性复检**（ExcludeInChosenArea：不能原地移动）剔除。
            game.ZoneManager.MoveToArea(foe, a2, 2);
            Assert.AreEqual(a2, foe.ZoneDetailId, "前置：敌方目标已进入目标地区（原地移动 ⇒ 该目标不再合法）");

            ResolveStackOnce(game);

            Assert.AreEqual(a2, mine.ZoneDetailId, "**合法目标照常执行**（移动到目标地区）");
            Assert.AreEqual(a2, foe.ZoneDetailId, "不合法目标被跳过（它本来就在 a2，不再被移动）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡照常结算进墓地（不得整条丢弃）");

            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>
        /// 同上但用**屏障**（用户举的例子）：敌方角色在结算前获得屏障 ⇒ 该目标不合法被跳过，
        /// 本方角色照常移动到目标地区。
        /// </summary>
        public void TestK15_JC021目标获得屏障时其余照常结算()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_g_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_g_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var stub = new MultiTargetStub
            {
                Script = (slot, _, __) => slot == 0 ? $"AREA:{a2}"
                    : (slot == 1 ? $"CARD:{mine.UID}" : (slot == 2 ? $"CARD:{foe.UID}" : null)),
            };
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.BeginMultiTargetPlay(1, card.UID);
            Assert.AreEqual(1, game.StackManager.Entries.Count, "前置：效果入堆叠");

            foe.SetGuard(true);   // 结算前获得屏障 ⇒ "不能被敌方卡牌或能力指定为目标"

            ResolveStackOnce(game);

            Assert.AreEqual(a2, mine.ZoneDetailId, "另一个目标照常移动到目标地区");
            Assert.AreEqual(a1, foe.ZoneDetailId, "获得屏障的目标被跳过（未被移动）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡照常结算进墓地");

            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>确认面板通过 → 效果入堆叠并结算（且"逐槽候选"按已选目的地动态刷新）。</summary>
        public void TestK15_确认面板_确认后打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_c_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var mineInA2 = game.CardManager.CreateCardInstance(Probe("TS_k15_c_mine2"), 1);
            game.ZoneManager.MoveToArea(mineInA2, a2, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var stub = new MultiTargetStub
            {
                Script = (slot, _, __) => slot == 0 ? $"AREA:{a2}" : (slot == 1 ? $"CARD:{mine.UID}" : null),
            };
            EffectEngine.Instance.UIHooks = stub;

            game.TurnManager.BeginMultiTargetPlay(1, card.UID);

            Assert.AreEqual(3, stub.SlotCalls, "应逐槽询问 3 次（地区 + 本方角色 + 敌方角色）");
            Assert.AreEqual(1, stub.ConfirmCalls, "全部槽选完后应有 1 次确认面板");
            Assert.AreEqual("目标地区", stub.SlotLabels[0], "第 1 个槽＝目标地区");
            Assert.AreEqual("本方角色", stub.SlotLabels[1], "第 2 个槽＝本方角色");
            Assert.AreEqual("敌方角色", stub.SlotLabels[2], "第 3 个槽＝敌方角色");
            // 第 2 个槽的候选已按"刚选定的目标地区(a2)"过滤 → 已在 a2 的角色不出现在候选里
            Assert.IsFalse(stub.SeenCandidateNames[1].Contains(mineInA2.UID),
                "逐槽候选按已选目的地动态刷新：已在目标地区内的角色不进候选（FAQ 禁止原地移动）");
            Assert.IsTrue(stub.SeenCandidateNames[1].Contains(mine.UID), "a1 的本方角色是候选");

            Assert.AreEqual(1, game.StackManager.Entries.Count, "确认后效果入堆叠");
            var entry = game.StackManager.Entries[0];
            Assert.AreEqual(a2, entry.TargetAreaId, "入堆叠的地区＝确认的目标地区");
            Assert.AreEqual(2, entry.TargetCardUids.Count, "按卡槽对齐（本方角色 + 敌方角色，后者留空）");
            Assert.AreEqual(mine.UID, entry.TargetCardUids[0], "槽1＝所选本方角色");
            Assert.AreEqual("", entry.TargetCardUids[1], "槽2 留空（卡文『和/或』）");

            ResolveStackOnce(game);

            Assert.AreEqual(a2, mine.ZoneDetailId, "被选中的本方角色移动到目标地区");
            Assert.AreEqual(a2, mineInA2.ZoneDetailId, "未被选中的角色原地不动（本来就在 a2）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡结算后进墓地");

            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>确认面板取消（或面板返回 null）→ 不打出、不扣费、卡留手牌。</summary>
        public void TestK15_确认面板_取消则不打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_x_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var stub = new MultiTargetStub
            {
                Confirm = false,
                Script = (slot, _, __) => slot == 0 ? $"AREA:{Areas(game)[1]}" : $"CARD:{mine.UID}",
            };
            EffectEngine.Instance.UIHooks = stub;

            game.TurnManager.BeginMultiTargetPlay(1, card.UID);

            Assert.AreEqual(1, stub.ConfirmCalls, "应走到确认面板");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "取消后卡留在手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "取消后不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "取消后不入堆叠");
            Assert.AreEqual(a1, mine.ZoneDetailId, "取消后角色未被移动");

            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>无 UI 时（测试/headless）确定性兜底：不会静默跳过目标，而是尝试打出并被校验拒绝。</summary>
        public void TestK15_无UI兜底不静默跳目标()
        {
            var game = NewGame();
            var areas = Areas(game);
            GiveAssets(game, 1, 3);
            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_u_mine"), 1);
            game.ZoneManager.MoveToArea(mine, areas[0], 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            EffectEngine.Instance.UIHooks = null;

            game.TurnManager.BeginMultiTargetPlay(1, card.UID);

            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "无 UI 兜底不会「跳过取目标」打出");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "兜底路径不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "兜底路径不入堆叠");
        }

        // ==================== ⑥ 联机：服务端逐槽请求 + 确认面板 ====================

        /// <summary>联机桩网关：按 Kind 记录请求并脚本化回包。</summary>
        private sealed class SlotGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            /// <summary>逐槽回包脚本：返回选中 UID（""＝留空；null＝取消整次）。</summary>
            public System.Func<int, Script.Net.ChoiceRequest, string> SlotAnswer;
            public bool Confirm = true;
            /// <summary>【2026-09】整窗多选（`FieldMultiPick`）回包脚本：返回 (已选卡, 已选地区)；null＝取消整次。</summary>
            public System.Func<Script.Net.ChoiceRequest, (System.Collections.Generic.List<string> Cards, System.Collections.Generic.List<string> Areas)> FieldAnswer;
            /// <summary>是否让 `FieldMultiPick` 回 Cancelled（取消整次打出）。</summary>
            public bool FieldCancel;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.ConfirmTargets)
                    return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult
                    {
                        RequestId = req.RequestId, Confirmed = Confirm, Cancelled = !Confirm,
                    });
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.FieldMultiPick)
                {
                    if (FieldCancel)
                        return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult
                        {
                            RequestId = req.RequestId, Cancelled = true,
                        });
                    var res = new Script.Net.ChoiceResult { RequestId = req.RequestId, Confirmed = true };
                    if (FieldAnswer != null)
                    {
                        var picked = FieldAnswer(req);
                        if (picked.Cards != null) res.SelectedUids.AddRange(picked.Cards);
                        if (picked.Areas != null) res.SelectedAreaUids.AddRange(picked.Areas);
                    }
                    return System.Threading.Tasks.Task.FromResult(res);
                }
                var ans = SlotAnswer?.Invoke(req.SlotIndex, req);
                return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    SelectedUid = ans,
                    Cancelled = ans == null,
                });
            }
        }

        /// <summary>
        /// 【2026-09 用户口径 JC021·联机】"一个窗多选"形状（`FieldMultiPick`）**只发一个聚合请求**
        /// （不再逐槽、也不再另发确认面板）——用户口径："JC062 只需要一个提示框""JC021 一起改掉"。
        /// 回包＝已选卡 + 已选地区；服务端用**单机同一份** `FieldPickPlan` 折算逐槽后 `PlayCard`
        /// （该请求的"确认"就是最终确认）。
        /// </summary>
        public void TestK15_联机_JC021一个窗多选()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_mp_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_k15_mp_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new SlotGateway
            {
                FieldAnswer = _ => (new System.Collections.Generic.List<string> { mine.UID },
                                    new System.Collections.Generic.List<string> { a2 }),
            };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayMultiTargetCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "整窗多选流程应成功");
            Assert.AreEqual(1, gw.Requests.Count, "**只发 1 个请求**（不再逐槽、不再另发确认面板）");
            var req = gw.Requests[0];
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.FieldMultiPick, req.RequestKind, "Kind＝FieldMultiPick");
            Assert.AreEqual("请选择目标角色（0/2）和目标地区（0/1）", req.Prompt, "窗面标题与用户样例逐字一致");
            Assert.AreEqual("目标角色", req.MultiCardLabel, "卡组标题");
            Assert.AreEqual("目标地区", req.MultiAreaLabel, "地区组标题");
            Assert.AreEqual(2, req.MultiMaxCards, "至多 2 张卡（本方/敌方各 1）");
            Assert.AreEqual(1, req.MultiMaxAreas, "至多 1 个地区");
            Assert.AreEqual(1, req.MultiMinCards, "至少选 1 个角色");
            Assert.IsTrue(req.MultiAreaRequired, "**地区必选**（没选地区不能确认）");
            Assert.IsTrue(req.MultiSameControllerOnce, "**每个玩家场上的角色只能选一个**");
            Assert.IsTrue(req.OptionUids.Contains(mine.UID), "本方角色在卡候选里");
            Assert.IsTrue(req.OptionUids.Contains(foe.UID), "敌方角色在卡候选里");
            Assert.IsTrue(req.OptionAreaUids.Contains(a2), "地区候选随请求下发");

            Assert.AreEqual(1, game.StackManager.Entries.Count, "确认后入堆叠");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "正常付费（2 费）");
            ResolveStackOnce(game);
            Assert.AreEqual(a2, mine.ZoneDetailId, "被选中的本方角色移动到目标地区");
            Assert.AreEqual(a1, foe.ZoneDetailId, "未选的敌方角色原地不动");
        }

        /// <summary>【2026-09·联机 JC062】两个"目标暗藏者"槽 ⇒ 一个聚合请求；标题与"选 0 个也能确认"同单机。</summary>
        public void TestK15_联机_JC062一个窗多选()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 1, (CardColor)4);    // JC062 费用 1、颜色忠诚 4:1

            var c1 = game.CardManager.CreateCardInstance(Probe("TS_k15_mp_c1"), 2);
            game.ZoneManager.MoveToArea(c1, a1, 2);
            c1.SetCovert(true);
            var c2 = game.CardManager.CreateCardInstance(Probe("TS_k15_mp_c2"), 2);
            game.ZoneManager.MoveToArea(c2, a2, 2);
            c2.SetCovert(true);
            var card = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var gw = new SlotGateway
            {
                FieldAnswer = _ => (new System.Collections.Generic.List<string> { c1.UID, c2.UID },
                                    new System.Collections.Generic.List<string>()),
            };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayMultiTargetCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "JC062 整窗多选应成功");
            Assert.AreEqual(1, gw.Requests.Count, "只发 1 个请求");
            var req = gw.Requests[0];
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.FieldMultiPick, req.RequestKind, "Kind＝FieldMultiPick");
            Assert.AreEqual("请选择目标暗藏者：已选择（0/2）", req.Prompt, "窗面标题与用户样例逐字一致");
            Assert.AreEqual("目标暗藏者", req.MultiCardLabel, "卡组标题（去掉了『（第 1 个，可留空）』括注）");
            Assert.AreEqual(2, req.MultiMaxCards, "至多 2 张");
            Assert.AreEqual(0, req.MultiMaxAreas, "无地区槽");
            Assert.AreEqual(0, req.MultiMinCards, "**选 0 个也能确认**（FAQ：只为抓一张牌）");
            Assert.IsFalse(req.MultiAreaRequired, "无地区槽 ⇒ 无『必选地区』要求");
            Assert.IsFalse(req.MultiSameControllerOnce, "两个同大类卡槽 ⇒ 不按控制者限 1");
            Assert.IsTrue(req.OptionUids.Contains(c1.UID) && req.OptionUids.Contains(c2.UID), "两个暗藏者都在候选里");
            Assert.AreEqual(0, req.OptionAreaUids.Count, "无地区候选");
        }

        /// <summary>【2026-09·联机】整窗多选取消/超时 → 不打出、不扣费、卡留手牌（与单机"取消则不发动"同口径）。</summary>
        public void TestK15_联机_整窗多选取消则不打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_mc_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new SlotGateway { FieldCancel = true };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayMultiTargetCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消 → 整次打出失败");
            Assert.AreEqual(1, gw.Requests.Count, "只发了那一个聚合请求");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡留在手牌");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        /// <summary>
        /// 【2026-09】**非** `FieldMultiPick` 形状必须保持既有"逐槽请求 + 确认面板"流程
        /// （用户裁定：JC092/JC133 这类"地区 ＋ 我方**墓地**角色牌＝区域牌堆、没有场上 UI"暂不改）。
        /// </summary>
        public void TestK15_联机_区域牌堆槽仍走逐槽加确认()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a2 = areas[1];
            GiveAssets(game, 1, 2, (CardColor)7);

            var graveCard = game.CardManager.CreateCardInstance(Probe("TS_k15_ps_grave"), 1);
            game.ZoneManager.MoveToGraveyard(graveCard, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC092_silingzhaohuan"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var gw = new SlotGateway
            {
                // 槽0＝目标地区；槽1＝你墓地中的角色牌
                SlotAnswer = (slot, _) => slot == 0 ? a2 : graveCard.UID,
            };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayMultiTargetCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "逐槽 + 确认流程应成功");
            Assert.AreEqual(3, gw.Requests.Count, "请求序列＝2 个槽 + 1 次确认（**不是**聚合请求）");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.MultiTargetSlot, gw.Requests[0].RequestKind, "第 1 个＝逐槽");
            Assert.AreEqual("目标地区", gw.Requests[0].SlotLabel, "槽提示＝目标地区");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.MultiTargetSlot, gw.Requests[1].RequestKind, "第 2 个＝逐槽");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.ConfirmTargets, gw.Requests[2].RequestKind, "最后＝确认面板");
        }

        /// <summary>阻塞式网关：请求发出后**不回答**，用于观察"流程进行中"的状态。</summary>
        private sealed class BlockingGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            private readonly List<System.Threading.Tasks.TaskCompletionSource<Script.Net.ChoiceResult>> _waiting
                = new List<System.Threading.Tasks.TaskCompletionSource<Script.Net.ChoiceResult>>();

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var tcs = new System.Threading.Tasks.TaskCompletionSource<Script.Net.ChoiceResult>();
                _waiting.Add(tcs);
                return tcs.Task;
            }

            public void Answer(int index, Script.Net.ChoiceResult res)
            {
                if (index < 0 || index >= _waiting.Count) return;
                _waiting[index].TrySetResult(res);
            }
        }

        /// <summary>
        /// 【2026-09 用户实测·联机】"打出后…还在手牌，**可以再次打出**"：
        /// 服务端在一次"打出前选择流程"（模式/多目标/额外费用）进行中，必须**忽略该卡的重复打出意图**，
        /// 且流程结束后自动解除登记；流程结束时该事务卡应已在**弃牌堆**（不再留手牌）。
        /// </summary>
        public void TestK15_联机_流程进行中忽略重复打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_dup_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var gw = new BlockingGateway();
            game.TurnManager.InputGateway = gw;

            Assert.IsTrue(game.PlayerActionService.TryBeginMultiTargetPlayOnline(1, card.UID), "第一次应被服务端接管");
            Assert.AreEqual(1, gw.Requests.Count, "已发出 1 个聚合请求（正等玩家）");
            Assert.IsTrue(game.PlayerActionService.IsPlayFlowPending(card.UID), "该卡登记为『打出前选择流程进行中』");

            // 玩家又拖了一次同一张卡 → 必须被吞掉，且**不再开第二个流程**
            Assert.IsTrue(game.PlayerActionService.TryBeginMultiTargetPlayOnline(1, card.UID), "重复意图被吞掉（已接管）");
            Assert.AreEqual(1, gw.Requests.Count, "**仍只有 1 个请求**（没有开第二个流程）");

            // 回答 → 流程收尾（入堆叠 + 事务卡入弃牌堆 + 解除登记）
            var res = new Script.Net.ChoiceResult { RequestId = gw.Requests[0].RequestId, Confirmed = true };
            res.SelectedUids.Add(mine.UID);
            res.SelectedAreaUids.Add(a2);
            gw.Answer(0, res);

            for (int i = 0; i < 200 && game.PlayerActionService.IsPlayFlowPending(card.UID); i++)
                System.Threading.Thread.Sleep(10);
            Assert.IsFalse(game.PlayerActionService.IsPlayFlowPending(card.UID), "流程结束后解除登记");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "**事务卡入堆叠时即进弃牌堆**（不再留手牌）");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "已入堆叠");
            Assert.AreEqual(a2, game.StackManager.Entries[0].TargetAreaId, "地区槽＝所选目标地区");
        }

        /// <summary>
        /// 【2026-09 用户口径】事务卡**进入堆叠时就置入弃牌堆**（不再等结算）：打出后立刻检查
        /// ——卡已在墓地、手牌里没有它、**且不能再次打出**（用户实测："打出后结算前还在手牌，可以再次打出"）。
        /// </summary>
        public void TestK15_事务卡入堆叠即进弃牌堆且不可重复打出()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            var mine = game.CardManager.CreateCardInstance(Probe("TS_k15_gy_mine"), 1);
            game.ZoneManager.MoveToArea(mine, a1, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(mine.UID), TargetChoice.Empty };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "前置：成功打出");

            // 尚未结算（堆叠仍在）
            Assert.AreEqual(1, game.StackManager.Entries.Count, "前置：已入堆叠、尚未结算");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "**入堆叠即进弃牌堆**（不等结算）");
            Assert.IsFalse(game.TurnManager.GetHandOf(1).Contains(card), "手牌里已没有这张卡");
            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "**不能再次打出同一张卡**");

            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "结算后仍在弃牌堆（结算分支幂等，不重复移动）");
            Assert.AreEqual(a2, mine.ZoneDetailId, "结算正常：角色被移动到目标地区");
        }

        /// <summary>【2026-09 用户口径 JC062/JC021】`FieldPickPlan`：分组文案/上限下限/映射/每方至多 1 个/禁止原地移动。</summary>
        public void TestK15_FieldPickPlan_JC062与JC021规则()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 3);

            // ---- JC062：两个"目标暗藏者"槽（可留空、至少 0）----
            var jc062 = MultiTargetSlots.FindMultiTargetEffect(Load("JC062_diaochadangan"), EffectTiming.OnSorceryCast);
            var src062 = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            var coverts = new List<CardInstance>();
            for (int i = 0; i < 3; i++)
            {
                var c = game.CardManager.CreateCardInstance(Probe($"TS_k15_plan_c{i}"), 2);
                game.ZoneManager.MoveToArea(c, i == 2 ? a2 : a1, 2);
                c.SetCovert(true);
                coverts.Add(c);
            }
            var plan062 = MultiTargetSlots.FieldPickPlan.Build(jc062, src062, 1, game.ZoneManager,
                game.CardManager, game.PlayerManager, game.AreaManager);
            Assert.IsNotNull(plan062, "JC062 应判定为 FieldMultiPick 形状");
            Assert.AreEqual(2, plan062.MaxCards, "至多 2 张（两个槽）");
            Assert.AreEqual(0, plan062.MaxAreas, "无地区槽");
            Assert.AreEqual(0, plan062.MinCards, "至少 0 张（可以一张都不选）");
            Assert.AreEqual("目标暗藏者", plan062.CardLabel, "卡组标题（去掉括注）");
            Assert.IsNull(plan062.AreaLabel, "无地区槽 ⇒ 无地区组标题");
            Assert.IsTrue(plan062.CanConfirm(new List<string>(), new List<string>()), "**选 0 个也能确认**");
            var open062 = plan062.OpenCardCandidates(new List<string>(), new List<string>());
            Assert.AreEqual(3, open062.Count, "三个暗藏者都是候选");
            var choices062 = plan062.MapPicks(new List<string> { coverts[0].UID, coverts[1].UID }, new List<string>());
            int filled062 = 0;
            foreach (var ch in choices062) if (!ch.IsEmpty) filled062++;
            Assert.AreEqual(2, filled062, "两张卡各占一个槽");
            // 【2026-09 修复】候选＝"已选中且仍然合法"的卡 ∪ "未填满槽还能接受的卡" ⇒ 两个槽都满时：
            //  · 已选中的两张**仍在候选里**（保持"已选中"高亮；否则协调器的"失效剔除"会把它们删掉
            //    ⇒ 用户实测"多次点选/取消后确认，效果没入堆叠"）；
            //  · 第三张（未选中的）**不在候选里**（槽已满）⇒ 点它无效（＝"第三个目标不能点击"）。
            var openWhenFull = plan062.OpenCardCandidates(new List<string> { coverts[0].UID, coverts[1].UID },
                new List<string>());
            Assert.IsTrue(openWhenFull.Contains(coverts[0].UID) && openWhenFull.Contains(coverts[1].UID),
                "已选中的两张仍在候选里（保持已选中高亮，避免被『失效剔除』误删）");
            Assert.IsFalse(openWhenFull.Contains(coverts[2].UID),
                "两个槽都满了 ⇒ **第三张不可点**（协调器还会用『至多 2 张』上限再挡一次）");

            // ---- JC021：一个"目标地区"槽（必选）＋"本方角色"/"敌方角色"（各自可留空、至少 1）----
            var jc021 = MultiTargetSlots.FindMultiTargetEffect(Load("JC021_xialuxiangfeng"), EffectTiming.OnSorceryCast);
            var src021 = game.CardManager.CreateCardInstance(Load("JC021_xialuxiangfeng"), 1);
            var mine1 = game.CardManager.CreateCardInstance(Probe("TS_k15_plan_m1"), 1);
            game.ZoneManager.MoveToArea(mine1, a1, 1);
            var mine2 = game.CardManager.CreateCardInstance(Probe("TS_k15_plan_m2"), 1);
            game.ZoneManager.MoveToArea(mine2, a1, 1);
            var foe1 = game.CardManager.CreateCardInstance(Probe("TS_k15_plan_f1"), 2);
            game.ZoneManager.MoveToArea(foe1, a1, 2);

            var plan021 = MultiTargetSlots.FieldPickPlan.Build(jc021, src021, 1, game.ZoneManager,
                game.CardManager, game.PlayerManager, game.AreaManager);
            Assert.IsNotNull(plan021, "JC021 应判定为 FieldMultiPick 形状");
            Assert.AreEqual(2, plan021.MaxCards, "至多 2 张卡");
            Assert.AreEqual(1, plan021.MaxAreas, "至多 1 个地区");
            Assert.AreEqual(1, plan021.MinCards, "至少 1 个角色");
            Assert.IsTrue(plan021.AreaRequired, "地区必选");
            Assert.IsTrue(plan021.SameControllerOnce, "每个玩家场上的角色只能选一个");
            Assert.AreEqual("目标角色", plan021.CardLabel, "卡组标题");
            Assert.AreEqual("目标地区", plan021.AreaLabel, "地区组标题");
            var areaCand021 = plan021.OpenAreaCandidates(new List<string>(), new List<string>());
            Assert.IsTrue(areaCand021.Contains(a1) && areaCand021.Contains(a2),
                "**地区候选非空**（界面据此高亮地区槽位；用户实测『地区没有正常高亮』时必须先保证这里不为空）");
            Assert.AreEqual(0, plan021.OpenAreaCandidates(new List<string>(), new List<string> { a2 }).Count,
                "已选地区后不再重复高亮其它地区（上限 1）");
            Assert.AreEqual("请选择目标角色（1/2）和目标地区（0/1）",
                plan021.Title(new List<string> { mine1.UID }, new List<string>()),
                "选了 1 个角色 → 角色计数变 1、**地区计数仍是 0**（用户实测『选择角色却显示地区（1/1）』的回归点）");
            Assert.AreEqual("请选择目标角色（0/2）和目标地区（0/1）", plan021.Title(new List<string>(), new List<string>()),
                "窗面标题与用户样例逐字一致");
            Assert.IsFalse(plan021.CanConfirm(new List<string>(), new List<string>()), "一个角色都没选 ⇒ 不能确认");
            Assert.IsFalse(plan021.CanConfirm(new List<string> { mine1.UID }, new List<string>()),
                "**没选地区 ⇒ 不能确认**");
            Assert.IsTrue(plan021.CanConfirm(new List<string> { mine1.UID }, new List<string> { a2 }),
                "1 个角色 + 1 个地区 ⇒ 可以确认");
            Assert.IsTrue(plan021.CanConfirm(new List<string> { mine1.UID, foe1.UID }, new List<string> { a2 }),
                "两个角色（本方＋敌方）+ 地区 ⇒ 可以确认（『每方至多 1 个』由 CanAddCard 保证）");
            Assert.IsTrue(plan021.CanAddCard(new List<string>(), new List<string>(), mine1.UID), "空格时本方角色可选");
            // 【2026-09 修复·用户实测 JC021"多点击几次取消几次再点确定，效果没有加入堆叠"】
            // 已选中且仍然合法的卡**必须留在候选里**：协调器用"不在候选 ⇒ 已失效 ⇒ 剔除"来清理选择，
            // 若候选只列"未填满槽能接受的卡"，就会把刚选中的卡删掉（越点越空 ⇒ 确认门槛永远不满足）。
            Assert.IsTrue(plan021.OpenCardCandidates(new List<string> { mine1.UID }, new List<string>())
                    .Contains(mine1.UID),
                "**已选中的角色仍在候选里**（避免被『失效剔除』误删 ⇒ 可继续点选/取消并最终确认）");
            Assert.IsFalse(plan021.CanAddCard(new List<string> { mine1.UID }, new List<string>(), mine2.UID),
                "**本方已选 1 个 ⇒ 第二个本方角色不可加选**（每个玩家场上的角色只能选一个）");
            Assert.IsTrue(plan021.CanAddCard(new List<string> { mine1.UID }, new List<string>(), foe1.UID),
                "本方已选 1 个 ⇒ 敌方角色仍可选（每方各至多 1）");
            // 禁止原地移动：选了 a1 作目的地 ⇒ a1 内角色不再是候选（会出现在 a2 的可选集合里仍可选）
            var openInA1 = plan021.OpenCardCandidates(new List<string>(), new List<string> { a1 });
            Assert.IsFalse(openInA1.Contains(mine1.UID), "**选了 a1 地区 ⇒ a1 内的角色不可再选**（禁止原地移动）");
            var openNoArea = plan021.OpenCardCandidates(new List<string>(), new List<string>());
            Assert.IsTrue(openNoArea.Contains(mine1.UID), "未选地区时该角色仍是候选");
            var choices021 = plan021.MapPicks(new List<string> { mine1.UID }, new List<string> { a2 });
            Assert.IsFalse(plan021.OpenCardCandidates(new List<string> { mine1.UID }, new List<string> { a1 }).Contains(mine1.UID),
                "**选了 a1 作目的地后，已在 a1 内且先前被选中的角色要从候选里消失** ⇒ 协调器据此自动取消该选择（用户实测 JC021 口径）");            int filledCards = 0;
            foreach (var ch in choices021) if (!ch.IsEmpty) filledCards++;
            Assert.AreEqual(2, filledCards, "地区槽 + 本方角色槽各被填上（共 2 个非空槽）");
        }


        /// <summary>协议往返：新 Kind 与新公开字段必须能过 JSON（漏改会静默丢字段）。</summary>
        public void TestK15_联机_协议字段往返()
        {
            var req = new Script.Net.ChoiceRequest
            {
                RequestId = "req_1", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.MultiTargetSlot,
                Prompt = "请选择本方角色（第 2/3 个目标槽）",
                SlotIndex = 1, SlotCount = 3, SlotOptional = true, SlotLabel = "本方角色",
                ForceChoice = false, OrderContextId = "mt_1_1_1",
            };
            req.OptionUids.Add("inst_x");
            req.OptionLabels.Add("凯拉");

            var json = Script.Net.ChoiceSerializer.Serialize(req);
            var back = Script.Net.ChoiceSerializer.DeserializeRequest(json);
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.MultiTargetSlot, back.RequestKind, "Kind 往返");
            Assert.AreEqual(1, back.SlotIndex, "SlotIndex 往返");
            Assert.AreEqual(3, back.SlotCount, "SlotCount 往返");
            Assert.IsTrue(back.SlotOptional, "SlotOptional 往返");
            Assert.AreEqual("本方角色", back.SlotLabel, "SlotLabel 往返");
            Assert.AreEqual("mt_1_1_1", back.OrderContextId, "OrderContextId 往返");
            Assert.AreEqual("凯拉", back.OptionLabels[0], "OptionLabels 往返");

            // 【2026-09】"一个窗多选"（JC062/JC021）新增的公开字段同样必须过 JSON
            var mpReq = new Script.Net.ChoiceRequest
            {
                RequestId = "req_2", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.FieldMultiPick,
                Prompt = "请选择目标角色（0/2）和目标地区（0/1）",
                MultiCardLabel = "目标角色", MultiAreaLabel = "目标地区",
                MultiMaxCards = 2, MultiMaxAreas = 1, MultiMinCards = 1,
                MultiAreaRequired = true, MultiSameControllerOnce = true,
            };
            mpReq.OptionUids.Add("inst_c1");
            mpReq.OptionUids.Add("inst_c2");
            mpReq.OptionAreaUids.Add("area_2");
            var mpJson = Script.Net.ChoiceSerializer.Serialize(mpReq);
            var mpBack = Script.Net.ChoiceSerializer.DeserializeRequest(mpJson);
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.FieldMultiPick, mpBack.RequestKind, "FieldMultiPick Kind 往返");
            Assert.AreEqual("目标角色", mpBack.MultiCardLabel, "MultiCardLabel 往返");
            Assert.AreEqual("目标地区", mpBack.MultiAreaLabel, "MultiAreaLabel 往返");
            Assert.AreEqual(2, mpBack.MultiMaxCards, "MultiMaxCards 往返");
            Assert.AreEqual(1, mpBack.MultiMaxAreas, "MultiMaxAreas 往返");
            Assert.AreEqual(1, mpBack.MultiMinCards, "MultiMinCards 往返");
            Assert.IsTrue(mpBack.MultiAreaRequired, "MultiAreaRequired 往返");
            Assert.IsTrue(mpBack.MultiSameControllerOnce, "MultiSameControllerOnce 往返");
            Assert.AreEqual(2, mpBack.OptionUids.Count, "卡候选往返");
            Assert.AreEqual("area_2", mpBack.OptionAreaUids[0], "地区候选往返");

            var mpRes = new Script.Net.ChoiceResult { RequestId = "req_2", Confirmed = true };
            mpRes.SelectedUids.Add("inst_c1");
            mpRes.SelectedAreaUids.Add("area_2");
            var mpRback = Script.Net.ChoiceSerializer.DeserializeResult(
                Script.Net.ChoiceSerializer.SerializeResult(mpRes));
            Assert.AreEqual("inst_c1", mpRback.SelectedUids[0], "已选卡往返");
            Assert.AreEqual("area_2", mpRback.SelectedAreaUids[0], "已选地区往返（新公开字段）");

            var res = new Script.Net.ChoiceResult { RequestId = "req_1", Confirmed = true };
            var rjson = Script.Net.ChoiceSerializer.SerializeResult(res);
            var rback = Script.Net.ChoiceSerializer.DeserializeResult(rjson);
            Assert.IsTrue(rback.Confirmed, "ChoiceResult.Confirmed 往返（public 字段 + IncludeFields）");
        }
    }
}
