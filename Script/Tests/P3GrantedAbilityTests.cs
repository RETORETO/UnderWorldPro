using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K13：**运行期授予的激活能力** → JC019 弥尔顿的镇魔枪】
    /// 卡文：结附于目标角色 持续：受此结附的角色获得［黑刀］，并获得"快速行动，横置：此角色对位于本地区的
    ///      目标非人类角色造成1点伤害，该伤害不能被防止"的能力。
    /// FAQ：使用该能力需要横置**它所结附的角色**，而非武器本身。
    /// 口径（用户 2026-09）：宿主原本就有激活效果时，**在既有的多激活效果选择窗口里额外增列 1 项**。
    ///
    /// 交付：`EffectData.GrantedAbility` + `EffectEngine.ReapplyGrantedAbilities()`（统一重算）
    ///  · 候选/高亮/权威门禁与卡面固有激活能力**同一套**（`ActivateEligibility.GetAllActivateAbilities`
    ///    把运行期授予项并入 → 选择窗口自然"增列 1 项"）；
    ///  · 能力以**宿主**为来源 → `ActivateCondition="横置本角色"` 横置的是宿主（FAQ）；
    ///  · `DealDamage{"1","不可防止"}` → 跳过"防止"（灵体/护盾）直接落地。
  /// </summary>
    public class P3GrantedAbilityTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1321 };
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

        /// <summary>角色探针；`subCategory` 决定是否"非人类"。</summary>
        private static PlayerCardData Probe(string uid, string subCategory = "人类", int defense = 5)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            if (!string.IsNullOrEmpty(subCategory)) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>宿主探针：自带一条可发动的激活能力（用于验证"选择窗口增列 1 项"）。</summary>
        private static PlayerCardData HostWithAbility(string uid)
        {
            var tmpl = Probe(uid);
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_own",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Godot.Collections.Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Godot.Collections.Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "宿主自带能力",
            });
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
            EffectEngine.Instance.ReapplyGrantedAbilities();   // 与生产路径一致：登记后重算运行期授予
        }

        private static CardInstance PlaceSpear(GameInitializer game, CardInstance host, string area)
        {
            var spear = game.CardManager.CreateCardInstance(Load("JC019_mierdundezhenmoqiang"), 1);
            game.ZoneManager.MoveToArea(spear, area, 1);
            Attach(game, host, spear);
            return spear;
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

        public void TestGranted_JC019结构()
        {
            var tmpl = Load("JC019_mierdundezhenmoqiang");
            Assert.IsNotNull(tmpl, "JC019 模板应可加载");
            var e = tmpl.EffectDataList[0];
            Assert.IsNotNull(e.GrantedAbility, "持续效果应声明运行期授予的激活能力");
            Assert.IsTrue(e.GrantToHost, "授予落点＝宿主（结附的角色）");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.GrantedAbility.Timing, "授予的是激活能力");
            Assert.AreEqual("横置本角色", e.GrantedAbility.ActivateCondition, "代价＝横置本角色（宿主，见 FAQ）");
            Assert.AreEqual("快速", e.GrantedAbility.ActivateWindow, "快速行动");
            Assert.IsTrue(e.GrantedAbility.Target.IsLocalZone, "目标限本地区");
            Assert.AreEqual(TargetRestriction.NotCurrentSubCategory, e.GrantedAbility.Target.Restrictions[0],
                "目标须『非人类』（读实例当前类别；用户 2026-09 口径）");
            Assert.AreEqual("人类", e.GrantedAbility.Target.Param, "非人类＝排除『人类』");
            Assert.AreEqual("不可防止", e.GrantedAbility.Actions[0].Args[1], "该伤害不能被防止");
            // 黑刀要"两条通道都写"：Icon（存在性/展示）+ Attribute:战斗（数值）
            Assert.AreEqual(2, e.ContinuousGrants.Count, "黑刀＝图标通道 + 属性通道各一条");
            Assert.AreEqual("Icon", e.ContinuousGrants[0].Kind, "第一条＝图标存在性");
            Assert.AreEqual("Attribute", e.ContinuousGrants[1].Kind, "第二条＝属性数值（黑刀=+1 战力）");
        }

        // ==================== ② 宿主获得能力 + 黑刀 ====================

        public void TestGranted_宿主获得能力与黑刀()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, HostWithAbility("TS_ga_host"), 1, areas[0]);
            int strengthBefore = host.CurrentStrength;

            PlaceSpear(game, host, areas[0]);

            Assert.AreEqual(strengthBefore + 1, host.CurrentStrength, "宿主获得［黑刀］（战斗 +1）");
            Assert.IsTrue(host.HasIcon("黑刀"), "宿主同时获得『黑刀』图标本身（展示/存在性通道）");
            var abilities = ActivateEligibility.GetAllActivateAbilities(host);
            Assert.AreEqual(2, abilities.Count, "宿主自带 1 条 + 运行期授予 1 条 = 2 条（选择窗口自然增列 1 项）");
            Assert.IsTrue(abilities.Exists(a => a.EffectId == "JC019_G_HostActivatedSmite"), "授予的那条在候选里");
        }

        // ==================== ③ 发动：横置宿主 + 不可防止的 1 点伤害 ====================

        public void TestGranted_发动横置宿主并造成不可防止伤害()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_ga_host2"), 1, areas[0]);
            var target = OnField(game, Probe("TS_ga_target", "不死生物"), 2, areas[0]);
            PlaceSpear(game, host, areas[0]);
            var ability = EffectEngine.Instance.GetGrantedAbilities(host)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            // 让目标处于"伤害会被防止"的状态：给它一个伤害护盾（普通伤害会被防止，本能力的伤害不会）
            EffectEngine.Instance.RegisterDamageShield(target);

            EffectEngine.Instance.ActivateAbility(host, ability, target);

            Assert.IsTrue(host.IsTapped, "代价：**横置宿主**（而非武器本身，符合 FAQ）");
            Assert.IsFalse(target.IsDead(), "前置：目标未被直接打死（1 点伤害对 5 防御角色）");
            // 堆叠结算
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.AreEqual(1, target.Damage, "『不可防止』→ 伤害穿透护盾直接落地（护盾没有拦下它）");
        }

        // ==================== ④ 对照：普通伤害会被护盾防止 ====================

        public void TestGranted_对照护盾能拦普通伤害()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ga_shield"), 2, areas[0]);
            EffectEngine.Instance.RegisterDamageShield(victim);

            game.CardManager.DealDamageToInstance(victim, 2);

            Assert.AreEqual(0, victim.Damage, "对照：普通伤害被护盾防止（证明上一用例确实走的是『不可防止』通道）");
        }

        // ==================== ⑤ 解附/武器离场 → 授予的能力收回 ====================

        public void TestGranted_解附后能力收回()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, HostWithAbility("TS_ga_host3"), 1, areas[0]);
            var spear = PlaceSpear(game, host, areas[0]);
            Assert.AreEqual(2, ActivateEligibility.GetAllActivateAbilities(host).Count, "前置：2 条能力");

            game.ZoneManager.MoveToHand(spear, 1);   // 武器离场（解附）

            var abilities = ActivateEligibility.GetAllActivateAbilities(host);
            Assert.AreEqual(1, abilities.Count, "武器离场 → 运行期授予的能力收回，只剩宿主自带的 1 条");
            Assert.IsFalse(abilities.Exists(a => a.EffectId == "JC019_G_HostActivatedSmite"), "授予的那条已消失");
        }
    }
}
