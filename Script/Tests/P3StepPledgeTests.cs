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
    /// 【P3·步骤级承诺（"在下一个准备步骤开始时…"）→ JC024 S．P．T董事局②】
    /// 卡文：快速行动2：将S．P．T董事局潜伏。在下一个准备步骤开始时，令其现身，且不需要支付其费用。
    ///
    /// 复用上一轮的**一次性待触发承诺**机制（`EffectData.Pending`），本轮补上"**无归属玩家的时机**"：
    /// 准备步骤/结束步骤是**双方共有步骤**（`OnTurnStarted` 的 `TriggeringPlayerId = -1`），
    /// 因此这类时机下"登记过的承诺一律在该事件出现时兑现"，不再按玩家过滤。
    /// 兑现动作 `Uncover{自身}` 直连 `CardManager.RevealCard`（不经过打出/翻面付费通道）→ 即卡文"不需要支付其费用"。
    /// </summary>
    public class P3StepPledgeTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            // 触发窗桩必须在**任何卡进场之前**设置：JC024 现在带『进场触发』（授予机动），`_view` 为空会 NRE。
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
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
                var tmpl = Probe($"TS_sp_asset_{playerId}_{i}", CardCategory.Attachment);
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

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>把回合推进到**下一个准备步骤**（靠双方连续让过推进步骤机）。</summary>
        private static void AdvanceToNextPrepare(GameInitializer game)
        {
            game.TurnManager.NextTurn();
            for (int i = 0; i < 12 && game.TurnManager.CurrentPhase != global::TurnManager.TurnPhase.StartPrepare; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            ResolveStackOnce(game);
        }

        // ==================== ① 结构 ====================

        public void TestStepPledge_JC024结构()
        {
            var tmpl = Load("JC024_SPTdongshiju");
            Assert.IsNotNull(tmpl, "JC024 模板应可加载");

            var e = FindEffect(tmpl, "JC024_2_LatentThenFreeUncoverNextPrepare");
            Assert.IsNotNull(e, "JC024 应有『潜伏 + 下个准备步骤免费现身』激活");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活");
            Assert.AreEqual("快速", e.ActivateWindow, "窗口＝快速");
            Assert.AreEqual(2, e.Cost, "卡文『快速行动2』→ 费用 2");
            Assert.AreEqual(ActionVerb.Latent, e.Actions[0].Verb, "动作 1＝潜伏自身");
            Assert.IsNotNull(e.Pending, "动作 2＝登记一次性承诺");
            Assert.AreEqual(EffectTiming.OnTurnStarted, e.Pending.Timing, "承诺时机＝准备步骤（OnTurnStarted）");
            Assert.AreEqual("", e.Pending.Expire, "不过期（等『下一个准备步骤』自然兑现）");
            Assert.IsFalse(e.Pending.Optional, "卡文无『你可以』→ 强制兑现");
            Assert.AreEqual(ActionVerb.Uncover, e.Pending.Actions[0].Verb, "兑现＝现身");
            Assert.AreEqual("自身", e.Pending.Actions[0].Args[0], "兑现对象＝自身");
        }

        // ==================== ② 激活：潜伏 + 登记承诺（不立即现身） ====================

        public void TestStepPledge_激活则潜伏并登记承诺()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)3);
            var unit = game.CardManager.CreateCardInstance(Load("JC024_SPTdongshiju"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            var e = FindEffect(unit.PlayerTemplate, "JC024_2_LatentThenFreeUncoverNextPrepare");
            EffectEngine.Instance.ActivateAbility(unit, e, null);
            Assert.IsFalse(game.StackManager.IsEmpty, "激活入堆叠");
            ResolveStackOnce(game);

            Assert.IsTrue(unit.IsCovert, "① 已将自身潜伏（翻面为暗藏者）");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "支付 2 费（快速行动2）");
            Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "② 已登记『下个准备步骤』承诺");
            Assert.IsTrue(unit.IsCovert, "登记承诺本身**不**现身");
        }

        // ==================== ③ 兑现：下一个准备步骤 → 免费现身（不扣费） ====================

        public void TestStepPledge_下个准备步骤免费现身()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            // 只给 2 张资产：刚好够发动（快速行动2），**远不够**支付卡面费用 6 →
            // 若现身需要付费，则此刻根本付不起；现身仍成功即证明"不需要支付其费用"。
            GiveAssets(game, 1, 2, (CardColor)3);
            var unit = game.CardManager.CreateCardInstance(Load("JC024_SPTdongshiju"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var e = FindEffect(unit.PlayerTemplate, "JC024_2_LatentThenFreeUncoverNextPrepare");
            EffectEngine.Instance.ActivateAbility(unit, e, null);
            ResolveStackOnce(game);
            Assert.IsTrue(unit.IsCovert, "前置：已潜伏");
            Assert.AreEqual(0, game.PlayerManager.GetAvailableMana(1), "前置：费用已用尽（恰够发动）");
            Assert.IsTrue(unit.PlayerTemplate.Cost > game.PlayerManager.GetAvailableMana(1),
                "前置：当前可用费用**远低于**卡面费用 6");

            AdvanceToNextPrepare(game);

            Assert.IsFalse(unit.IsCovert, "下一个准备步骤 → 已现身（正面角色）");
            Assert.AreEqual(CardZone.AreaZone, unit.CurrentZone, "现身不移动（仍在原地区）");
            Assert.AreEqual(0, EffectEngine.Instance.PendingEffectCount, "承诺已兑现并消费");
            // 准备步骤会重置（资产解横置）→ 可用费用回到 2，即**没有**为现身支付过 6 费
            // （若强行扣费，只会被拒绝而无法现身；现身成功本身即证明免费）。
            Assert.AreEqual(2, game.PlayerManager.GetAvailableMana(1), "现身未发生任何付费（重置后仍为 2）");
        }

        /// <summary>兑现出来的触发是**强制**的（卡文无"你可以"）。</summary>
        public void TestStepPledge_兑现为强制触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)3);
            var unit = game.CardManager.CreateCardInstance(Load("JC024_SPTdongshiju"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var e = FindEffect(unit.PlayerTemplate, "JC024_2_LatentThenFreeUncoverNextPrepare");
            EffectEngine.Instance.ActivateAbility(unit, e, null);
            ResolveStackOnce(game);

            // 手动推进到准备步骤，但**不结算堆叠**，先检查入栈条目的 Forced
            game.TurnManager.NextTurn();
            for (int i = 0; i < 12 && game.TurnManager.CurrentPhase != global::TurnManager.TurnPhase.StartPrepare; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            StackEntry entry = null;
            foreach (var en in game.StackManager.Entries)
                if (en.EffectDataEffect?.EffectId != null && en.EffectDataEffect.EffectId.StartsWith("Pending_")) entry = en;
            Assert.IsNotNull(entry, "承诺已兑现为触发并入堆叠");
            Assert.IsTrue(entry.EffectDataEffect.Forced, "强制兑现（卡文无『你可以』）");
            ResolveStackOnce(game);
            Assert.IsFalse(unit.IsCovert, "结算后现身");
        }

        /// <summary>兑现前卡已离场 → 现身动作安全跳过（不改状态、不报错）。</summary>
        public void TestStepPledge_离场后兑现安全跳过()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)3);
            var unit = game.CardManager.CreateCardInstance(Load("JC024_SPTdongshiju"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var e = FindEffect(unit.PlayerTemplate, "JC024_2_LatentThenFreeUncoverNextPrepare");
            EffectEngine.Instance.ActivateAbility(unit, e, null);
            ResolveStackOnce(game);
            Assert.AreEqual(1, EffectEngine.Instance.PendingEffectCount, "前置：已登记承诺");

            // 潜伏期间被消灭（离开争夺区）
            game.CardManager.ResolveDeath(unit, CardManager.DeathCause.Destroy);
            ResolveStackOnce(game);

            AdvanceToNextPrepare(game);

            Assert.AreEqual(CardZone.Graveyard, unit.CurrentZone, "卡仍在墓地（未被错误移回场上）");
            Assert.AreEqual(0, EffectEngine.Instance.PendingEffectCount, "承诺仍被消费（时机已出现）");
        }
    }
}
