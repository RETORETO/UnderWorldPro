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
    /// 【JC124 尼古拉斯·勒梅】卡文：「**行动3：在目标角色上放置一个哲人石标志。**
    /// **强制触发：当具有哲人石标志的角色死亡时，将其从墓地移回场上，并由其拥有者操控。
    /// 该能力在尼古拉斯·勒梅离场后仍然生效。**」
    ///
    /// 用户 2026-09 口径（本轮答复）：**"JC124 需要制作一个有效果的指示物，或者说一个全局触发效果，
    /// 只要 JC124 一开始加入玩家卡组就能触发这个效果"**（"两种方法都可以只要能以合理形式满足 JC124 效果
    /// 使用即可"）；并明确：**加入多个 JC124 也只能有一个效果生效**、**效果加入堆叠用 JC124 的卡图**、
    /// 角色**回到它死亡时所在的地区**、**按玩家各自登记**。
    ///
    /// 落地（构筑级／卡组级全局触发）：
    ///  · `EffectData.DeckLevelRegistration = true` 的声明 → `DeckManager` 装载卡组时
    ///    `DeckLevelTriggerRule.RegisterFromDeck` **按玩家登记一次**（多张同名只生效一份）；
    ///  · 登记与"那张牌此后身处何处"无关 ⇒ **离场后仍生效**（刻意不走持续效果登记，避免被离场撤销）；
    ///  · 事件发生时由 `TriggerRegistry.DeckLevelCollector` 合成触发，**来源卡＝卡组里那张 JC124 实例**
    ///    ⇒ 堆叠条目显示 JC124 的卡图；收集**跳过**常规场上扫描（否则在场时重复收集）；
    ///  · 条件谓词 `DiedWithCounter`（`Param=哲人石标志`）读**死亡瞬间的计数器快照**
  ///    （`CardInstance.CountersAtDeath`，`CardManager.ResolveDeath` 在广播前抓取）——
    ///    死亡触发的**结算**晚于"进墓地"，而进墓地会清空计数器；
    ///  · 动作 `MoveToZone{"目标","争夺区","正面","拥有者"}`：移回**死亡时所在地区**（条目携带"触发时所在地区"），
    ///    并把控制者交回**拥有者**（探针卡可直接构造"控制者≠拥有者"的场合来验证）。
    /// </summary>
    public class P4PhilosopherStoneTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1240 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>把 JC124 放进某玩家卡组并**走生产登记路径**（`DeckLevelTriggerRule.RegisterFromDeck`）。</summary>
        private static CardInstance RegisterJc124InDeck(GameInitializer game, int playerId, int copies = 1)
        {
            var deck = new List<CardInstance>();
            CardInstance first = null;
            for (int i = 0; i < copies; i++)
            {
                var c = game.CardManager.CreateCardInstance(Load("JC124_nigulasileimei"), playerId);
                deck.Add(c);
                first ??= c;
            }
            DeckLevelTriggerRule.RegisterFromDeck(playerId, deck);
            return first;
        }

        private static CardInstance OnField(GameInitializer game, CardInstance card, int playerId, string area)
        {
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        // ==================== ① 结构 ====================

        public void TestJC124_结构()
        {
            var tmpl = Load("JC124_nigulasileimei");

            var place = FindEffect(tmpl, "JC124_1_PlacePhilosopherStone");
            Assert.IsNotNull(place, "JC124 应有『行动3：放置哲人石标志』效果");
            Assert.AreEqual(EffectTiming.CharacterActivated, place.Timing, "角色激活（行动3）");
            Assert.AreEqual(3, place.Cost, "费用 3（行动3）");
            Assert.AreEqual(ActionVerb.PlaceCounter, place.Actions[0].Verb, "动作＝放置标志");
            Assert.AreEqual("哲人石标志", place.Actions[0].Args[0], "标志名＝哲人石标志");
            Assert.AreEqual(TargetKind.Character, place.Target.Kind, "目标＝角色");

            var ret = FindEffect(tmpl, "JC124_2_ReturnPhilosopherStoneDeadToField");
            Assert.IsNotNull(ret, "JC124 应有『具哲人石标志的角色死亡 → 移回场上』触发");
            Assert.AreEqual(EffectTiming.OnCardDied, ret.Timing, "时机＝死亡触发");
            Assert.IsTrue(ret.Forced, "卡文『强制触发』→ 强制（不可放弃）");
            Assert.IsTrue(ret.DeckLevelRegistration, "**构筑级（卡组级）登记**：只要在开局卡组内即生效");
            Assert.AreEqual("DiedWithCounter", ret.Condition.Predicate, "条件＝死亡瞬间具有指定标志");
            Assert.AreEqual("哲人石标志", ret.Condition.Param, "标志＝哲人石标志");
            Assert.AreEqual(ActionVerb.MoveToZone, ret.Actions[0].Verb, "动作＝移动禁区");
            Assert.AreEqual("争夺区", ret.Actions[0].Args[1], "回场（争夺区）");
            Assert.AreEqual("拥有者", ret.Actions[0].Args[3], "回场后由其**拥有者**操控");
        }

        // ==================== ② 登记：只在卡组内即可 / 多张只生效一份 / 按玩家各自登记 ====================

        public void TestJC124_从卡组登记且多张只生效一份()
        {
            var game = NewGame();
            Assert.IsFalse(DeckLevelTriggerRule.IsRegistered(1), "前置：尚未登记");

            var first = RegisterJc124InDeck(game, 1, copies: 3);   // 三张同名

            Assert.IsTrue(DeckLevelTriggerRule.IsRegistered(1), "卡组内有 JC124 → 玩家1 已登记");
            Assert.AreEqual(first.UID, DeckLevelTriggerRule.SourceUidOf(1), "登记来源＝卡组里的**第一张**实例");
            Assert.AreEqual("JC124_2_ReturnPhilosopherStoneDeadToField", DeckLevelTriggerRule.EffectIdOf(1),
                "登记的效果＝构筑级死亡触发（**多张同名只有一个生效**）");
            Assert.IsFalse(DeckLevelTriggerRule.IsRegistered(2), "玩家2 卡组内没有 JC124 → 未登记（**按玩家各自登记**）");
        }

        public void TestJC124_不在卡组则不登记()
        {
            var game = NewGame();
            var deck = new List<CardInstance>
            {
                game.CardManager.CreateCardInstance(Probe("TS_ps_other"), 1),
            };
            DeckLevelTriggerRule.RegisterFromDeck(1, deck);
            Assert.IsFalse(DeckLevelTriggerRule.IsRegistered(1), "卡组内没有带构筑级声明的牌 → 不登记");
        }

        // ==================== ③ 端到端：具哲人石标志的角色死亡 → 从墓地移回其死亡时所在地区 ====================

        /// <summary>触发排序桩：全部触发直接入堆叠（无 UI 场景下的确定性兜底，与既有测试同款）。</summary>
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

        public void TestJC124_标志角色死亡后回场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim"), 1), 1, areas[1]);
            victim.AddCounter("哲人石标志", 1);

            game.CardManager.DestroyInstance(victim);          // 消灭（死亡触发时机）
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "前置：已进墓地");

            // 走触发排序窗口（桩：全部入堆叠）→ 双方让过结算
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "结算后**从墓地移回场上**");
            Assert.AreEqual(areas[1], victim.ZoneDetailId, "回场落点＝**它死亡时所在的地区**");
            Assert.AreEqual(1, victim.ControllerId, "由其**拥有者**操控（玩家1）");
        }

        public void TestJC124_死亡瞬间无标志则不回场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim2"), 1), 1, areas[0]);
            // 不放哲人石标志

            game.CardManager.DestroyInstance(victim);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "没有哲人石标志 → 触发条件不满足，留在墓地");
        }

        public void TestJC124_标志被移除后死亡不回场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim3"), 1), 1, areas[0]);
            victim.AddCounter("哲人石标志", 1);
            victim.RemoveCounter("哲人石标志", 1);      // 标志已被移除

            game.CardManager.DestroyInstance(victim);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "标志已移除 → 不回场（读的是死亡瞬间的快照）");
        }

        // ==================== ④ "离场后仍然生效"：JC124 从未在场/已被消灭都照常触发 ====================

        public void TestJC124_登记后不依赖本体在场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc124 = RegisterJc124InDeck(game, 1);

            // JC124 本体仍在牌库（从未在场）→ 触发照常
            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim4"), 1), 1, areas[0]);
            victim.AddCounter("哲人石标志", 1);
            game.CardManager.DestroyInstance(victim);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "JC124 不在场（在牌库）→ 触发照常生效（构筑级登记）");

            // 再把 JC124 本体放到场上并消灭 → 仍然生效（卡文"离场后仍然生效"）
            OnField(game, jc124, 1, areas[1]);
            game.CardManager.DestroyInstance(jc124);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(CardZone.Graveyard, jc124.CurrentZone, "前置：JC124 本体已进墓地");

            var victim2 = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim5"), 1), 1, areas[1]);
            victim2.AddCounter("哲人石标志", 1);
            game.CardManager.DestroyInstance(victim2);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(CardZone.AreaZone, victim2.CurrentZone, "JC124 本体已离场 → 触发**仍然生效**（卡文如此）");
            Assert.AreEqual(areas[1], victim2.ZoneDetailId, "落点＝该角色死亡时所在地区");
        }

        // ==================== ⑤ 卡文未限定"本方"：任意玩家的具标志角色死亡都回场（回场后归其拥有者） ====================

        /// <summary>卡文只写"**当具有哲人石标志的角色死亡时**…**并由其拥有者操控**"——**没有**写"本方/敌方"，
        /// 且行动3 的目标（`TargetKind.Character`）本身也**不限本方**（可以把标志放在对手角色上）；
        /// "由其拥有者操控"这句正是为跨玩家场合准备的。故本用例锁定：**对手的具标志角色同样回场**，
        /// 且回场后归**其拥有者**（仍是玩家2）。</summary>
        public void TestJC124_任意玩家的标志角色死亡都回场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);   // 只有玩家1 登记

            var enemy = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_enemy"), 2), 2, areas[0]);
            enemy.AddCounter("哲人石标志", 1);
            game.CardManager.DestroyInstance(enemy);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, enemy.CurrentZone, "对手的具标志角色死亡同样被移回场上（卡文未限定本方）");
            Assert.AreEqual(areas[0], enemy.ZoneDetailId, "回到它死亡时所在的地区");
            Assert.AreEqual(2, enemy.ControllerId, "回场后由其**拥有者**（玩家2）操控");
        }

        /// <summary>双方都登记（镜像对局）：同一次死亡只回场一次，且不会因两个登记而错乱。</summary>
        public void TestJC124_双方都登记时单次回场()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);
            RegisterJc124InDeck(game, 2);
            Assert.IsTrue(DeckLevelTriggerRule.IsRegistered(1) && DeckLevelTriggerRule.IsRegistered(2),
                "前置：双方各自登记（按玩家各自登记）");

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_both"), 1), 1, areas[1]);
            victim.AddCounter("哲人石标志", 1);
            game.CardManager.DestroyInstance(victim);
            for (int i = 0; i < 10 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "已回场（第二份登记是幂等的空操作）");
            Assert.AreEqual(areas[1], victim.ZoneDetailId, "落点仍是死亡时所在地区");
            Assert.AreEqual(1, victim.ControllerId, "仍由其拥有者操控");
        }

        // ==================== ⑥ 回场后由拥有者操控（控制者≠拥有者的场合） ====================

        public void TestJC124_回场交回拥有者操控()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            RegisterJc124InDeck(game, 1);

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim6"), 1), 1, areas[0]);
            victim.AddCounter("哲人石标志", 1);
            // 模拟"被夺控"（如 JC036 初拥）：拥有者＝玩家1，控制者＝玩家2
            game.ZoneManager.ChangeController(victim, 2, bumpVersion: false);
            Assert.AreEqual(2, victim.ControllerId, "前置：控制者已变为玩家2");
            Assert.AreEqual(1, victim.OwnerId, "前置：拥有者仍是玩家1");

            game.CardManager.DestroyInstance(victim);
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "已回场");
            Assert.AreEqual(1, victim.ControllerId, "回场后**由其拥有者（玩家1）操控**");
        }

        // ==================== ⑦ 堆叠条目用 JC124 的卡图（来源＝登记的 JC124 实例） ====================

        public void TestJC124_堆叠来源为JC124卡图()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc124 = RegisterJc124InDeck(game, 1);

            var victim = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_victim7"), 1), 1, areas[0]);
            victim.AddCounter("哲人石标志", 1);
            game.CardManager.DestroyInstance(victim);

            // 触发已入堆叠（桩：全部入堆叠），未结算前检查来源卡
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠（等待让过结算）");
            bool found = false;
            foreach (var e in game.StackManager.Entries)
            {
                if (e == null || e.EffectDataEffect == null) continue;
                if (e.EffectDataEffect.EffectId != "JC124_2_ReturnPhilosopherStoneDeadToField") continue;
                found = true;
                Assert.AreEqual(jc124.UID, e.SourceCardUid, "堆叠条目来源＝**登记的 JC124 实例**（⇒ 用 JC124 的卡图）");
                Assert.AreEqual("JC124_nigulasileimei", game.CardManager.GetInstance(e.SourceCardUid)?.PlayerTemplate?.UID,
                    "来源卡模板＝JC124");
                Assert.AreEqual(jc124.UID, e.StackEffect?.Source?.UID, "触发实例的来源卡同为该 JC124");
            }
            Assert.IsTrue(found, "堆叠上应能找到 JC124 的构筑级触发条目");
        }

        // ==================== ⑧ 行动3：在目标角色上放置哲人石标志 ====================

        public void TestJC124_行动3放置哲人石标志()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc124 = OnField(game, game.CardManager.CreateCardInstance(Load("JC124_nigulasileimei"), 1), 1, areas[0]);
            var ally = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_target"), 1), 1, areas[0]);

            var effect = FindEffect(jc124.PlayerTemplate, "JC124_1_PlacePhilosopherStone");
            game.EffectEngine.ResolveEffect(effect, jc124, ally, areas[0]);

            Assert.IsTrue(ally.Counters.TryGetValue("哲人石标志", out var n) && n == 1,
                "目标角色上已放置哲人石标志");
        }

        // ==================== ⑨ 死亡瞬间快照本身（谓词判据） ====================

        public void TestJC124_死亡快照读取()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var card = OnField(game, game.CardManager.CreateCardInstance(Probe("TS_ps_snap"), 1), 1, areas[0]);
            card.AddCounter("哲人石标志", 2);

            game.CardManager.DestroyInstance(card);   // 死亡 → 快照 + 进墓地（计数器被清空）

            Assert.IsFalse(card.Counters.ContainsKey("哲人石标志"), "前置：进墓地后当前计数器已清空（ResetToBaseState）");
            Assert.IsTrue(card.HadCounterAtDeath("哲人石标志"), "但**死亡瞬间的快照**仍记录着哲人石标志");
            Assert.IsFalse(card.HadCounterAtDeath("时间标志"), "快照只记录死亡时真实存在的标志");
        }
    }
}
