using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K22 牺牲代价（续）→ JC093 怨灵咒刃 / JC096 阿格里帕之犬 / JC038 列恩·爱弗罗德】
    /// 口径（用户 2026-09）：
    ///  · 牺牲由**发动者自己选择**；**除非卡文明确写"暗藏者"，否则不能牺牲暗藏者**；**牺牲＝死亡→触发死亡时机**；
    ///  · **卡文没写"另一个"时，可以牺牲来源卡自身**（JC096「牺牲一个角色」、JC038「牺牲一个吸血鬼或奴仆角色」）。
    /// </summary>
    public class P3SacrificeCost2Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 877 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
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

        private static PlayerCardData Probe(string uid, string subCategory = "人类", int defense = 3)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            if (!string.IsNullOrEmpty(subCategory)) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area,
            bool covert = false)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            if (covert) card.SetCovert(true);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void PrepareActionWindow(GameInitializer game)
        {
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        /// <summary>把附属结附到宿主（沿用既有 `P3GrantToHostTests` 的落地方式：结附区 + 持续登记）。</summary>
        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        /// <summary>双方连续让过 → 结算堆叠（把入栈的激活/触发跑完）。</summary>
        private static void ResolveStack(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① 结构（三张卡） ====================

        public void TestSacrifice2_三张卡结构()
        {
            var jc093 = Load("JC093_yuanlingzhouren");
            var e93 = FindEffect(jc093, "JC093_1_SacrificeGrantHostBladeStar");
            Assert.IsNotNull(e93, "JC093 应有『牺牲一个角色→宿主获得黑刀黑星』能力");
            Assert.AreEqual(EffectTiming.AttachmentActivated, e93.Timing, "附属激活能力");
            Assert.AreEqual("快速", e93.ActivateWindow, "快速行动");
            Assert.AreEqual(2, e93.MaxUsesPerTurn, "每回合至多发动两次");
            Assert.IsNotNull(e93.SacrificeCost, "声明了牺牲代价");
            Assert.AreEqual(2, e93.Actions.Count, "两条 Gain（黑刀 + 黑星）");
            Assert.AreEqual("结附宿主", e93.Actions[0].Args[2], "授予主体＝结附宿主");
            Assert.AreEqual("结附宿主", e93.Actions[1].Args[2], "授予主体＝结附宿主");
            Assert.AreEqual("直到回合结束", e93.Duration, "只到回合结束");

            var jc096 = Load("JC096_agelipazhiquan");
            var e96 = FindEffect(jc096, "JC096_1_SacrificeThenDraw");
            Assert.IsNotNull(e96, "JC096 应有『牺牲一个角色→抓一张牌』能力");
            Assert.AreEqual(1, e96.Cost, "快速行动1");
            Assert.AreEqual("横置本角色", e96.ActivateCondition, "横置自身为代价");
            Assert.IsNotNull(e96.SacrificeCost, "声明了牺牲代价");
            Assert.IsFalse(e96.SacrificeCost.ExcludeSource, "未写『另一个』→ 可牺牲来源自身（用户口径）");
            Assert.AreEqual(ActionVerb.Draw, e96.Actions[0].Verb, "动作为抓牌");

            var jc038 = Load("JC038_lieenaifuluode");
            var e38 = FindEffect(jc038, "JC038_C_SacrificeVampireOrServant");
            Assert.IsNotNull(e38, "JC038 应有『牺牲吸血鬼或奴仆→对手弃牌+抓牌』能力");
            Assert.IsNotNull(e38.SacrificeCost, "声明了牺牲代价");
            Assert.AreEqual(2, e38.SacrificeCost.SubCategories.Count, "子类别白名单＝吸血鬼 / 奴仆");
            Assert.IsFalse(e38.SacrificeCost.ExcludeSource, "未写『另一个』→ 可牺牲来源自身");
            Assert.AreEqual(TargetKind.Player, e38.Target.Kind, "目标＝玩家（目标对手）");
            Assert.AreEqual(ActionVerb.Discard, e38.Actions[0].Verb, "第一条＝弃牌");
            Assert.AreEqual("目标玩家", e38.Actions[0].Args[1], "弃牌者＝目标玩家");
            Assert.AreEqual("自选", e38.Actions[0].Args[2], "由被指定者自己选（§7.4 口径①）");
            Assert.AreEqual(ActionVerb.Draw, e38.Actions[1].Verb, "第二条＝抓牌");
        }

        // ==================== ② JC093：宿主获得黑刀+黑星（到回合结束） ====================

        public void TestSacrifice2_JC093授予结附宿主()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_s2_host"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_s2_victim"), 1, areas[0]);
            var blade = game.CardManager.CreateCardInstance(Load("JC093_yuanlingzhouren"), 1);
            game.ZoneManager.MoveToArea(blade, areas[0], 1);
            Attach(game, host, blade);   // 结附于宿主
            PrepareActionWindow(game);
            var ability = FindEffect(blade.PlayerTemplate, "JC093_1_SacrificeGrantHostBladeStar");

            int strengthBefore = host.CurrentStrength;
            int powerBefore = host.CurrentPower;

            EffectEngine.Instance.ActivateAbility(blade, ability, null, -1, victim.UID);

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "代价先付：被牺牲者立刻进墓地");
            ResolveStack(game);

            Assert.AreEqual(strengthBefore + 1, host.CurrentStrength, "宿主获得黑刀（战斗 +1）");
            Assert.AreEqual(powerBefore + 1, host.CurrentPower, "宿主获得黑星（势力 +1）");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.AreEqual(strengthBefore, host.CurrentStrength, "回合结束 → 黑刀撤销");
            Assert.AreEqual(powerBefore, host.CurrentPower, "回合结束 → 黑星撤销");
        }

        // ==================== ③ JC093：每回合至多两次（K23） ====================

        public void TestSacrifice2_JC093每回合两次上限()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_s2_host2"), 1, areas[0]);
            OnField(game, Probe("TS_s2_v1"), 1, areas[0]);
            OnField(game, Probe("TS_s2_v2"), 1, areas[0]);
            OnField(game, Probe("TS_s2_v3"), 1, areas[0]);
            var blade = game.CardManager.CreateCardInstance(Load("JC093_yuanlingzhouren"), 1);
            game.ZoneManager.MoveToArea(blade, areas[0], 1);
            Attach(game, host, blade);
            PrepareActionWindow(game);
            var ability = FindEffect(blade.PlayerTemplate, "JC093_1_SacrificeGrantHostBladeStar");

            for (int i = 0; i < 2; i++)
            {
                EffectEngine.Instance.ActivateAbility(blade, ability, null);
                ResolveStack(game);
            }
            int usesAfterTwo = blade.GetAbilityUsesThisTurn(ability.EffectId);

            EffectEngine.Instance.ActivateAbility(blade, ability, null);   // 第 3 次

            Assert.AreEqual(2, usesAfterTwo, "前两次发动均被接受");
            Assert.AreEqual(2, blade.GetAbilityUsesThisTurn(ability.EffectId), "第 3 次被次数上限拒绝（未登记发动）");
        }

        // ==================== ④ JC096：可以牺牲来源卡自身（用户口径） ====================

        public void TestSacrifice2_JC096可牺牲自身()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC096_agelipazhiquan"), 1, areas[0]);
            for (int i = 0; i < 3; i++)
            {
                var t = Probe($"TS_s2_asset{i}");
                var asset = game.CardManager.CreateCardInstance(t, 1);
                game.ZoneManager.MoveToAsset(asset, 1);      // 供 1 点费用
            }
            PrepareActionWindow(game);
            var ability = FindEffect(dog.PlayerTemplate, "JC096_1_SacrificeThenDraw");
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ActivateAbility(dog, ability, null, -1, dog.UID);   // 牺牲自身

            Assert.AreEqual(CardZone.Graveyard, dog.CurrentZone, "未写『另一个』→ 来源自身可被牺牲");
            ResolveStack(game);
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "抓一张牌（代价付出后主效果照常结算）");
        }

        // ==================== ⑤ JC038：子类别筛选（吸血鬼/奴仆）+ 玩家目标弃牌 ====================

        public void TestSacrifice2_JC038牺牲奴仆并令对手弃牌()
        {
            var game = NewGame();
            var areas = Areas(game);
            var lien = OnField(game, Load("JC038_lieenaifuluode"), 1, areas[0]);
            var servant = OnField(game, Probe("TS_s2_servant", "奴仆"), 1, areas[0]);
            var human = OnField(game, Probe("TS_s2_human", "人类"), 1, areas[0]);
            var enemyHand = game.CardManager.CreateCardInstance(Probe("TS_s2_enemyhand"), 2);
            game.ZoneManager.MoveToHand(enemyHand, 2);
            PrepareActionWindow(game);
            var ability = FindEffect(lien.PlayerTemplate, "JC038_C_SacrificeVampireOrServant");

            // 候选过滤：只有『吸血鬼或奴仆』可作代价
            var candidates = ActivateEligibility.GetSacrificeCandidates(lien, ability);
            Assert.IsTrue(candidates.Contains(servant), "奴仆 → 合法候选");
            Assert.IsFalse(candidates.Contains(human), "普通人类 → 不是合法候选（子类别筛选）");
            int enemyHandBefore = game.TurnManager.GetHandOf(2).Count;
            int ownHandBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ActivateAbility(lien, ability, null, 2, servant.UID);   // 目标玩家＝P2
            ResolveStack(game);

            Assert.AreEqual(CardZone.Graveyard, servant.CurrentZone, "奴仆被牺牲（代价）");
            Assert.AreEqual(enemyHandBefore - 1, game.TurnManager.GetHandOf(2).Count, "目标对手弃一张手牌（由他自己选）");
            Assert.AreEqual(ownHandBefore + 1, game.TurnManager.GetHandOf(1).Count, "然后你抓一张牌");
        }

        // ==================== ⑥ JC038：卡文未写"另一个" → 来源自身（列恩是吸血鬼）可作代价 ====================

        public void TestSacrifice2_JC038可牺牲自身()
        {
            var game = NewGame();
            var areas = Areas(game);
            var lien = OnField(game, Load("JC038_lieenaifuluode"), 1, areas[0]);   // 其印刷子类别含「吸血鬼」
            OnField(game, Probe("TS_s2_only_human", "人类"), 1, areas[0]);          // 普通人类不是合法代价
            PrepareActionWindow(game);
            var ability = FindEffect(lien.PlayerTemplate, "JC038_C_SacrificeVampireOrServant");

            var candidates = ActivateEligibility.GetSacrificeCandidates(lien, ability);
            Assert.IsTrue(candidates.Contains(lien), "来源自身是『吸血鬼』且卡文未写『另一个』→ 自身为合法代价候选（用户口径）");
            Assert.AreEqual(1, candidates.Count, "场上没有其它吸血鬼/奴仆 → 唯一候选是来源自身");
            Assert.IsTrue(ActivateEligibility.CanActivate(lien, game.TurnManager, game.PlayerManager, game.CardManager),
                "有合法候选（自身）→ 该能力可发动");
        }
    }
}
