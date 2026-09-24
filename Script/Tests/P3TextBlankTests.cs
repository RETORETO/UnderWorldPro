using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K20 收尾：**"印刷文本框视作空白"（失去效果）+ 失去魔法领域图标** → JC101 直视深渊】
    /// 卡文：持续：受此结附的角色，其印刷文本框视作空白，且失去所有魔法领域图标。（不影响能力栏内的能力图标）
    /// 口径（用户 2026-09）：文本空白→**该卡失去效果**，但**不影响已经发动的效果**。
    ///
    /// 交付：`EffectData.TextBlank`（与 `DomainSuppression.Victim = AttachedHost` 配套）
    ///  · `EffectEngine.IsTextBlank` 三处门禁：`RegisterPersistent`（不再注册）/ `ActivateEligibility`
    ///    （不再可发动、不出现在候选里）/ `TriggerRegistry`（不参与触发收集）+ `ActivateAbility` 权威拒绝；
    ///  · 施加时会 `UnregisterPersistent`（已注册的持续效果停止）；解除时重新注册；
    ///  · **结算路径不加检查** → 已经发动（已在堆叠）的效果照常结算。
  /// </summary>
    public class P3TextBlankTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1277 };
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

        /// <summary>角色探针：可选挂一条"持续授予自身 +1 战斗"的效果（用于验证"失去效果"会停掉持续效果）。</summary>
        private static PlayerCardData Probe(string uid, bool withContinuous = false, MagicDomain? domain = MagicDomain.Mind)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5,
            };
            tmpl.SubCategories.Add("人类");
            if (domain.HasValue) tmpl.MagicDomains.Add(domain.Value);
            if (withContinuous)
            {
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_cont",
                    Timing = EffectTiming.Continuous,
                    ContinuousPrereq = "常驻",
                    ContinuousEffect = "自身 +1 战斗",
                    ContinuousGrants = new Godot.Collections.Array<EffectGrant>
                    {
                        new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                    },
                    Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                    Duration = "常驻",
                });
            }
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
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

        public void TestTextBlank_JC101结构()
        {
            var tmpl = Load("JC101_zhishishenyuan");
            Assert.IsNotNull(tmpl, "JC101 模板应可加载");
            var e = FindEffect(tmpl, "JC101_1_BlankTextAndSuppressDomains");
            Assert.IsNotNull(e, "JC101 应有『文本空白 + 失去领域图标』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsTrue(e.TextBlank, "声明了『印刷文本框视作空白』（失去效果）");
            Assert.IsNotNull(e.DomainSuppression, "同时声明了失去魔法领域图标");
            Assert.AreEqual(DomainSuppressionVictim.AttachedHost, e.DomainSuppression.Victim, "对象＝结附宿主");
            Assert.AreEqual(TargetKind.Character, tmpl.TargetRules[0].Kind, "打出目标＝角色（结附于目标角色）");
        }

        // ==================== ② 结附后：宿主失去领域图标 + 文本空白 ====================

        public void TestTextBlank_结附后宿主失活()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_tb_host"), 2, areas[0]);
            Assert.AreEqual(1, host.MagicDomains.Count, "前置：宿主有 1 个魔法领域图标");

            var curse = game.CardManager.CreateCardInstance(Load("JC101_zhishishenyuan"), 1);
            game.ZoneManager.MoveToArea(curse, areas[0], 1);
            Attach(game, host, curse);

            Assert.AreEqual(0, host.MagicDomains.Count, "宿主失去全部魔法领域图标");
            Assert.IsTrue(EffectEngine.Instance.IsTextBlank(host), "宿主被标记为『文本视作空白』（失去效果）");
        }

        // ==================== ③ "失去效果"：已注册的持续效果停止 ====================

        public void TestTextBlank_持续效果停止()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_tb_cont", withContinuous: true), 2, areas[0]);
            int strengthBefore = host.CurrentStrength;   // 已含自身持续授予的 +1

            var curse = game.CardManager.CreateCardInstance(Load("JC101_zhishishenyuan"), 1);
            game.ZoneManager.MoveToArea(curse, areas[0], 1);
            Attach(game, host, curse);

            Assert.AreEqual(strengthBefore - 1, host.CurrentStrength,
                "宿主被『文本视作空白』→ 其持续效果被注销（+1 战斗消失）");
        }

        // ==================== ④ 不能发动：激活门禁与权威门禁都拒绝 ====================

        public void TestTextBlank_不能发动()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_tb_act"), 2, areas[0]);
            var curse = game.CardManager.CreateCardInstance(Load("JC101_zhishishenyuan"), 1);
            game.ZoneManager.MoveToArea(curse, areas[0], 1);
            Attach(game, host, curse);
            game.TurnManager.PriorityPlayerId = 2;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;

            Assert.IsFalse(ActivateEligibility.CanActivate(host, game.TurnManager, game.PlayerManager, game.CardManager),
                "文本视作空白 → 该卡不可发动任何激活能力（候选/高亮侧）");

            var eligible = ActivateEligibility.GetEligibleAbilities(2, game.TurnManager, game.PlayerManager, game.CardManager);
            Assert.IsFalse(eligible.Exists(t => t.Source == host), "候选列表里也不出现该卡");
        }

        // ==================== ⑤ 解除结附 → 恢复（效果重新生效、领域图标回来） ====================

        public void TestTextBlank_解除结附后恢复()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_tb_recover", withContinuous: true), 2, areas[0]);
            int strengthBefore = host.CurrentStrength;
            var curse = game.CardManager.CreateCardInstance(Load("JC101_zhishishenyuan"), 1);
            game.ZoneManager.MoveToArea(curse, areas[0], 1);
            Attach(game, host, curse);
            Assert.IsTrue(EffectEngine.Instance.IsTextBlank(host), "前置：被屏蔽");

            game.ZoneManager.MoveToHand(curse, 1);   // 附属离场（解附）→ 换区触发重判

            Assert.IsFalse(EffectEngine.Instance.IsTextBlank(host), "解除后不再被屏蔽");
            Assert.AreEqual(1, host.MagicDomains.Count, "领域图标恢复");
            Assert.AreEqual(strengthBefore, host.CurrentStrength, "其持续效果重新生效（+1 战斗回来）");
        }

        // ==================== ⑥ 已经发动的效果不受影响（结算路径不加检查） ====================

        public void TestTextBlank_已发动的效果照常结算()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_tb_stack"), 2, areas[0]);
            var curse = game.CardManager.CreateCardInstance(Load("JC101_zhishishenyuan"), 1);
            game.ZoneManager.MoveToArea(curse, areas[0], 1);
            Attach(game, host, curse);   // 先在"未屏蔽"状态下把效果送进堆叠？—— 本例用显式堆叠条目模拟

            // 直接以宿主为来源把一个"造成 2 点伤害"的效果结算：即便它现在文本空白，
            // 已经在堆叠上的效果（＝已经发动的效果）仍应照常结算（用户口径）。
            var victim = OnField(game, Probe("TS_tb_victim"), 1, areas[0]);
            var damageEffect = new EffectData
            {
                EffectId = "TS_tb_damage",
                Timing = EffectTiming.CharacterActivated,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Godot.Collections.Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Godot.Collections.Array<string> { "2" } },   // Args[0]＝伤害值（对象＝本次 target）
                },
                Duration = "瞬时",
            };

            EffectEngine.Instance.ResolveEffect(damageEffect, host, victim);

            Assert.AreEqual(2, victim.Damage, "已经在堆叠/已经发动的效果照常结算（文本空白不回溯撤销）");
        }
    }
}
