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
    /// 【P3·"门禁 + 分支"双条件（`EffectCondition.BranchPredicate`）+ JC031 唤灵师】
    /// 卡文：触发：每当另一个本方角色死亡时，你可以令目标玩家弃一张手牌。
    ///      若死亡的是吸血鬼角色，则改为可以令目标玩家随机弃一张手牌。该能力每回合只能发动一次。
    ///
    /// 交付的机制：
    ///  · `EffectCondition.BranchPredicate`：**触发门禁**（`Predicate`，收集与结算都按它过）满足后，
    ///    再按**第二条**条件在 `Actions` / `ElseBranch` 之间二选一（卡文"若 X 是 Y，则**改为**…"）；
    ///    缺省为空时行为与既有实现完全一致；
    ///  · 新谓词 `DiedVampireCharacter`：死亡的卡是**吸血鬼角色**（当前实际子类别含「吸血鬼」，
    ///    与 `TargetRestriction.CurrentSubCategory` 同口径，**含运行期授予**，如 JC030 授予的吸血鬼）；
    ///  · JC031 组合：`OnCardDied` + 门禁 `DiedAllyCharacter`（"另一个本方角色"）+ 分支 `DiedVampireCharacter`
    ///    + K24 玩家目标（触发路径独立窗口）+ `Discard{1,"目标玩家",随机/自选}` + `MaxUsesPerTurn=1`。
    /// </summary>
    public class P3DeathBranchTests : TestRunner
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

        /// <summary>探针卡（可选子类别）——`subCategories` 为空即"普通人类角色"。</summary>
        private static PlayerCardData Probe(string uid, params string[] subCategories)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            foreach (var s in subCategories) tmpl.SubCategories.Add(s);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static PlayerCardData ProbeAttachment(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "附属_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Attachment,
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

        /// <summary>触发排序窗口桩：全部按默认顺序入堆叠。</summary>
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

        /// <summary>UI 桩：记录"目标玩家选择"与"弃牌选择"两类回调（用于区分随机/自选两条分支）。</summary>
        private sealed class DiscardStub : Script.Interfaces.IGameUIHooks
        {
            public int PlayerTargetAnswer = 2;
            public int PlayerTargetCalls;
            public List<int> LastPlayerCandidates = new List<int>();
            public int DiscardCalls;
            public int LastDiscarder = -1;
            public int LastChooser = -1;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked)
            {
                DiscardCalls++;
                LastDiscarder = discarderId;
                LastChooser = chooserPlayerId;
                onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
            {
                PlayerTargetCalls++;
                LastPlayerCandidates = new List<int>(candidates ?? new List<int>());
                onChosen?.Invoke(PlayerTargetAnswer);
            }
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>布置：场上本方 JC031 + 一个待死亡的本方角色；对手有手牌。</summary>
        private static (CardInstance summoner, CardInstance victim, string area) Setup(GameInitializer game,
            params string[] victimSubCategories)
        {
            string area = Areas(game)[0];
            var summoner = game.CardManager.CreateCardInstance(Load("JC031_huanlingshi"), 1);
            game.ZoneManager.MoveToArea(summoner, area, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_db_victim", victimSubCategories), 1);
            game.ZoneManager.MoveToArea(victim, area, 1);
            // 对手放一张场上卡，保证有"对手玩家"可作目标
            var foe = game.CardManager.CreateCardInstance(Probe("TS_db_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            return (summoner, victim, area);
        }

        // ==================== ① 结构 ====================

        public void TestDeathBranch_JC031结构()
        {
            var tmpl = Load("JC031_huanlingshi");
            Assert.IsNotNull(tmpl, "JC031 模板应可加载");
            var e = FindEffect(tmpl, "JC031_1_DiscardOnAllyDeath");
            Assert.IsNotNull(e, "JC031 应有『本方角色死亡 → 令目标玩家弃牌』触发");
            Assert.AreEqual(EffectTiming.OnCardDied, e.Timing, "时机＝死亡触发");
            Assert.AreEqual(1, e.MaxUsesPerTurn, "『每回合只能发动一次』→ 次数上限 1");
            Assert.IsFalse(e.Forced, "『你可以』→ 非强制");
            Assert.AreEqual("DiedAllyCharacter", e.Condition.Predicate, "门禁＝另一个本方角色死亡");
            Assert.AreEqual("DiedVampireCharacter", e.Condition.BranchPredicate,
                "分支＝死亡的是吸血鬼角色（新的门禁+分支双条件）");
            Assert.AreEqual(1, e.Condition.ElseBranch.Count, "ElseBranch 有 1 个动作");
            Assert.AreEqual(ActionVerb.Discard, e.Condition.ElseBranch[0].Verb, "ElseBranch＝弃牌");
            Assert.AreEqual("自选", e.Condition.ElseBranch[0].Args[2], "非吸血鬼 → 由该玩家自选");
            Assert.AreEqual(TargetKind.Player, e.Target.Kind, "目标大类＝玩家（K24）");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.Enemy), "『目标玩家』＝对手");
            Assert.AreEqual(ActionVerb.Discard, e.Actions[0].Verb, "主分支＝弃牌");
            Assert.AreEqual("随机", e.Actions[0].Args[2], "吸血鬼 → 随机弃");
            Assert.AreEqual("目标玩家", e.Actions[0].Args[1], "弃牌者＝目标玩家");
        }

        // ==================== ② 谓词：吸血鬼判据（含运行期授予） ====================

        public void TestDeathBranch_吸血鬼判据()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var src = game.CardManager.CreateCardInstance(Load("JC031_huanlingshi"), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            // 模板固有吸血鬼
            var printed = game.CardManager.CreateCardInstance(Probe("TS_db_printed", "吸血鬼"), 1);
            game.ZoneManager.MoveToArea(printed, area, 1);
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("DiedVampireCharacter", src, printed, printed, -1, area),
                "模板子类别含吸血鬼 → 满足");

            // 运行期授予吸血鬼（JC030 同款路径）
            var granted = game.CardManager.CreateCardInstance(Probe("TS_db_granted"), 1);
            game.ZoneManager.MoveToArea(granted, area, 1);
            granted.Grant("SubCategory", "吸血鬼", 1);
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("DiedVampireCharacter", src, granted, granted, -1, area),
                "运行期授予的吸血鬼也满足（与 CurrentSubCategory 同口径）");

            // 非吸血鬼角色
            var plain = game.CardManager.CreateCardInstance(Probe("TS_db_plain"), 1);
            game.ZoneManager.MoveToArea(plain, area, 1);
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("DiedVampireCharacter", src, plain, plain, -1, area),
                "普通角色 → 不满足");

            // 非角色（附属）
            var att = game.CardManager.CreateCardInstance(ProbeAttachment("TS_db_att"), 1);
            game.ZoneManager.MoveToArea(att, area, 1);
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("DiedVampireCharacter", src, att, att, -1, area),
                "非角色卡 → 不满足");
        }

        // ==================== ③ 端到端：非吸血鬼死亡 → ElseBranch（该玩家自选） ====================

        public void TestDeathBranch_非吸血鬼死亡走自选()
        {
            var game = NewGame();
            var (_, victim, _) = Setup(game);
            var stub = new DiscardStub { PlayerTargetAnswer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            int hand2Before = game.TurnManager.GetHandOf(2).Count;
            game.CardManager.ResolveDeath(victim, CardManager.DeathCause.Destroy);

            Assert.AreEqual(1, stub.PlayerTargetCalls, "触发前应弹一次『目标玩家』独立窗口");
            Assert.AreEqual(1, stub.LastPlayerCandidates.Count, "对手限制 → 1 个候选");
            Assert.AreEqual(2, stub.LastPlayerCandidates[0], "候选＝P2");
            ResolveStackOnce(game);

            Assert.AreEqual(1, stub.DiscardCalls, "非吸血鬼 → 走 ElseBranch（**自选**：询问弃牌者）");
            Assert.AreEqual(2, stub.LastDiscarder, "弃牌者＝目标玩家 P2");
            Assert.AreEqual(2, stub.LastChooser, "自选 → 选择者也是 P2 本人");
            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count, "P2 弃掉 1 张");
        }

        // ==================== ④ 端到端：吸血鬼死亡 → 主分支（随机弃） ====================

        public void TestDeathBranch_吸血鬼死亡走随机()
        {
            var game = NewGame();
            var (_, victim, _) = Setup(game, "吸血鬼");
            var stub = new DiscardStub { PlayerTargetAnswer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            int hand2Before = game.TurnManager.GetHandOf(2).Count;
            game.CardManager.ResolveDeath(victim, CardManager.DeathCause.Destroy);
            ResolveStackOnce(game);

            Assert.AreEqual(0, stub.DiscardCalls, "吸血鬼 → 走主分支（**随机**：不询问弃牌者）");
            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count, "P2 仍被随机弃掉 1 张");
        }

        // ==================== ⑤ 门禁：必须是"另一个**本方**角色" ====================

        /// <summary>唤灵师自己死亡 → 不触发（卡文"另一个"）。</summary>
        public void TestDeathBranch_来源自身死亡不触发()
        {
            var game = NewGame();
            var (summoner, _, _) = Setup(game);
            var stub = new DiscardStub { PlayerTargetAnswer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            int hand2Before = game.TurnManager.GetHandOf(2).Count;
            game.CardManager.ResolveDeath(summoner, CardManager.DeathCause.Destroy);

            Assert.AreEqual(0, stub.PlayerTargetCalls, "『另一个』→ 自身死亡不触发");
            Assert.AreEqual(hand2Before, game.TurnManager.GetHandOf(2).Count, "对手手牌不变");
        }

        /// <summary>**对手**角色死亡 → 不触发（门禁 DiedAllyCharacter 不满足）。</summary>
        public void TestDeathBranch_对手角色死亡不触发()
        {
            var game = NewGame();
            var (_, _, area) = Setup(game);
            var stub = new DiscardStub { PlayerTargetAnswer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            var enemyChar = game.CardManager.CreateCardInstance(Probe("TS_db_enemychar"), 2);
            game.ZoneManager.MoveToArea(enemyChar, area, 2);
            int hand2Before = game.TurnManager.GetHandOf(2).Count;

            game.CardManager.ResolveDeath(enemyChar, CardManager.DeathCause.Destroy);
            ResolveStackOnce(game);

            Assert.AreEqual(0, stub.PlayerTargetCalls, "本方之外的角色死亡 → 不触发");
            Assert.AreEqual(hand2Before, game.TurnManager.GetHandOf(2).Count, "对手手牌不变");
        }

        // ==================== ⑥ K23：每回合只能发动一次 ====================

        public void TestDeathBranch_每回合一次()
        {
            var game = NewGame();
            var (_, victim1, area) = Setup(game);
            var stub = new DiscardStub { PlayerTargetAnswer = 2 };
            EffectEngine.Instance.UIHooks = stub;

            int hand2Before = game.TurnManager.GetHandOf(2).Count;

            game.CardManager.ResolveDeath(victim1, CardManager.DeathCause.Destroy);
            ResolveStackOnce(game);
            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count, "第一次死亡：弃 1 张");

            // 同回合第二个本方角色死亡 → 触发被收集但结算时被次数上限拦下
            var victim2 = game.CardManager.CreateCardInstance(Probe("TS_db_victim2"), 1);
            game.ZoneManager.MoveToArea(victim2, area, 1);
            game.CardManager.ResolveDeath(victim2, CardManager.DeathCause.Destroy);
            ResolveStackOnce(game);

            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count,
                "『每回合只能发动一次』→ 第二次死亡不再弃牌");
        }

        // ==================== ⑦ 取消目标玩家 → 不入堆叠、不弃牌 ====================

        public void TestDeathBranch_取消目标玩家则不弃牌()
        {
            var game = NewGame();
            var (_, victim, _) = Setup(game);
            var stub = new DiscardStub { PlayerTargetAnswer = -1 };   // 取消
            EffectEngine.Instance.UIHooks = stub;

            int hand2Before = game.TurnManager.GetHandOf(2).Count;
            game.CardManager.ResolveDeath(victim, CardManager.DeathCause.Destroy);

            Assert.AreEqual(1, stub.PlayerTargetCalls, "弹过窗口");
            Assert.IsTrue(game.StackManager.IsEmpty, "取消 → 触发不入堆叠");
            Assert.AreEqual(hand2Before, game.TurnManager.GetHandOf(2).Count, "对手手牌不变");
        }
    }
}
