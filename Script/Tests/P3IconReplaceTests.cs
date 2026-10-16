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
    /// 【P3·K20 第一批：**图标替换＝属性替换** → JC106 酒神秘仪】
    /// 卡文：快速行动：本回合中，目标角色上的所有［黑星］和［白星］能力图标都替换为［黑刀］，并获得灵体。
    /// 口径（用户 2026-09）：图标替换**就是属性替换**；这几张卡**都影响全部同类图标**、**任意顺序**完成即可；
    /// 整套替换在**效果内部**一次做完（原子锁期间不会被其它触发插进来）。
    ///
    /// 交付：`ActionVerb.ReplaceIcon`（Args: [来源图标清单, 目标图标, 对象]）：
    ///  计数＝来源图标的**当前有效贡献**（势力＝`CurrentPower`；先手势力＝授予的『先手势力』），
    ///  替换＝`Grant(来源属性, -n)` + `Grant(目标属性, +n)`；`Duration` 含"回合结束"时登记 `TimedMod{Kind="IconReplace"}`
    ///  （`Value="来源|目标"`）→ 回合结束**精确还原**。
  /// </summary>
    public class P3IconReplaceTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1097 };
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

        /// <summary>带印刷图标的角色探针。</summary>
        private static PlayerCardData Probe(string uid, int power = 0, int firstStrikePower = 0, int strength = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5,
                Power = power, FirstStrikePower = firstStrikePower, Strength = strength,
            };
            tmpl.SubCategories.Add("人类");
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

        /// <summary>结算一次 JC106 效果（聚焦"图标替换"本身，不经打出流程）。</summary>
        private static void ApplyJC106(GameInitializer game, CardInstance target)
        {
            var tmpl = Load("JC106_jiushenmiyi");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            EffectEngine.Instance.ResolveEffect(FindEffect(tmpl, "JC106_1_ReplaceStarsToBladeAndGainSpirit"), spell, target);
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

        public void TestReplace_JC106结构()
        {
            var tmpl = Load("JC106_jiushenmiyi");
            Assert.IsNotNull(tmpl, "JC106 模板应可加载");
            var e = FindEffect(tmpl, "JC106_1_ReplaceStarsToBladeAndGainSpirit");
            Assert.IsNotNull(e, "JC106 应有『黑星/白星 → 黑刀 + 获得灵体』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡打出即结算");
            Assert.AreEqual(PlayTiming.Response, e.PlayTiming, "快速行动");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.AreEqual(ActionVerb.ReplaceIcon, e.Actions[0].Verb, "第一条＝图标替换");
            Assert.AreEqual("黑星,白星", e.Actions[0].Args[0], "来源图标＝黑星+白星（全部同类图标）");
            Assert.AreEqual("黑刀", e.Actions[0].Args[1], "目标图标＝黑刀");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[1].Verb, "第二条＝获得灵体");
            Assert.AreEqual("灵体", e.Actions[1].Args[0], "获得的是灵体");
            Assert.AreEqual("直到回合结束", e.Duration, "只到回合结束");
        }

        // ==================== ② 黑星(印刷势力2)被整块换成黑刀 ====================

        public void TestReplace_黑星换黑刀()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ir_star", power: 2), 2, areas[0]);
            int strengthBefore = victim.CurrentStrength;

            ApplyJC106(game, victim);

            Assert.AreEqual(0, victim.CurrentPower, "势力图标全部被替换掉（不再提供势力）");
            Assert.AreEqual(strengthBefore + 2, victim.CurrentStrength, "换成等量的黑刀（战斗 +2）");
        }

        // ==================== ③ 黑星 + 白星（先手势力）一起换成黑刀 ====================

        public void TestReplace_黑白星一起替换()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ir_both", power: 1, firstStrikePower: 2), 2, areas[0]);
            victim.Grant("Attribute", "先手势力", 1);            // 再叠一个授予的白星
            int strengthBefore = victim.CurrentStrength;
            // 【2026-10 用户口径·7e】改写的计数＝**图标集合**（**含印刷图标**：本项目把印刷
            // `Strength/Power/FirstStrike*` 就当能力图标读，见 `BuildMirroredIconGrants`）
            // ⇒ 黑星 = 印刷势力 ＋ 授予势力；白星 = 印刷先手势力 ＋ 授予先手势力。
            int expected = victim.CurrentPower + victim.FirstStrikePowerTotal;

            ApplyJC106(game, victim);

            Assert.AreEqual(0, victim.CurrentPower, "黑星被替换（印刷势力 ＋ 授予势力 一起改写）");
            Assert.AreEqual(0, victim.FirstStrikePowerTotal,
                "白星被替换：先手势力**总值**（印刷先手势力 ＋ 授予）归零（净账为 −印刷值，故读总值）");
            Assert.AreEqual(strengthBefore + expected, victim.CurrentStrength,
                "换成等量的黑刀（合计 = 原有黑星 ＋ 白星的图标数）");
            Assert.IsTrue(victim.HasIcon("黑刀"), "改写必须产出［黑刀］图标");
            Assert.IsFalse(victim.HasIcon("黑星") || victim.HasIcon("白星"), "来源图标不再存在");
        }

        // ==================== ④ 无势力图标 → 只获得灵体，不做无意义替换 ====================

        public void TestReplace_无势力图标()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ir_none", strength: 3), 2, areas[0]);
            int strengthBefore = victim.CurrentStrength;

            ApplyJC106(game, victim);

            Assert.AreEqual(strengthBefore, victim.CurrentStrength, "没有黑星/白星 → 战斗不变");
            Assert.IsTrue(victim.HasKeyword("灵体"), "仍然获得灵体");
        }

        // ==================== ⑤ 回合结束精确还原（时效项） ====================

        public void TestReplace_回合结束还原()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ir_revert", power: 2, firstStrikePower: 1), 2, areas[0]);
            int powerBefore = victim.CurrentPower;
            int strengthBefore = victim.CurrentStrength;

            ApplyJC106(game, victim);
            Assert.AreEqual(0, victim.CurrentPower, "前置：势力被替换");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.AreEqual(powerBefore, victim.CurrentPower, "回合结束 → 势力图标还原");
            Assert.AreEqual(strengthBefore, victim.CurrentStrength, "回合结束 → 换来的黑刀收回");
            Assert.IsFalse(victim.HasKeyword("灵体"), "回合结束 → 灵体也撤销（同为本回合时效）");
        }

        // ==================== ⑥ 对象＝自身（Args[2] 写法可用） ====================

        public void TestReplace_对象可写自身()
        {
            var game = NewGame();
            var areas = Areas(game);
            var self = OnField(game, Probe("TS_ir_self", power: 2), 1, areas[0]);
            int strengthBefore = self.CurrentStrength;

            // 用**本地构造**的效果数据改对象参数（不污染共享模板数据），聚焦"对象＝自身"这一写法
            var local = new EffectData
            {
                EffectId = "TS_ir_self_check",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.ReplaceIcon,
                        Args = new Array<string> { "黑星,白星", "黑刀", "自身" },
                    },
                },
                Duration = "瞬时",
            };

            EffectEngine.Instance.ResolveEffect(local, self, null);

            Assert.AreEqual(0, self.CurrentPower, "对象＝自身：自身的黑星被替换");
            Assert.AreEqual(strengthBefore + 2, self.CurrentStrength, "自身获得等量黑刀");
        }
    }
}
