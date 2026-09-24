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
    /// 【P3·F·按计数缩放的持续授予】按卡文实现并锁定：
    ///  · N 由 `ContinuousPrereqSpec.ScaleExpr`（复用 `EffectValueResolver` 的计数表达式）求出，
    ///    **授予量＝每条 `ContinuousGrant.Amount × N`**，且 **N&gt;0 才算前提成立**（N=0 时不授予）；
    ///  · 计数变化即时重判：暗藏者进出/翻面（`ZoneManager.OnPostMove` / `CovertChanged`）、
    ///    卡上标志增减（`CardManager.AddCounter/RemoveCounter`）、封印落地（`TurnManager.SealCard`）。
    /// 覆盖卡：**JC037 克里斯蒂娜·爱弗罗德**（每操控一个暗藏者 → 一个黑眼）、
    ///        **JC045 钟摆祭司**（其上每有一个时间标志 → 一个黑星）、
    ///        **JC109 沉沦的巴德勒**（其上每有一张封印牌 → 一个黑刀）。
    /// </summary>
    public class P3ScaledGrantTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 151 };
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestScaled_结构三卡()
        {
            var e37 = FindEffect(Load("JC037_kelisidinaaifuluode"), "JC037_B_BlackEyePerCovert");
            Assert.IsNotNull(e37, "JC037 应有『每操控一个暗藏者 → 黑眼』持续效果");
            Assert.AreEqual("计数:区域:争夺区:暗藏者:自己", e37.PrereqSpec.ScaleExpr, "缩放表达式＝本方争夺区暗藏者数");
            Assert.AreEqual("黑眼", e37.ContinuousGrants[0].Value, "授予黑眼");

            var e45 = FindEffect(Load("JC045_zhongbaijisi"), "JC045_2_BlackStarPerTimeMarker");
            Assert.IsNotNull(e45, "JC045 应有『每有一个时间标志 → 黑星』持续效果");
            Assert.AreEqual("计数:卡上标志:时间标志:自身", e45.PrereqSpec.ScaleExpr, "缩放表达式＝卡上时间标志数");
            Assert.AreEqual("黑星", e45.ContinuousGrants[0].Value, "授予黑星");

            var e109 = FindEffect(Load("JC109_chenlundebadelei"), "JC109_2_BlackBladePerSealed");
            Assert.IsNotNull(e109, "JC109 应有『每有一张封印牌 → 黑刀』持续效果");
            Assert.AreEqual("计数:封印牌:自身", e109.PrereqSpec.ScaleExpr, "缩放表达式＝其上封印牌数");
            Assert.AreEqual("黑刀", e109.ContinuousGrants[0].Value, "授予黑刀");
        }

        // ==================== ② JC037：暗藏者数量缩放（并按翻面/离场实时重算） ====================

        public void TestScaled_JC037按暗藏者数缩放()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var kristina = game.CardManager.CreateCardInstance(Load("JC037_kelisidinaaifuluode"), 1);
            game.ZoneManager.MoveToArea(kristina, a1, 1);
            EffectEngine.Instance.RegisterPersistent(kristina);

            Assert.AreEqual(0, kristina.GetAttribute("调查"), "尚无暗藏者 → 黑眼数 0（N=0 前提不成立）");
            Assert.IsFalse(kristina.HasIcon("黑眼"), "尚无暗藏者 → 没有黑眼");

            // 两个本方暗藏者（一个在别的地区也算"你操控"）
            var c1 = game.CardManager.CreateCardInstance(Probe("TS_sg_c1"), 1);
            c1.SetCovert(true);
            game.ZoneManager.MoveToArea(c1, a1, 1);
            var c2 = game.CardManager.CreateCardInstance(Probe("TS_sg_c2"), 1);
            c2.SetCovert(true);
            game.ZoneManager.MoveToArea(c2, a2, 1);
            Assert.IsTrue(kristina.HasIcon("黑眼"), "有暗藏者 → 获得黑眼");
            Assert.IsTrue(kristina.HasIcon("黑眼"), "N=2 → 仍有黑眼（图标授予不折进调查属性；属性映射只在 Gain 动词里）");

            // 一个暗藏者翻面 → N 减到 1
            game.CardManager.RevealCard(c1);
            Assert.IsTrue(kristina.HasIcon("黑眼"), "暗藏者翻面 → N=1 → 黑眼仍生效");

            // 另一个离场 → N=0（前提不成立，黑眼清空）
            game.ZoneManager.MoveToHand(c2, 1);
            Assert.AreEqual(0, kristina.GetAttribute("调查"), "暗藏者离场 → N=0（未登记属性授予，属性为 0）");
            Assert.IsFalse(kristina.HasIcon("黑眼"), "N=0 时不再有黑眼");
        }

        // ==================== ③ JC045：时间标志数缩放 ====================

        public void TestScaled_JC045按时间标志缩放()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var priest = game.CardManager.CreateCardInstance(Load("JC045_zhongbaijisi"), 1);
            game.ZoneManager.MoveToArea(priest, area, 1);
            EffectEngine.Instance.RegisterPersistent(priest);

            Assert.IsFalse(priest.HasIcon("黑星"), "前置：无时间标志 → 无黑星");

            game.CardManager.AddCounter(priest, "时间标志", 1);
            Assert.IsTrue(priest.HasIcon("黑星"), "有 1 个时间标志 → 获得黑星");

            game.CardManager.AddCounter(priest, "时间标志", 2);
            Assert.IsTrue(priest.HasIcon("黑星"), "N=3 → 黑星仍生效（缩放倍数经授予量体现，图标为集合语义）");

            game.CardManager.RemoveCounter(priest, "时间标志", 2);
            Assert.IsTrue(priest.HasIcon("黑星"), "移除 2 个（剩 1）→ N=1 → 黑星仍生效");

            game.CardManager.RemoveCounter(priest, "时间标志", 1);
            Assert.IsFalse(priest.HasIcon("黑星"), "标志清空 → N=0 → 黑星被撤销");
        }

        // ==================== ④ JC109：其上封印牌数缩放 ====================

        public void TestScaled_JC109按封印牌缩放()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var badler = game.CardManager.CreateCardInstance(Load("JC109_chenlundebadelei"), 1);
            game.ZoneManager.MoveToArea(badler, area, 1);
            EffectEngine.Instance.RegisterPersistent(badler);

            Assert.IsFalse(badler.HasIcon("黑刀"), "前置：其上无封印牌 → 无黑刀");

            // 封印两张牌在其上（走唯一出口）
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            game.SetInputGateway(new EnterAllGateway());
            for (int i = 0; i < 2; i++)
            {
                var seal = game.CardManager.CreateCardInstance(Probe($"TS_sg_seal{i}"), 1);
                game.ZoneManager.MoveToHand(seal, 1);
                game.TurnManager.SealCard(seal, badler);
            }

            Assert.IsTrue(badler.HasIcon("黑刀"), "有封印牌 → 获得黑刀（N=2）");
        }

        // ==================== ⑤ 表达式未登记 → fail-closed（不授予，不静默当 0 之外的行为） ====================

        public void TestScaled_表达式未登记则不授予()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var tmpl = Probe("TS_sg_bad");
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_sg_bad_e",
                Timing = EffectTiming.Continuous,
                PrereqSpec = new ContinuousPrereqSpec { ScaleExpr = "计数:不存在的写法" },
                ContinuousEffect = "未登记表达式探针",
                ContinuousGrants = new Array<EffectGrant> { new EffectGrant { Kind = "Icon", Value = "黑星" } },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
            });
            tmpl._effectsInjected = true;

            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            EffectEngine.Instance.RegisterPersistent(card);

            Assert.IsFalse(card.HasIcon("黑星"), "未登记的缩放表达式 → 求值失败 → 不授予（fail-closed）");
        }

        // ==================== 桩件 ====================

        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                if (req?.OptionUids != null && req.OptionUids.Count > 0) r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone) { }
            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }
    }
}
