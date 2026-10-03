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
    /// 【P3·K2 扩展："目标卡上"的整组消灭（`ActionVerb.DestroyOnTarget`）→ JC013 貘 · JC017 除咒师】
    ///
    /// K2 群体动作此前只支持"地区内群体"（`MassMode.AllInArea`），无法表达"**某张卡上**的集合"：
    ///  · JC017「消灭目标角色上的所有**敌方附属**」＝ 宿主卡的 `AttachedCards` + 阵营筛选；
    ///  · JC013「消灭该角色或附属上**封印的所有牌**」＝ 宿主卡的 `SealedCards`。
    /// 新动词参数：`Args[0]`＝集合来源（`附属`/`封印牌`），`Args[1]`＝归属（`任意`/`本方`/`对手`），
    /// 对象＝宿主卡（`target`）。集合为空**不**置链式中止（卡文后续动作不以消灭成功为前提）。
    /// </summary>
    public class P3DestroyOnTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());   // 手工结算用例：不让进场触发自动入栈
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>把附属结附到宿主（生产路径等价：双向绑定 + 附属区落地 + 持续登记）。</summary>
        private static CardInstance Attach(GameInitializer game, CardInstance host, int ownerId)
        {
            var att = game.CardManager.CreateCardInstance(Probe($"TS_dot_att_{host.UID}_{ownerId}", CardCategory.Attachment), ownerId);
            att.IsAttached = true;
            att.AttachedTo = host;
            host.AttachedCards.Add(att);
            game.ZoneManager.MoveToAttachedZone(att, host.ZoneDetailId ?? Areas(game)[0], ownerId);
            return att;
        }

        /// <summary>把一张牌封印到宿主上。</summary>
        private static CardInstance SealOnto(GameInitializer game, CardInstance host, int ownerId)
        {
            var card = game.CardManager.CreateCardInstance(Probe($"TS_dot_seal_{host.UID}_{ownerId}"), ownerId);
            game.ZoneManager.MoveToArea(card, host.ZoneDetailId ?? Areas(game)[0], ownerId);
            game.TurnManager.SealCard(card, host, false);
            return card;
        }

        // ==================== ③ JC017 可指定**其他地区**的角色（2026-09 修复） ====================

        /// <summary>
        /// 【2026-09 修复·用户实测"JC017 效果可以指定其他地区的角色，现在不能指定"】
        /// 卡文"消灭**目标角色**上的所有敌方附属"**没有"本地区"限定** ⇒ 候选＝全场角色。
        /// 触发路径默认只枚举"来源卡所在地区"，必须显式 `AllAreas = true`；
        /// 本用例从**真实触发链路**取"服务端下发的候选"，断言另一地区的角色在候选里。
        /// </summary>
        public void TestDestroyOnTarget_JC017可指定其他地区的角色()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            var view = new CapturingTriggerView();
            game.TurnManager.SetTriggerView(view);

            // 另一地区（地区2）放一个对手角色
            var far = game.CardManager.CreateCardInstance(Probe("TS_dot_far"), 2);
            game.ZoneManager.MoveToArea(far, areas[1], 2);

            // JC017 进入地区1（进场触发收集 → 取目标窗口）
            var caster = game.CardManager.CreateCardInstance(Load("JC017_chuzhoushi"), 1);
            game.ZoneManager.MoveToArea(caster, areas[0], 1);

            Assert.IsTrue(view.SelectionCalls > 0, "前置：JC017 进场触发进入取目标窗口");
            Assert.IsTrue(view.LastValidUids.Contains(far.UID),
                $"候选必须包含**另一地区**的角色（AllAreas=true；实际候选 {view.LastValidUids.Count} 个）");
        }

        /// <summary>结构：JC017 的目标声明必须显式"不限本地区"。</summary>
        public void TestDestroyOnTarget_JC017目标不限本地区()
        {
            var tmpl = Load("JC017_chuzhoushi");
            var spec = FindEffect(tmpl, "JC017_1_DestroyEnemyAttachmentsThenBarrier")?.Target;
            Assert.IsNotNull(spec, "JC017 应声明目标");
            Assert.IsTrue(spec.AllAreas, "必须 `AllAreas = true`（卡文无『本地区』限定）");
            Assert.IsFalse(spec.IsLocalZone, "不得声明 `IsLocalZone`");
            Assert.IsTrue(spec.CanTargetSelf, "卡文未排除自身 ⇒ 可指定自身（A62）");
        }

        /// <summary>把触发**全部入堆叠**并记录"服务端下发的取目标候选"的触发视图（目标选择＝放弃）。</summary>
        private sealed class CapturingTriggerView : Script.Interfaces.ITriggerView
        {
            public int SelectionCalls;
            public List<string> LastValidUids = new List<string>();

            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);   // 全部入堆叠（不排序/不放弃）→ 才会走到"取目标"
                onDone?.Invoke();
            }

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger,
                List<string> validTargetUids, System.Action<string> onPicked)
            {
                SelectionCalls++;
                LastValidUids = validTargetUids != null ? new List<string>(validTargetUids) : new List<string>();
                onPicked?.Invoke(null);           // 放弃选择（本用例只看候选集合）
            }

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
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

        // ==================== ① 结构 ====================

        public void TestDestroyOnTarget_JC017结构()
        {
            var tmpl = Load("JC017_chuzhoushi");
            Assert.IsNotNull(tmpl, "JC017 模板应可加载");
            var e = FindEffect(tmpl, "JC017_1_DestroyEnemyAttachmentsThenBarrier");
            Assert.IsNotNull(e, "JC017 应有『消灭敌方附属 + 获得屏障』");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.AreEqual(2, e.Actions.Count, "两个动作");
            Assert.AreEqual(ActionVerb.DestroyOnTarget, e.Actions[0].Verb, "① 消灭宿主上的整组卡");
            Assert.AreEqual("附属", e.Actions[0].Args[0], "集合＝附属");
            Assert.AreEqual("对手", e.Actions[0].Args[1], "只消灭**敌方**附属");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[1].Verb, "② 获得屏障");
            Assert.AreEqual("屏障", e.Actions[1].Args[0], "授予屏障");
            Assert.AreEqual("目标", e.Actions[1].Args[2], "主体＝目标角色");
            Assert.AreEqual("直到回合结束", e.Duration, "屏障时效＝直到回合结束");
        }

        public void TestDestroyOnTarget_JC013结构()
        {
            var tmpl = Load("JC013_mo");
            Assert.IsNotNull(tmpl, "JC013 模板应可加载");
            var e = FindEffect(tmpl, "JC013_1_TapThenDestroySealed");
            Assert.IsNotNull(e, "JC013 应有『横置目标 → 消灭其上封印的牌』激活");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活");
            Assert.AreEqual("行动", e.ActivateWindow, "窗口＝行动");
            Assert.IsTrue(e.ActivateCondition.Contains("牺牲本角色"), "『牺牲貘』＝发动代价");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.AreEqual(TargetKind.Attachment, e.Target.KindAlt, "或种类＝附属（角色**或附属**）");
            Assert.AreEqual(ActionVerb.Tap, e.Actions[0].Verb, "① 横置目标");
            Assert.AreEqual(ActionVerb.DestroyOnTarget, e.Actions[1].Verb, "② 消灭其上封印的牌");
            Assert.AreEqual("封印牌", e.Actions[1].Args[0], "集合＝封印牌");
        }

        // ==================== ② JC017 端到端：只消灭敌方附属，本方附属留存；目标获得屏障 ====================

        public void TestDestroyOnTarget_JC017只消灭敌方附属()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var caster = game.CardManager.CreateCardInstance(Load("JC017_chuzhoushi"), 1);
            game.ZoneManager.MoveToArea(caster, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_dot_host"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            // 阵営基准＝来源卡控制者（P1）：**P2 控制的附属**才是"敌方附属"；P1 自己的附属留存。
            var enemyAtt1 = Attach(game, host, 2);
            var enemyAtt2 = Attach(game, host, 2);
            var ownAtt1 = Attach(game, host, 1);
            var ownAtt2 = Attach(game, host, 1);

            var e = FindEffect(caster.PlayerTemplate, "JC017_1_DestroyEnemyAttachmentsThenBarrier");
            EffectEngine.Instance.ResolveEffect(e, caster, host, area);

            Assert.AreEqual(CardZone.Graveyard, enemyAtt1.CurrentZone, "敌方附属 1 被消灭");
            Assert.AreEqual(CardZone.Graveyard, enemyAtt2.CurrentZone, "敌方附属 2 被消灭");
            Assert.AreEqual(CardZone.AttachedZone, ownAtt1.CurrentZone, "本方附属 1 留存");
            Assert.AreEqual(CardZone.AttachedZone, ownAtt2.CurrentZone, "本方附属 2 留存");
            Assert.IsFalse(host.AttachedCards.Contains(enemyAtt1), "宿主列表已清除被消灭的附属");
            Assert.IsTrue(host.AttachedCards.Contains(ownAtt1), "宿主列表保留本方附属");

            // 注：`Gain` 动词授予"屏障"走 `SetGuard`（`HasGuard`），不是关键字通道。
            Assert.IsTrue(host.HasGuard, "该角色获得屏障（直到回合结束）");
            Assert.IsTrue(TargetValidity.IsBarrierBlocking(host, 1), "屏障生效：敌方来源不能指定该角色");
            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.IsFalse(host.HasGuard, "回合结束 → 屏障到期撤销");
        }

        // ==================== ③ JC013 端到端：横置目标 + 消灭其上封印的所有牌 ====================

        public void TestDestroyOnTarget_JC013消灭封印牌()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var mo = game.CardManager.CreateCardInstance(Load("JC013_mo"), 1);
            game.ZoneManager.MoveToArea(mo, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_dot_host2"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var sealed1 = SealOnto(game, host, 2);
            var sealed2 = SealOnto(game, host, 1);
            Assert.AreEqual(2, host.SealedCards.Count, "前置：宿主上有 2 张封印牌");

            var e = FindEffect(mo.PlayerTemplate, "JC013_1_TapThenDestroySealed");
            EffectEngine.Instance.ResolveEffect(e, mo, host, area);

            Assert.IsTrue(host.IsTapped, "① 目标被横置");
            Assert.AreEqual(CardZone.Graveyard, sealed1.CurrentZone, "② 封印牌 1 被消灭");
            Assert.AreEqual(CardZone.Graveyard, sealed2.CurrentZone, "② 封印牌 2 被消灭");
            Assert.AreEqual(0, host.SealedCards.Count, "宿主封印列表已清空");
        }

        /// <summary>集合为空 → 什么都不做，且**不**阻断后续动作（JC017 的屏障照常授予）。</summary>
        public void TestDestroyOnTarget_集合为空不影响后续动作()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var caster = game.CardManager.CreateCardInstance(Load("JC017_chuzhoushi"), 1);
            game.ZoneManager.MoveToArea(caster, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_dot_host3"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);   // 无任何附属

            var e = FindEffect(caster.PlayerTemplate, "JC017_1_DestroyEnemyAttachmentsThenBarrier");
            EffectEngine.Instance.ResolveEffect(e, caster, host, area);

            Assert.IsTrue(host.HasGuard, "没有敌方附属可消灭时，**屏障仍照常授予**（卡文无『若你如此做』）");
        }

        /// <summary>宿主为空 → 动词安全跳过。</summary>
        public void TestDestroyOnTarget_无宿主安全跳过()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var caster = game.CardManager.CreateCardInstance(Load("JC017_chuzhoushi"), 1);
            game.ZoneManager.MoveToArea(caster, area, 1);

            var data = new EffectData
            {
                EffectId = "TS_dot_nohost",
                Timing = EffectTiming.OnSorceryCast,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DestroyOnTarget, Args = new Array<string> { "附属", "任意" } },
                },
                Duration = "瞬时",
            };
            // 直接以 source 自身为宿主（无附属）→ 不抛异常、无副作用
            EffectEngine.Instance.ResolveEffect(data, caster, null, area);
            Assert.AreEqual(CardZone.AreaZone, caster.CurrentZone, "来源卡未被误动");
        }

        // ==================== ④ 激活路径：牺牲自身为代价 + 目标选择 ====================

        public void TestDestroyOnTarget_JC013激活牺牲自身()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var mo = game.CardManager.CreateCardInstance(Load("JC013_mo"), 1);
            game.ZoneManager.MoveToArea(mo, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_dot_host4"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);
            var sealedCard = SealOnto(game, host, 2);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = FindEffect(mo.PlayerTemplate, "JC013_1_TapThenDestroySealed");
            EffectEngine.Instance.ActivateAbility(mo, e, host);   // 取目标＝宿主

            Assert.AreEqual(CardZone.Graveyard, mo.CurrentZone, "代价：**牺牲貘**（发动即进墓地）");
            Assert.IsFalse(game.StackManager.IsEmpty, "效果已入堆叠");

            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(host.IsTapped, "结算后目标被横置");
            Assert.AreEqual(CardZone.Graveyard, sealedCard.CurrentZone, "其上封印的牌被消灭");
        }
    }
}
