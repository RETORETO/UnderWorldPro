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
    /// 【P3·K27「手牌行动」：在手牌中发动激活能力 → JC110 闲人的绿地①】
    /// 卡文：灵体 手牌行动4：将闲人的绿地封印在目标角色上。
    ///
    /// 交付：
    ///  · **生效区声明即许可**：激活类效果声明 `Scope = ZoneScope.HandZone` 即可在**手牌**中发动
    ///    （无需新数据标志）；`EffectEngine.ActivateAbility` 新增**来源生效区门禁**（默认角色=争夺区、
    ///    附属=附属区；手牌行动＝手牌）——顺带堵住"来源不在生效区也能激活"的漏洞；
    ///  · `ActivateEligibility`（`CanActivate` / `GetEligibleAbilities` 候选扫描）把手牌来源纳入；
    ///  · View：`CardUI` 双击"手牌中的手牌行动卡"即请求激活（该分支 `AcceptEvent` 先于拖拽，普通手牌不受影响）；
    ///  · 费用/行动步骤/目标规则照常（卡文"手牌**行动**4"）。
    /// </summary>
    public class P3HandActivateTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_ha_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        // ==================== ① 结构 ====================

        public void TestHandActivate_JC110结构()
        {
            var tmpl = Load("JC110_xianrendelvdi");
            Assert.IsNotNull(tmpl, "JC110 模板应可加载");
            var e = FindEffect(tmpl, "JC110_1_HandActionSealSelfOnCharacter");
            Assert.IsNotNull(e, "JC110 应有『手牌行动4：把自身封印在目标角色上』");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活");
            Assert.AreEqual(ZoneScope.HandZone, e.Scope, "**生效区＝手牌**（手牌行动的单一表达）");
            Assert.AreEqual("行动", e.ActivateWindow, "窗口＝行动");
            Assert.AreEqual(4, e.Cost, "卡文『行动4』→ 费用 4");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.NeedsSelection, "需要选目标");
            Assert.AreEqual(ActionVerb.Seal, e.Actions[0].Verb, "动作＝封印");
            Assert.AreEqual("自身", e.Actions[0].Args[0], "被封印者＝自身");
            Assert.AreEqual("目标", e.Actions[0].Args[1], "宿主＝目标角色");
        }

        // ==================== ② 生效区门禁：手牌可发动、离手不可发动 ====================

        public void TestHandActivate_手牌中可发动离手被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);       // JC110 颜色 5 + 费用 4
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            Assert.IsTrue(ActivateEligibility.CanActivate(unit, game.TurnManager, game.PlayerManager, game.CardManager),
                "手牌中的『手牌行动』卡可发动（候选/高亮纳入手牌）");

            // 把卡移到场上（不再是手牌）→ 生效区不符 → 拒绝
            game.ZoneManager.MoveToArea(unit, area, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            EffectEngine.Instance.ActivateAbility(unit, e, victim);
            Assert.IsTrue(game.StackManager.IsEmpty, "离手后（生效区＝手牌）拒绝发动");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "被拒 → 不扣费");
            Assert.IsFalse(victim.IsSealed, "未发生封印");
        }

        // ==================== ③ 端到端：从手牌发动 → 付费 → 自身封印在目标角色上 ====================

        public void TestHandActivate_从手牌发动并封印自身()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            EffectEngine.Instance.ActivateAbility(unit, e, victim);

            Assert.IsFalse(game.StackManager.IsEmpty, "手牌行动 → 入堆叠");
            Assert.AreEqual(manaBefore - 4, game.PlayerManager.GetAvailableMana(1), "支付 4 费");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.SealedZone, unit.CurrentZone, "本卡已进入**封印区**");
            Assert.IsTrue(ReferenceEquals(victim, unit.SealedBy), "封印宿主＝目标角色");
            Assert.IsTrue(victim.SealedCards.Contains(unit), "目标角色记录被封印的本卡");
            Assert.IsTrue(victim.IsSealed, "目标角色处于『被封印』状态");
        }

        // ==================== ④ 费用不足 → 拒绝（门禁顺序：付费前） ====================

        public void TestHandActivate_费用不足被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim3"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = FindEffect(unit.PlayerTemplate, "JC110_1_HandActionSealSelfOnCharacter");
            EffectEngine.Instance.ActivateAbility(unit, e, victim);

            Assert.IsTrue(game.StackManager.IsEmpty, "无资产 → 付不起 4 费 → 拒绝");
            Assert.AreEqual(CardZone.Hand, unit.CurrentZone, "卡仍在手牌");
        }

        // ==================== ⑤ 候选扫描：手牌来源进入可发动列表 ====================

        public void TestHandActivate_候选含手牌来源()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)5);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var unit = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToHand(unit, 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_ha_plain"), 1);
            game.ZoneManager.MoveToHand(plain, 1);
            // 场上需有**合法目标**（否则门禁 4「需选目标时场上须有合法目标」会让能力不进候选）
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ha_victim4"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var list = ActivateEligibility.GetEligibleAbilities(1, game.TurnManager, game.PlayerManager, game.CardManager);
            bool hasUnit = false, hasPlain = false;
            foreach (var (src, ab) in list)
            {
                if (src.UID == unit.UID && ab.EffectId == "JC110_1_HandActionSealSelfOnCharacter") hasUnit = true;
                if (src.UID == plain.UID) hasPlain = true;
            }
            Assert.IsTrue(hasUnit, "手牌中的『手牌行动』进入可发动候选");
            Assert.IsFalse(hasPlain, "普通手牌卡（无手牌生效区能力）不在候选里");
        }
    }
}
