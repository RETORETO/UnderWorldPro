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
    /// 【P3·K3 死亡类·第一批】按用户 2026-09 口径（清单 §7.4b A10）实现并锁定：
    ///  · **走触发路线**（不是替代式效应）：角色**先进入墓地**，再由死亡触发把它**取出**并"封印/回场"；
    ///  · `OnCardDied` + 谓词 `DiedAllySpiritCharacter`（"另一个本方灵体角色死亡"）+
    ///    `PayOptional{2,2,2}`（"你可以支付2"）→ `Seal{"触发卡","目标"}`；
    ///  · **`"触发卡"` 主体**＝本次触发的上下文卡（`TriggerContext.ContextCard`＝刚死亡的角色），
    ///    由 `TurnManager` 在结算触发条目前从 `StackEffectInstance.TargetCardUid` 下发给
    ///    `EffectEngine.TriggerContextCard`；
    ///  · 封印走唯一出口 `TurnManager.SealCard`（`MoveToSealedZone` → `RemoveFromAllZones`）→ 该角色**离开墓地**、
    ///    进入目标角色的封印区（`SealedBy`＝宿主、宿主 `SealedCards` 收录）。
    /// 覆盖卡：**JC105 噩梦领主**（灵体；持续：其他本方灵体获白星；死亡触发：可支付2→改封印）。
    /// </summary>
    public class P3DeathSealTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 105 };
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

        /// <summary>探针卡：`inherentKeyword` 经"自身持续授予"表达（＝卡面固有词条）。</summary>
        private static PlayerCardData Probe(string uid, string inherentKeyword = null,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 2,
            };
            if (inherentKeyword != null)
            {
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_inherent",
                    Timing = EffectTiming.Continuous,
                    ContinuousPrereq = "常驻",
                    ContinuousEffect = $"关键字：{inherentKeyword}",
                    ContinuousGrants = new Array<EffectGrant>
                    {
                        new EffectGrant { Kind = "Keyword", Value = inherentKeyword, Amount = 1 },
                    },
                    Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                });
            }
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

        /// <summary>给某玩家若干资产（供"支付2"）：资产区未横置卡数＝可用费用。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(Probe($"TS_ds_asset_{playerId}_{i}", category: CardCategory.Attachment), playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static void ResolveStack(GameInitializer game)
        {
            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>触发排序视图桩：全部触发直接入堆叠；目标一律选定桩里指定的卡（便于确定性断言）。</summary>
        private sealed class FixedTargetView : Script.Interfaces.ITriggerView
        {
            public string TargetUid;

            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(!string.IsNullOrEmpty(TargetUid) ? TargetUid
                    : (validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null));

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>UI 钩子桩：数值输入按 `PayAmount` 回包（-1＝不支付）。</summary>
        private sealed class HooksStub : Script.Interfaces.IGameUIHooks
        {
            public int PayAmount = 2;
            public int NumberCalls;
            public int LastNumberMin = -1;
            public int LastNumberMax = -1;

            public int PayOrTapCalls;
            /// <summary>【2026-09 整体复查】固定金额的"你可以支付N"改走**二选一窗**（不再用数值输入框）。</summary>
            public int OptionalPayCalls;
            public int LastOptionalPayAmount = -1;
            public void PromptPayOrTap(CardInstance source, int payAmount) => PayOrTapCalls++;
            public void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
            {
                OptionalPayCalls++;
                LastOptionalPayAmount = amount;
                onChosen?.Invoke(PayAmount > 0);
            }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
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
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(0);

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                NumberCalls++;
                LastNumberMin = min;
                LastNumberMax = max;
                onChosen?.Invoke(PayAmount);
            }
        }

        // ==================== ① 结构 ====================

        public void TestDeathSeal_JC105结构()
        {
            var tmpl = Load("JC105_emenglingzhu");
            var e = FindEffect(tmpl, "JC105_B_PayToSealDeadSpiritOnTarget");
            Assert.IsNotNull(e, "JC105 应有『另一个本方灵体角色死亡 → 可支付2 改封印』效果");
            Assert.AreEqual(EffectTiming.OnCardDied, e.Timing, "时机＝角色死亡");
            Assert.AreEqual("DiedAllySpiritCharacter", e.Condition.Predicate, "条件＝另一个本方灵体角色死亡");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 非强制");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝目标角色（封印宿主）");
            Assert.IsTrue(e.Target.NeedsSelection, "宿主需玩家选择");
            Assert.AreEqual(ActionVerb.PayOptional, e.Actions[0].Verb, "第 1 个动作＝可选支付");
            Assert.AreEqual("2", e.Actions[0].Args[0], "支付金额＝2");
            Assert.AreEqual("2", e.Actions[0].Args[1], "最小值＝2（不接受其他金额）");
            Assert.AreEqual("2", e.Actions[0].Args[2], "最大值＝2");
            Assert.AreEqual(ActionVerb.Seal, e.Actions[1].Verb, "第 2 个动作＝封印");
            Assert.AreEqual("触发卡", e.Actions[1].Args[0], "被封印者＝触发卡（刚死亡的角色）");
            Assert.AreEqual("目标", e.Actions[1].Args[1], "封印宿主＝目标角色");
        }

        // ==================== ② 端到端：支付2 → 死亡角色被封印到目标角色上 ====================

        public void TestDeathSeal_支付2则死亡灵体被封印到目标角色上()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            GiveAssets(game, 1, 3);
            var lord = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(lord, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ds_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ds_spirit", inherentKeyword: "灵体"), 1);
            game.ZoneManager.MoveToArea(victim, area, 1);

            var view = new FixedTargetView { TargetUid = host.UID };
            var hooks = new HooksStub { PayAmount = 2 };
            game.TurnManager.SetTriggerView(view);
            EffectEngine.Instance.UIHooks = hooks;

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(manaBefore >= 2, "前置：可用费用 ≥ 2");

            game.CardManager.DestroyInstance(victim);    // 死亡（走 ResolveDeath → OnCardDied）
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "死亡后先进入墓地（触发路线口径）");

            ResolveStack(game);                          // 结算死亡触发

            Assert.AreEqual(1, hooks.OptionalPayCalls, "询问过一次『是否支付2』（**二选一窗**）");
            Assert.AreEqual(2, hooks.LastOptionalPayAmount, "询问金额＝2");
            Assert.AreEqual(0, hooks.NumberCalls, "**不再使用数值输入框**（用户口径：这类是/否选择不需要输入框）");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "支付了 2 点费用");

            Assert.AreEqual(CardZone.SealedZone, victim.CurrentZone, "死亡的角色被封印（离开墓地）");
            Assert.AreEqual(host, victim.SealedBy, "封印宿主＝目标角色");
            Assert.IsTrue(host.SealedCards != null && host.SealedCards.Contains(victim), "宿主收录该封印牌");
            Assert.AreEqual(0, game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count, "该角色已不在墓地");
        }

        // ==================== ③ 不支付 → 照常留在墓地 ====================

        public void TestDeathSeal_不支付则留在墓地()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            GiveAssets(game, 1, 3);
            var lord = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(lord, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ds_host2"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ds_spirit2", inherentKeyword: "灵体"), 1);
            game.ZoneManager.MoveToArea(victim, area, 1);

            game.TurnManager.SetTriggerView(new FixedTargetView { TargetUid = host.UID });
            var hooks = new HooksStub { PayAmount = -1 };     // 不支付
            EffectEngine.Instance.UIHooks = hooks;

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.CardManager.DestroyInstance(victim);
            ResolveStack(game);

            Assert.AreEqual(1, hooks.OptionalPayCalls, "询问过一次『是否支付2』（**二选一窗**）");
            Assert.AreEqual(0, hooks.NumberCalls, "**不再使用数值输入框**");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 → 不扣费");
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "不支付 → 角色照常留在墓地");
            Assert.IsNull(victim.SealedBy, "未被封印");
        }

        // ==================== ④ 非灵体角色死亡 → 条件不成立 ====================

        public void TestDeathSeal_非灵体角色死亡不触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            GiveAssets(game, 1, 3);
            var lord = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(lord, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ds_host3"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_ds_nonspirit"), 1);   // 无「灵体」
            game.ZoneManager.MoveToArea(victim, area, 1);

            game.TurnManager.SetTriggerView(new FixedTargetView { TargetUid = host.UID });
            var hooks = new HooksStub { PayAmount = 2 };
            EffectEngine.Instance.UIHooks = hooks;

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("DiedAllySpiritCharacter", lord, lord, victim, 1, area),
                "非灵体角色死亡 → 条件不成立");

            game.CardManager.DestroyInstance(victim);
            ResolveStack(game);

            Assert.AreEqual(0, hooks.NumberCalls, "条件不成立 → 不询问支付");
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "照常进墓地");
            Assert.IsNull(victim.SealedBy, "未被封印");
        }

        // ==================== ⑤ 敌方灵体死亡 → 条件不成立（"本方"限定） ====================

        public void TestDeathSeal_敌方灵体死亡不触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            GiveAssets(game, 1, 3);
            var lord = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(lord, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ds_host4"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ds_enemy_spirit", inherentKeyword: "灵体"), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);

            game.TurnManager.SetTriggerView(new FixedTargetView { TargetUid = host.UID });
            var hooks = new HooksStub { PayAmount = 2 };
            EffectEngine.Instance.UIHooks = hooks;

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("DiedAllySpiritCharacter", lord, lord, enemy, 2, area),
                "敌方灵体死亡 → 条件不成立（卡文『本方』）");

            game.CardManager.DestroyInstance(enemy);
            ResolveStack(game);

            Assert.AreEqual(0, hooks.NumberCalls, "不询问支付");
            Assert.AreEqual(CardZone.Graveyard, enemy.CurrentZone, "敌方角色照常进墓地");
        }

        // ==================== ⑥ 来源自身死亡不触发（"另一个"限定） ====================

        public void TestDeathSeal_来源自身死亡不触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            GiveAssets(game, 1, 3);
            var lord = game.CardManager.CreateCardInstance(Load("JC105_emenglingzhu"), 1);
            game.ZoneManager.MoveToArea(lord, area, 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_ds_host5"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);

            game.TurnManager.SetTriggerView(new FixedTargetView { TargetUid = host.UID });
            var hooks = new HooksStub { PayAmount = 2 };
            EffectEngine.Instance.UIHooks = hooks;

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("DiedAllySpiritCharacter", lord, lord, lord, 1, area),
                "JC105 自身死亡（＝来源自身）→ 条件不成立（卡文『另一个』）");

            game.CardManager.DestroyInstance(lord);
            ResolveStack(game);

            Assert.AreEqual(0, hooks.NumberCalls, "不询问支付");
            Assert.AreEqual(CardZone.Graveyard, lord.CurrentZone, "JC105 自身照常进墓地");
        }
    }
}
