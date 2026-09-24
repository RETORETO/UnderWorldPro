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
    /// 【用户 2026-09 口径批】三张"只差一个机制"的小卡一次落地（口径见清单 §7.4b）：
    ///  · **JC022 引路仙子②**："参与了该次战斗"按**近似**判定＝**位于战斗地区且属于获胜方** →
    ///    复用既有谓词 `WinnerIsSelfInSameArea`（`OnCombatWon` 触点带胜者玩家 + 战斗地区）；
    ///  · **JC074 天空的教诲**：快速行动 → 目标角色 +［黑眼］×2（＝调查 +2）+「声望」直到回合结束
    ///    （`Gain` 两笔 → `TimedMod`，回合结束由 `ExpireTurnEndEffects` 还原）；
    ///  · **JC122 哈克·亚当斯②**：`StaticKind.NoRevealWithLockCounter` —— 来源在场期间，
    ///    **带锁定标记/锁定标志的暗藏者不能【现身】**（用户口径：只封现身方向）；
    ///    消费点＝`TurnManager.RevealCovert`（现身唯一玩家入口）经 `EffectEngine.IsRevealBlocked`。
    /// </summary>
    public class P3RulingBatchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private sealed class RecordingBus
        {
            public readonly List<GameEvent> Events = new List<GameEvent>();
            public GameEventBus Bus { get; }
            public RecordingBus()
            {
                Bus = new GameEventBus();
                Bus.SetDispatch((e, c) => Events.Add(e));
            }
            public int Count(GameEvent e)
            {
                int n = 0;
                foreach (var x in Events) if (x == e) n++;
                return n;
            }
        }

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 220 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        /// <summary>接上"记录总线 + 可手动触发的事件触点"，供战斗获胜类触发的端到端驱动。</summary>
        private static TriggerEmitPoints WireEmitPoints(GameInitializer game, RecordingBus rec)
        {
            var registry = new TriggerRegistry(rec.Bus);
            rec.Bus.SetDispatch((e, c) =>
            {
                rec.Events.Add(e);
                registry.HandleEvent(e, c);
            });
            var emit = new TriggerEmitPoints(rec.Bus);
            game.TurnManager.SetTriggerSystem(registry, emit, rec.Bus);
            return emit;
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
                CategoryFlag = category, Defense = 3,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void ResolveStack(GameInitializer game)
        {
            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked) => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① JC022：结构 + 端到端 ====================

        public void TestBatch_JC022结构()
        {
            var tmpl = Load("JC022_yinluxianzi");
            Assert.IsNotNull(tmpl, "JC022 模板应可加载");
            var e = FindEffect(tmpl, "JC022_A_DrawOnCombatWonInSameArea");
            Assert.IsNotNull(e, "JC022 应有『赢下本卡所在地区战斗对抗 → 抓一张』效果");
            Assert.AreEqual(EffectTiming.OnCombatWon, e.Timing, "时机＝战斗对抗获胜");
            Assert.AreEqual("WinnerIsSelfInSameArea", e.Condition.Predicate,
                "条件＝位于战斗地区且属于获胜方（用户口径的近似判定）");
            Assert.AreEqual(ActionVerb.Draw, e.Actions[0].Verb, "动作＝抓牌");
            Assert.AreEqual("1", e.Actions[0].Args[0], "抓 1 张");
        }

        public void TestBatch_JC022赢下本地区战斗则抓一张()
        {
            var game = NewGame();
            var rec = new RecordingBus();
            var emit = WireEmitPoints(game, rec);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            string area = Areas(game)[0];

            OnField(game, Load("JC022_yinluxianzi"), 1, area);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int drawsBefore = rec.Count(GameEvent.OnDraw);

            emit.OnCombatWon(1, area);      // 生产触点：本方赢下该地区的战斗对抗
            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发已入堆叠");
            ResolveStack(game);

            Assert.AreEqual(handBefore + 1, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "抓了一张牌");
            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "牌库少一张");
            Assert.AreEqual(drawsBefore + 1, rec.Count(GameEvent.OnDraw), "这是真·抓牌 → 广播 OnDraw");
        }

        public void TestBatch_JC022敌方获胜与本地区外获胜均不触发()
        {
            var game = NewGame();
            var rec = new RecordingBus();
            var emit = WireEmitPoints(game, rec);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = Areas(game);

            OnField(game, Load("JC022_yinluxianzi"), 1, areas[0]);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;

            emit.OnCombatWon(2, areas[0]);   // 敌方赢下本地区
            Assert.AreEqual(0, game.StackManager.Entries.Count, "敌方获胜 → 不入堆叠（「属于获胜方」不成立）");

            emit.OnCombatWon(1, areas[1]);   // 本方赢下**别的**地区
            Assert.AreEqual(0, game.StackManager.Entries.Count, "别的地区获胜 → 不入堆叠（「位于战斗地区」不成立）");

            ResolveStack(game);
            Assert.AreEqual(handBefore, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "手牌不变");
        }

        // ==================== ② JC074：结构 + 授予与到期还原 ====================

        public void TestBatch_JC074结构()
        {
            var tmpl = Load("JC074_tiankongdejiaohui");
            Assert.IsNotNull(tmpl, "JC074 模板应可加载");
            Assert.IsTrue(tmpl.LimitToActionPhaseOnly, "卡文『快速行动』→ 限行动阶段（与既有同类事务卡一致）");
            var e = FindEffect(tmpl, "JC074_A_BlackEye2AndRenown");
            Assert.IsNotNull(e, "JC074 应有『目标角色 +黑眼×2 +声望』效果");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.AreEqual("黑眼", e.Actions[0].Args[0], "第 1 笔＝黑眼（＝调查图标）");
            Assert.AreEqual("2", e.Actions[0].Args[1], "黑眼 ×2");
            Assert.AreEqual("目标", e.Actions[0].Args[2], "主体＝目标角色");
            Assert.AreEqual("声望", e.Actions[1].Args[0], "第 2 笔＝授予关键词『声望』");
            Assert.AreEqual("直到回合结束", e.Duration, "期限＝直到回合结束");
        }

        public void TestBatch_JC074黑眼二连与声望_回合结束还原()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            string area = Areas(game)[0];

            var spell = game.CardManager.CreateCardInstance(Load("JC074_tiankongdejiaohui"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var target = OnField(game, Probe("TS_rb_gain_target"), 1, area);

            int invBefore = target.GetAttribute("调查");
            var e = FindEffect(spell.PlayerTemplate, "JC074_A_BlackEye2AndRenown");
            EffectEngine.Instance.ResolveEffect(e, spell, target, "");

            Assert.AreEqual(invBefore + 2, target.GetAttribute("调查"), "黑眼×2 → 调查 +2");
            Assert.IsTrue(target.HasKeyword("声望"), "获得关键词『声望』");

            EffectEngine.Instance.ExpireTurnEndEffects(int.MaxValue);   // 回合结束：时效项出栈还原
            Assert.AreEqual(invBefore, target.GetAttribute("调查"), "回合结束后调查还原");
            Assert.IsFalse(target.HasKeyword("声望"), "回合结束后『声望』失效");
        }

        // ==================== ③ JC122：静止规则 + 现身门禁 ====================

        public void TestBatch_JC122结构()
        {
            var tmpl = Load("JC122_hakeyadangsi");
            var e = FindEffect(tmpl, "JC122_2_NoRevealForLockedCovert");
            Assert.IsNotNull(e, "JC122 应有『带锁定标记的暗藏者不能现身』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "时机＝持续");
            Assert.IsNotNull(e.StaticEffects, "以静止规则表达");
            Assert.AreEqual(StaticKind.NoRevealWithLockCounter, e.StaticEffects[0].Kind, "静止种类＝锁定者禁现身");
            Assert.AreEqual("锁定标记,锁定标志", e.StaticEffects[0].Param, "标志名两写法都认");
        }

        public void TestBatch_JC122锁定暗藏者不能现身_来源离场后放行()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            string area = Areas(game)[0];

            var hawk = OnField(game, Load("JC122_hakeyadangsi"), 1, area);   // 来源在场（登记静止规则）
            var covert = game.CardManager.CreateCardInstance(Probe("TS_rb_covert"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            Assert.IsFalse(EffectEngine.Instance.IsRevealBlocked(covert), "无锁定标记 → 不禁止现身");

            game.CardManager.AddCounter(covert, "锁定标记");
            Assert.IsTrue(EffectEngine.Instance.IsRevealBlocked(covert), "有锁定标记 + 来源在场 → 禁止现身");

            // 生产路径：现身唯一入口被拒（持有优先权也照样拒绝）
            game.TurnManager.PriorityPlayerId = 2;
            Assert.IsFalse(game.TurnManager.RevealCovert(2, covert.UID), "生产路径：带锁定标记的暗藏者被拒绝现身");
            Assert.IsTrue(covert.IsCovert, "仍为暗藏者（未翻面）");

            // 来源离场 → 登记注销 → 放行（潜台词：规则是"只要在场"）
            game.CardManager.MoveCardToGraveyard(hawk);
            Assert.IsFalse(EffectEngine.Instance.IsRevealBlocked(covert), "来源离场 → 不再禁止现身");
        }
    }
}
