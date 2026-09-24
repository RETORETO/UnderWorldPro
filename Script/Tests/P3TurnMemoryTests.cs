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
    /// 【P3·"本回合"粒度的事件记忆位 + JC053 鸣钟教派徽记②】
    /// 卡文：快速行动1，横置：本回合中，**若有敌方势力标志曾被移除**，你可以抓一张牌。
    ///
    /// 交付的机制：
    ///  · **记忆位**：`AreaManager.MarkPowerMarkRemovedThisTurn` 在 `RemovePowerMark` **真实移除 &gt;0 个**时置位
    ///    （遏制/其它移除皆算；请求但无可移除 → 不置位）；**每个回合开始时（重置步骤）清空** → 粒度＝"本回合"；
    ///  · **谓词** `EnemyPowerMarkRemovedThisTurn`：以来源卡控制者为敌我基准，**任一对手**本回合曾被移除即满足；
    ///  · **发动门禁前置**：激活类效果若声明了 `EffectCondition.Predicate`，由
    ///    `EffectEngine.ActivateAbility` 在**付费/横置/牺牲之前**判定（不满足 → 拒绝发动、代价不付出），
    ///    并由 `ActivateEligibility` 同步用于 UI 高亮/候选（置灰）。
    /// </summary>
    public class P3TurnMemoryTests : TestRunner
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
                var tmpl = Probe($"TS_tm_asset_{playerId}_{i}", CardCategory.Attachment);
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

        /// <summary>把 JC053 徽记结附到 P1 的一个地区（`MoveToAttachedZone` 即附属落位，激活不依赖秘社绑定）。</summary>
        private static CardInstance PlaceBadge(GameInitializer game, string area, int playerId = 1)
        {
            var badge = game.CardManager.CreateCardInstance(Load("JC053_mingzhongjiaopaihuiji"), playerId);
            game.ZoneManager.MoveToAttachedZone(badge, area, playerId);
            return badge;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>
        /// 放一张**探针角色**（只有一条激活能力，条件＝predicate）到场上——用于验证"能力粒度"的门禁
        /// （`ActivateEligibility.CanActivate` 是**整卡**判定：JC053 同卡还挂着不受条件影响的减费能力，
        /// 用它验证会被掩盖；`GetEligibleAbilities` 只扫争夺区卡，不含附属）。
        /// </summary>
        private static CardInstance PlaceConditionProbe(GameInitializer game, string area, string predicate)
        {
            var tmpl = Probe("TS_tm_probe");
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_tm_probe_effect",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 0,
                Forced = false,
                Condition = new EffectCondition { Predicate = predicate },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
            });
            var inst = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(inst, area, 1);
            return inst;
        }

        // ==================== ① 结构 ====================

        public void TestTurnMemory_JC053结构()
        {
            var tmpl = Load("JC053_mingzhongjiaopaihuiji");
            Assert.IsNotNull(tmpl, "JC053 模板应可加载");

            var e = FindEffect(tmpl, "JC053_2_DrawIfEnemyMarkRemoved");
            Assert.IsNotNull(e, "JC053 应有『本回合敌方标志曾被移除 → 抓一张』激活");
            Assert.AreEqual(EffectTiming.AttachmentActivated, e.Timing, "附属激活");
            Assert.AreEqual("快速", e.ActivateWindow, "窗口＝快速");
            Assert.AreEqual(1, e.Cost, "卡文『快速行动1』→ 费用 1");
            Assert.IsTrue(e.ActivateCondition.Contains("横置本角色"), "横置本徽记为代价");
            Assert.IsNotNull(e.Condition, "应有发动前提条件");
            Assert.AreEqual("EnemyPowerMarkRemovedThisTurn", e.Condition.Predicate,
                "前提＝本回合有敌方势力标志曾被移除");
            Assert.AreEqual(ActionVerb.Draw, e.Actions[0].Verb, "动作＝抓牌");
            Assert.AreEqual("1", e.Actions[0].Args[0], "抓 1 张");
            Assert.IsFalse(e.Forced, "『你可以』→ 非强制");
        }

        // ==================== ② 记忆位：置位 / 不置位 / 清空 ====================

        public void TestTurnMemory_移除标志置位()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            Assert.IsFalse(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "前置：未置位");

            // 请求移除但该玩家在该地区没有标记 → 实际移除 0 → **不置位**
            game.AreaManager.RemovePowerMark(area, 2, 1);
            Assert.IsFalse(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "移除 0 个 → 不置位");

            game.AreaManager.AddPowerMark(area, 2, 2);
            int removed = game.AreaManager.RemovePowerMark(area, 2, 1);
            Assert.AreEqual(1, removed, "实际移除 1 个");
            Assert.IsTrue(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "真实移除 → 置位");

            Assert.IsFalse(game.AreaManager.HasPowerMarkRemovedThisTurn(1), "只登记被移除的一方（P1 未置位）");
        }

        public void TestTurnMemory_回合开始清空()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            game.AreaManager.AddPowerMark(area, 2, 1);
            game.AreaManager.RemovePowerMark(area, 2, 1);
            Assert.IsTrue(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "前置：已置位");

            game.TurnManager.NextTurn();   // 进入新回合的重置步骤（DoReset → ResetPowerMarkRemovedThisTurn）

            Assert.IsFalse(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "回合开始 → 记忆位清空（粒度＝本回合）");
        }

        // ==================== ③ 谓词：只看"敌方" ====================

        public void TestTurnMemory_谓词只看敌方()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var badge = PlaceBadge(game, area, 1);   // 控制者＝P1

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("EnemyPowerMarkRemovedThisTurn", badge, null, null, -1, null),
                "前置：无人被移除 → 不满足");

            // 本方（P1）标志被移除 → 对 P1 而言不是"敌方" → 仍不满足
            game.AreaManager.AddPowerMark(area, 1, 1);
            game.AreaManager.RemovePowerMark(area, 1, 1);
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("EnemyPowerMarkRemovedThisTurn", badge, null, null, -1, null),
                "本方标志被移除 → 不满足（卡文要求『敌方』）");

            // 敌方（P2）标志被移除 → 满足
            game.AreaManager.AddPowerMark(area, 2, 1);
            game.AreaManager.RemovePowerMark(area, 2, 1);
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("EnemyPowerMarkRemovedThisTurn", badge, null, null, -1, null),
                "敌方标志被移除 → 满足");
        }

        // ==================== ④ 门禁：条件不满足 → 拒绝发动且**不付代价** ====================

        public void TestTurnMemory_条件不满足则拒绝激活且不付代价()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)6);
            var badge = PlaceBadge(game, area, 1);
            game.TurnManager.PriorityPlayerId = 1;

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            var e = FindEffect(badge.PlayerTemplate, "JC053_2_DrawIfEnemyMarkRemoved");
            EffectEngine.Instance.ActivateAbility(badge, e, null);

            Assert.IsTrue(game.StackManager.IsEmpty, "条件不满足 → 拒绝发动（不入堆叠）");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "**不扣费**（门禁在付费之前）");
            Assert.IsFalse(badge.IsTapped, "**不横置**（代价未付出）");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "未抓牌");

            // UI 可用性判定同源（**能力粒度**：探针卡只挂这一条能力）：
            // 条件不满足 → 该能力不在可发动集合里（UI 置灰）。
            var probe = PlaceConditionProbe(game, area, "EnemyPowerMarkRemovedThisTurn");
            Assert.IsFalse(ActivateEligibility.CanActivate(probe, game.TurnManager, game.PlayerManager, game.CardManager),
                "条件不满足 → CanActivate=false（UI 置灰）");
        }

        // ==================== ⑤ 端到端：敌方标志被移除 → 横置 + 付费 + 抓一张 ====================

        public void TestTurnMemory_条件满足则激活抓牌()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)6);
            var badge = PlaceBadge(game, area, 1);
            game.TurnManager.PriorityPlayerId = 1;

            // 制造"敌方势力标志曾被移除"
            game.AreaManager.AddPowerMark(area, 2, 2);
            game.AreaManager.RemovePowerMark(area, 2, 1);
            Assert.IsTrue(game.AreaManager.HasPowerMarkRemovedThisTurn(2), "前置：敌方标志曾移除");

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;

            var probe = PlaceConditionProbe(game, area, "EnemyPowerMarkRemovedThisTurn");
            Assert.IsTrue(ActivateEligibility.CanActivate(probe, game.TurnManager, game.PlayerManager, game.CardManager),
                "条件满足 → 该能力可发动（UI 可点）");

            var e = FindEffect(badge.PlayerTemplate, "JC053_2_DrawIfEnemyMarkRemoved");
            EffectEngine.Instance.ActivateAbility(badge, e, null);
            Assert.IsFalse(game.StackManager.IsEmpty, "条件满足 → 入堆叠");
            ResolveStackOnce(game);

            Assert.IsTrue(badge.IsTapped, "横置本徽记（代价已付出）");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 费");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "抓了 1 张");
            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "牌库 -1");
        }

        /// <summary>回合切换后条件不再成立 → 同一条能力又被拒绝（记忆位已清空）。</summary>
        public void TestTurnMemory_新回合后条件失效()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 2, (CardColor)6);
            var badge = PlaceBadge(game, area, 1);

            game.AreaManager.AddPowerMark(area, 2, 1);
            game.AreaManager.RemovePowerMark(area, 2, 1);
            game.TurnManager.NextTurn();   // 进入新回合：记忆位清空
            game.TurnManager.PriorityPlayerId = 1;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var e = FindEffect(badge.PlayerTemplate, "JC053_2_DrawIfEnemyMarkRemoved");
            EffectEngine.Instance.ActivateAbility(badge, e, null);

            Assert.IsTrue(game.StackManager.IsEmpty, "新回合条件失效 → 拒绝发动");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不扣费");
        }
    }
}
