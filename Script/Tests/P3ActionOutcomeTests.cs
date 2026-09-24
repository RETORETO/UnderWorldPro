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
    /// 【P3·"若你如此做"的动作成果判定（`_actionChainAborted` 泛化）→ JC009 远东六号科研所 · JC087 格鲁尼的走私客】
    ///
    /// 语义：动作序列中某动作**未真正生效**时，置链式中止标志 → **后续动作整段跳过**
    /// （与既有"支付失败即中止"共用同一标志；`Pay`/`PayOptional` 的"若你如此做"同源）。
    /// 生效判据：
    ///  · `Seal` 未落地（宿主为空/封印自身/`SealCard` 返回 false）；
    ///  · `Latent` 未生效（目标为空/不在场上/已是暗藏者）；
    ///  · `Uncover` 未生效（目标为空/不在场上/非暗藏）。
    /// 覆盖：JC009「封印 → 然后遏制1」（FAQ：未能成功封印则遏制不结算）、
    ///       JC087「令目标潜伏，若你如此做，在本地区放置势力标志」。
    /// </summary>
    public class P3ActionOutcomeTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            // 默认触发窗桩＝"放弃全部触发"：本套件多数用例是**直接**调 `ResolveEffect` 验证动作链，
            // 若让进场触发自动入栈会与手工结算重复执行（JC009/JC087 都带进场触发）。
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

        private static CardInstance MakeCouncil(GameInitializer game, int playerId)
        {
            var councilTmpl = Godot.ResourceLoader.Load<CouncilCardData>(CouncilDir + "MSJC001_weimushouwang.tres");
            var inst = game.CardManager.CreateCardInstance(councilTmpl, playerId);
            game.ZoneManager.MoveToCouncil(inst, playerId);
            return inst;
        }

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        /// <summary>场上某玩家在某地区的势力标志数。</summary>
        private static int MarksOf(GameInitializer game, string area, int playerId)
            => game.AreaManager.GetPowerMark(area, playerId);

        // ==================== ① 结构 ====================

        public void TestOutcome_JC009结构()
        {
            var tmpl = Load("JC009_yuandongliuhaokeyansuo");
            Assert.IsNotNull(tmpl, "JC009 模板应可加载");
            var e = FindEffect(tmpl, "JC009_1_SealTargetThenCurb");
            Assert.IsNotNull(e, "JC009 应有『封印目标角色 → 然后遏制1』");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.AllAreas, "显式打开全场候选（本卡结附秘社、没有本地区）");
            Assert.AreEqual(2, e.Actions.Count, "两个动作：封印 → 遏制");
            Assert.AreEqual(ActionVerb.Seal, e.Actions[0].Verb, "① 封印");
            Assert.AreEqual("目标", e.Actions[0].Args[0], "被封印者＝目标角色");
            Assert.AreEqual("自身", e.Actions[0].Args[1], "宿主＝本卡");
            Assert.AreEqual(ActionVerb.Curb, e.Actions[1].Verb, "② 遏制");
            Assert.AreEqual("1", e.Actions[1].Args[0], "遏制 1");
        }

        public void TestOutcome_JC087结构()
        {
            var tmpl = Load("JC087_gelunidezousike");
            Assert.IsNotNull(tmpl, "JC087 模板应可加载");
            var e = FindEffect(tmpl, "JC087_1_LatentAllyThenPlaceMark");
            Assert.IsNotNull(e, "JC087 应有『潜伏目标 → 若你如此做放标志』");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进入一个地区（进场触发）");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 非强制");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.IsLocalZone, "限定本地区");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.Friendly), "本方角色");
            Assert.AreEqual(ActionVerb.Latent, e.Actions[0].Verb, "① 潜伏目标");
            Assert.AreEqual(ActionVerb.PlaceToken, e.Actions[1].Verb, "② 放势力标志");
        }

        // ==================== ② JC009：封印成功 → 遏制结算 ====================

        public void TestOutcome_JC009封印成功则遏制()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var council = MakeCouncil(game, 1);
            var lab = game.CardManager.CreateCardInstance(Load("JC009_yuandongliuhaokeyansuo"), 1);

            // 对手在该地区有 2 个势力标志，且场上有可封印的角色
            game.AreaManager.AddPowerMark(area, 2, 2);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ao_victim"), 1);
            game.ZoneManager.MoveToArea(victim, area, 1);

            Attach(game, council, lab);
            var e = FindEffect(lab.PlayerTemplate, "JC009_1_SealTargetThenCurb");
            EffectEngine.Instance.ResolveEffect(e, lab, victim, area);

            Assert.AreEqual(CardZone.SealedZone, victim.CurrentZone, "① 目标角色被封印在本卡上");
            // `SealedBy` 挂在**被封印者**身上（封印卡 → 宿主），宿主侧经 `SealedCards` 反向记录。
            Assert.IsTrue(ReferenceEquals(lab, victim.SealedBy), "封印宿主＝远东六号科研所");
            Assert.IsTrue(lab.SealedCards.Contains(victim), "本卡记录被封的角色");
            Assert.IsTrue(MarksOf(game, area, 2) < 2, "② 封印成功 → 遏制1 已结算（对手标志减少）");
        }

        // ==================== ③ JC009：封印失败 → 遏制整段跳过（FAQ 第二条） ====================

        public void TestOutcome_JC009封印失败则不遏制()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var council = MakeCouncil(game, 1);
            var lab = game.CardManager.CreateCardInstance(Load("JC009_yuandongliuhaokeyansuo"), 1);
            game.AreaManager.AddPowerMark(area, 2, 2);
            Attach(game, council, lab);

            var e = FindEffect(lab.PlayerTemplate, "JC009_1_SealTargetThenCurb");

            // 封印失败场景：被封印者＝宿主自身（`SealAndReport` 判定"不能封印在自己身上"）
            EffectEngine.Instance.ResolveEffect(e, lab, lab, area);

            Assert.IsFalse(lab.IsSeal, "前置：未发生封印");
            Assert.AreEqual(2, MarksOf(game, area, 2), "**封印失败 → 遏制1 不结算**（对手标志不变）");
        }

        // ==================== ④ JC087：潜伏成功 → 放标志；未生效 → 不放 ====================

        public void TestOutcome_JC087潜伏成功则放标志()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var smuggler = game.CardManager.CreateCardInstance(Load("JC087_gelunidezousike"), 1);
            game.ZoneManager.MoveToArea(smuggler, area, 1);
            var ally = game.CardManager.CreateCardInstance(Probe("TS_ao_ally"), 1);
            game.ZoneManager.MoveToArea(ally, area, 1);
            int before = MarksOf(game, area, 1);

            var e = FindEffect(smuggler.PlayerTemplate, "JC087_1_LatentAllyThenPlaceMark");
            EffectEngine.Instance.ResolveEffect(e, smuggler, ally, area);

            Assert.IsTrue(ally.IsCovert, "① 目标本方角色已潜伏");
            Assert.AreEqual(before + 1, MarksOf(game, area, 1), "② 潜伏生效 → 在本地区放置 1 个本方势力标志");
        }

        /// <summary>目标已是暗藏者 → 潜伏不生效 → **不放**标志（"若你如此做"不成立）。</summary>
        public void TestOutcome_JC087潜伏未生效则不放标志()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var smuggler = game.CardManager.CreateCardInstance(Load("JC087_gelunidezousike"), 1);
            game.ZoneManager.MoveToArea(smuggler, area, 1);
            var ally = game.CardManager.CreateCardInstance(Probe("TS_ao_ally2"), 1);
            game.ZoneManager.MoveToArea(ally, area, 1);
            ally.SetCovert(true);                          // 已经是暗藏者
            int before = MarksOf(game, area, 1);

            var e = FindEffect(smuggler.PlayerTemplate, "JC087_1_LatentAllyThenPlaceMark");
            EffectEngine.Instance.ResolveEffect(e, smuggler, ally, area);

            Assert.IsTrue(ally.IsCovert, "前置/结果：仍是暗藏者");
            Assert.AreEqual(before, MarksOf(game, area, 1), "潜伏未生效 → **不放**势力标志");
        }

        /// <summary>未选目标（触发窗取消）→ 潜伏未生效 → 不放标志。</summary>
        public void TestOutcome_JC087无目标则不放标志()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var smuggler = game.CardManager.CreateCardInstance(Load("JC087_gelunidezousike"), 1);
            game.ZoneManager.MoveToArea(smuggler, area, 1);
            int before = MarksOf(game, area, 1);

            var e = FindEffect(smuggler.PlayerTemplate, "JC087_1_LatentAllyThenPlaceMark");
            EffectEngine.Instance.ResolveEffect(e, smuggler, null, area);   // 目标为空

            Assert.AreEqual(before, MarksOf(game, area, 1), "无目标 → 潜伏未生效 → 不放标志");
        }

        // ==================== ⑤ 端到端：JC087 进场触发（进入一个地区） ====================

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

        /// <summary>触发窗桩：放弃全部触发（不入堆叠）——用于"手工结算动作链"的用例。</summary>
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

        public void TestOutcome_JC087进场触发端到端()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            // 场上先放一个本方角色（作为潜伏目标候选）
            var ally = game.CardManager.CreateCardInstance(Probe("TS_ao_ally3"), 1);
            game.ZoneManager.MoveToArea(ally, area, 1);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());   // 覆盖默认桩：本用例要让触发自动入栈
            int before = MarksOf(game, area, 1);

            // 走私客从手牌进入该地区 → EnterTrigger
            var smuggler = game.CardManager.CreateCardInstance(Load("JC087_gelunidezousike"), 1);
            game.ZoneManager.MoveToHand(smuggler, 1);
            game.ZoneManager.MoveToArea(smuggler, area, 1);

            Assert.IsFalse(game.StackManager.IsEmpty, "进入地区 → 触发入堆叠");
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(ally.IsCovert, "目标本方角色被潜伏（触发取目标桩自动选第一个候选）");
            Assert.AreEqual(before + 1, MarksOf(game, area, 1), "并在该地区放置了 1 个本方势力标志");
        }
    }
}
