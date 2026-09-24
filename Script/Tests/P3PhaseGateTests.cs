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
    /// 【K26·发动窗口细化（阶段排除）】按用户 2026-09 口径实现并锁定：
    /// 效果可声明 `EffectData.ExcludedPhases`（如 `"分配战斗伤害阶段"` → `TurnPhase.CombatStrength`），
    /// 在该阶段**不可发动 / 不可打出**；单一判定实现 `PhaseGateRule`，三处消费点同解
    /// （`ActivateEligibility` 高亮与候选 / `EffectEngine.ActivateAbility` 服务端权威 / `TurnManager.PlayCard` 事务卡打出）。
    /// 覆盖卡：**JC008 灵能激发**（FAQ：不能在分配战斗伤害阶段使用）。
    /// </summary>
    public class P3PhaseGateTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 83 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        /// <summary>给某玩家放 n 张"颜色1、无色费用"的资产（未横置）→ 提供费用与颜色忠诚（供 JC008 打出）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_k26_asset_{playerId}_{i}", CardName = "探针资产", Cost = 0,
                    CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl.Colors.Add((CardColor)1);
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static bool StackHas(GameInitializer game, string sourceUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == sourceUid) return true;
            return false;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 判定层：状态名 → 判据（伤害分配窗口打开中） ====================

        public void TestK26_阶段名映射与未登记告警()
        {
            var e = new EffectData { EffectId = "TS_k26", ExcludedPhases = "分配战斗伤害阶段" };

            // 【用户 2026-09 口径】"分配战斗伤害阶段"＝**伤害分配窗口打开的那一小段**（不是整个 CombatStrength 阶段）
            Assert.IsTrue(PhaseGateRule.IsTokenActive("分配战斗伤害阶段", GameWithDamageWindow(true).TurnManager),
                "伤害分配窗口打开中 → 该状态成立（效果被排除）");
            Assert.IsFalse(PhaseGateRule.IsTokenActive("分配战斗伤害阶段", GameWithDamageWindow(false).TurnManager),
                "窗口关闭（哪怕仍在战斗对抗阶段）→ 不排除");

            // 未登记状态名：按"不排除"处理（fail-open），不静默全禁
            Assert.IsFalse(PhaseGateRule.IsTokenActive("某个不存在的阶段", GameWithDamageWindow(true).TurnManager),
                "未登记状态名按『不排除』处理");

            var empty = new EffectData { EffectId = "TS_k26_empty" };
            Assert.IsFalse(PhaseGateRule.IsExcluded(empty, GameWithDamageWindow(true).TurnManager),
                "未声明 ExcludedPhases → 无排除（既有卡不受影响）");
        }

        /// <summary>造一局并设置"伤害分配窗口是否打开"（模拟真实窗口开关；单机/联机两条路径都会置位该标志）。</summary>
        private static GameInitializer GameWithDamageWindow(bool open)
        {
            var game = NewGame();
            game.CombatManager.IsDamageAssignmentOpen = open;
            return game;
        }

        // ==================== ② JC008 结构 ====================

        public void TestK26_JC008结构()
        {
            var tmpl = Load("JC008_lingnengjifa");
            Assert.IsNotNull(tmpl, "JC008 模板应可加载");
            var e = FindEffect(tmpl, "JC008_1_GainDefenseAndBlackBlade");
            Assert.IsNotNull(e, "JC008 应有『+1防御与黑刀』效果");
            Assert.AreEqual("分配战斗伤害阶段", e.ExcludedPhases, "应声明阶段排除（FAQ 口径）");
            Assert.AreEqual(PlayTiming.Response, e.PlayTiming, "快速行动＝可响应打出（C 档）");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
            Assert.AreEqual(2, e.Actions.Count, "两个动作都要做（+1防御 与 黑刀），不做半条效果");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
        }

        // ==================== ③ 事务卡打出门禁：伤害分配窗口打开期间不可打出 ====================

        public void TestK26_事务卡在分配战斗伤害阶段不可打出()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3);   // 提供费用（2）与颜色忠诚（1），使"费用/忠诚"不成为拒绝原因
            var card = game.CardManager.CreateCardInstance(Load("JC008_lingnengjifa"), 1);
            game.ZoneManager.MoveToHand(card, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_k26_victim"), 1);
            game.ZoneManager.MoveToArea(victim, FirstArea(game), 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.CombatStrength;
            game.CombatManager.IsDamageAssignmentOpen = true;   // 伤害分配窗口打开中

            bool playedInWindow = game.TurnManager.PlayCard(1, card.UID, null, victim.UID);
            Assert.IsFalse(playedInWindow, "JC008 不能在伤害分配窗口打开期间打出（FAQ 阶段排除）");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "被拒绝后应留在手牌");

            // 【收窄口径】同一个战斗对抗阶段、但**窗口已关闭** → 放行（证明拒绝只来自"窗口打开那一小段"）
            game.CombatManager.IsDamageAssignmentOpen = false;
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, victim.UID),
                "窗口关闭后（仍在战斗对抗阶段/或行动步骤）应能正常打出（对照组）");
        }

        // ==================== ④ 激活门禁：被排除阶段不可发动 ====================

        public void TestK26_激活效果被排除阶段不可发动()
        {
            var game = NewGame();
            string area = FirstArea(game);

            // 探针：角色激活（快速行动），声明"不能在分配战斗伤害阶段发动"
            var tmpl = new PlayerCardData
            {
                UID = "TS_k26_probe", CardName = "探针_K26", Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k26_probe_act",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ExcludedPhases = "分配战斗伤害阶段",
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } } },
            });
            tmpl._effectsInjected = true;

            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            game.TurnManager.PriorityPlayerId = 1;

            // 伤害分配窗口打开中：不可发动（高亮/候选用的同一门禁）
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.CombatStrength;
            game.CombatManager.IsDamageAssignmentOpen = true;
            Assert.IsFalse(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "窗口打开中：CanActivate 应为 false（不高亮）");
            var eligibleExcluded = ActivateEligibility.GetEligibleAbilities(1, game.TurnManager, game.PlayerManager, game.CardManager);
            Assert.IsFalse(eligibleExcluded.Exists(x => x.Source == card), "窗口打开中：候选池不应包含该激活");

            // 服务端权威门禁：即便绕过 UI 直接调用也应拒绝（无堆叠条目产生）
            EffectEngine.Instance.ActivateAbility(card, tmpl.EffectDataList[0], null);
            Assert.IsFalse(StackHas(game, card.UID), "窗口打开中：直接调用 ActivateAbility 也应被拒绝（服务端权威），不产生堆叠条目");

            // 窗口关闭（同一战斗对抗阶段）→ 可发动
            game.CombatManager.IsDamageAssignmentOpen = false;
            Assert.IsTrue(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "窗口关闭后：应可发动");
            EffectEngine.Instance.ActivateAbility(card, tmpl.EffectDataList[0], null);
            Assert.IsTrue(StackHas(game, card.UID), "窗口关闭后：激活被接受并入堆叠（效果随后由堆叠结算）");
        }

        // ==================== ⑤ 对照：无阶段声明的卡不受影响 ====================
        public void TestK26_未声明阶段的卡不受影响()
        {
            var game = NewGame();
            GiveAssets(game, 1, 2);
            var card = game.CardManager.CreateCardInstance(Probe("TS_k26_plain"), 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            Assert.IsFalse(PhaseGateRule.IsCardPlayExcluded(card.PlayerTemplate, game.TurnManager),
                "无 ExcludedPhases 的卡不受阶段排除影响（既有卡零回归）");
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, FirstArea(game)),
                "无阶段声明的角色卡在行动步骤应能正常打出");
        }

        // ==================== ⑥ 效果本体：+1防御 与 黑刀 都落地且回合结束还原 ====================

        public void TestK26_JC008效果落地与时效还原()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var src = game.CardManager.CreateCardInstance(Probe("TS_k26_src"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);
            var target = game.CardManager.CreateCardInstance(Probe("TS_k26_tgt"), 1);
            game.ZoneManager.MoveToArea(target, area, 1);

            var e = FindEffect(Load("JC008_lingnengjifa"), "JC008_1_GainDefenseAndBlackBlade");
            int defBefore = target.CurrentDefense;
            int bladeBefore = target.GetAttribute("战斗");

            EffectEngine.Instance.ResolveEffect(e, src, target);

            Assert.AreEqual(defBefore + 1, target.CurrentDefense, "目标获得 +1 防御力");
            Assert.AreEqual(bladeBefore + 1, target.GetAttribute("战斗"), "目标获得［黑刀］（战斗+1）");

            EffectEngine.Instance.ExpireTurnEndEffects(EffectEngine.Instance.CurrentTurn);

            Assert.AreEqual(defBefore, target.CurrentDefense, "回合结束后 +1 防御应还原");
            Assert.AreEqual(bladeBefore, target.GetAttribute("战斗"), "回合结束后［黑刀］应还原");
        }

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }
    }
}
