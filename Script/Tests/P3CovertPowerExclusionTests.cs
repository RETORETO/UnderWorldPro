using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K21：地区级"敌方暗藏者不能参与势力对抗" → JC023 范·弥尔顿】
    /// 卡文：领袖 威名 机动 袭击2 持续：所有本地区的敌方暗藏者不能参与势力对抗。
    ///
    /// 交付：`StaticKind.ExcludeCovertPower`（与 `Timing = Continuous` 同一 `EffectData`，前提常驻）
    ///  · 登记/注销生命周期与 K18 的 `SkipAreaCombat` 同款（前提满足登记；离场 / 前提丧失 /
    ///    `RemoveAllContinuousFor` / `ResetAll` 四处注销）；
    ///  · 判定 `EffectEngine.ExcludesCovertPower(地区, 卡的控制者)`：地区内存在登记来源卡且其控制者 ≠ 该控制者
    ///    → 该暗藏者参与资格被取消（**不对称**：只抹掉对手的，本方暗藏者照常贡献）；
    ///  · 消费点＝`AreaStatsCalculator`（对抗结算 `SumPlayerValue` 与 UI 展示 `GetAreaStats` 共用的
    ///    **单一真相源**）→ 显示与结算不会漂移；正面角色/附属完全不受影响；
    ///  · 判定按卡**当前**所在地实时匹配地区 → 换区自动跟随。
    /// </summary>
    public class P3CovertPowerExclusionTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 419 };
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

        /// <summary>角色探针（战力/势力按需）。</summary>
        private static PlayerCardData Probe(string uid, int strength = 0, int power = 0, int investigation = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5,
                Strength = strength, Power = power, Investigation = investigation,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area, bool covert = false)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            if (covert) card.SetCovert(true);   // 暗藏（背面朝上）
            return card;
        }

        private static int Power(GameInitializer game, string area, int playerId)
            => AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager, area, playerId,
                CombatManager.CombatType.Power);

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

        public void TestCovertPower_JC023结构()
        {
            var tmpl = Load("JC023_fanmierdun");
            Assert.IsNotNull(tmpl, "JC023 模板应可加载");
            var e = FindEffect(tmpl, "JC023_E_ExcludeEnemyCovertPower");
            Assert.IsNotNull(e, "JC023 应有『本地区敌方暗藏者不能参与势力对抗』效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果（前提常驻）");
            Assert.IsNotNull(e.StaticEffects, "声明了静止规则");
            Assert.AreEqual(StaticKind.ExcludeCovertPower, e.StaticEffects[0].Kind, "静止种类＝敌方暗藏者不参与势力对抗");
        }

        // ==================== ② 基线：暗藏者本来参与势力（1 点），正面角色不受规则影响 ====================

        public void TestCovertPower_基线暗藏者本来提供势力1()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Probe("TS_cp_base_p2"), 2, areas[0], covert: true);

            Assert.AreEqual(1, Power(game, areas[0], 2), "暗藏者在无静止规则时提供 1 点势力（基线口径）");
        }

        // ==================== ③ 登记后：敌方暗藏者势力归零，本方暗藏者照常 ====================

        public void TestCovertPower_登记后敌方暗藏者归零()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC023_fanmierdun"), 1, areas[0]);
            OnField(game, Probe("TS_cp_enemy_char", power: 2), 2, areas[0]);
            OnField(game, Probe("TS_cp_enemy_covert"), 2, areas[0], covert: true);
            OnField(game, Probe("TS_cp_own_covert"), 1, areas[0], covert: true);

            Assert.AreEqual(2, Power(game, areas[0], 2), "敌方：正面角色 2 点计入，暗藏者的 1 点被抹掉");
            Assert.AreEqual(1, Power(game, areas[0], 1), "本方暗藏者不受影响（规则只针对『敌方』，不对称）");
        }

        // ==================== ④ 只影响势力：调查/战斗不受影响 ====================

        public void TestCovertPower_不影响调查与战斗()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC023_fanmierdun"), 1, areas[0]);
            OnField(game, Probe("TS_cp_enemy_faceup", strength: 3, investigation: 2, power: 2), 2, areas[0]);
            OnField(game, Probe("TS_cp_enemy_covert2"), 2, areas[0], covert: true);

            // 敌方（P2）侧：正面角色的三项完全计入；暗藏者被排除的只有它的 1 点势力
            Assert.AreEqual(2, AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager,
                areas[0], 2, CombatManager.CombatType.Investigation), "调查对抗不受影响（正面角色 2 点计入）");
            Assert.AreEqual(3, AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager,
                areas[0], 2, CombatManager.CombatType.Strength), "战斗对抗不受影响（正面角色 3 点计入）");
            Assert.AreEqual(2, Power(game, areas[0], 2), "势力：正面角色 2 点计入、暗藏者 1 点被排除");
        }

        // ==================== ⑤ 离场即失效 / 换区跟随 ====================

        public void TestCovertPower_离场即失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var van = OnField(game, Load("JC023_fanmierdun"), 1, areas[0]);
            OnField(game, Probe("TS_cp_x"), 2, areas[0], covert: true);
            Assert.AreEqual(0, Power(game, areas[0], 2), "前置：敌方暗藏者被排除");

            game.ZoneManager.MoveToHand(van, 1);

            Assert.AreEqual(1, Power(game, areas[0], 2), "来源离场 → 暗藏者恢复参与势力对抗");
        }

        public void TestCovertPower_换区后跟随新地区()
        {
            var game = NewGame();
            var areas = Areas(game);
            var van = OnField(game, Load("JC023_fanmierdun"), 1, areas[0]);
            OnField(game, Probe("TS_cp_y1"), 2, areas[0], covert: true);
            OnField(game, Probe("TS_cp_y2"), 2, areas[1], covert: true);

            Assert.AreEqual(0, Power(game, areas[0], 2), "原地区：被排除");
            Assert.AreEqual(1, Power(game, areas[1], 2), "其它地区：不受影响");

            game.ZoneManager.MoveToArea(van, areas[1], 1);   // 争夺区 A → B

            Assert.AreEqual(1, Power(game, areas[0], 2), "原地区恢复");
            Assert.AreEqual(0, Power(game, areas[1], 2), "新地区开始排除（按当前所在地实时判定）");
        }

        // ==================== ⑥ 端到端：势力对抗胜负真的被翻转 ====================

        public void TestCovertPower_端到端势力对抗翻转()
        {
            // 对照组（无 JC023）：P1 = 2 点，P2 = 1 点正面 + 1 点暗藏 = 2 点 → 平局 → 无人得分
            var control = NewGame();
            var cAreas = Areas(control);
            OnField(control, Probe("TS_cp_e1", power: 1), 1, cAreas[0]);
            OnField(control, Probe("TS_cp_e2", power: 1), 1, cAreas[0]);
            OnField(control, Probe("TS_cp_e3", power: 1), 2, cAreas[0]);
            OnField(control, Probe("TS_cp_e4"), 2, cAreas[0], covert: true);
            Assert.AreEqual(2, Power(control, cAreas[0], 2), "对照：P2 势力 = 1 + 暗藏 1 = 2");

            control.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Power, cAreas[0], 0, () => { });
            Assert.AreEqual(0, control.AreaManager.GetPowerMark(cAreas[0], 1), "对照：平局 → P1 不得势力标志");
            Assert.AreEqual(0, control.AreaManager.GetPowerMark(cAreas[0], 2), "对照：平局 → P2 也不得");

            // 实验组（本地区有 JC023）：P2 的暗藏不再计入 → 2 > 1 → P1 赢得势力对抗
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC023_fanmierdun"), 1, areas[0]);
            OnField(game, Probe("TS_cp_g1", power: 1), 1, areas[0]);
            OnField(game, Probe("TS_cp_g2", power: 1), 1, areas[0]);
            OnField(game, Probe("TS_cp_g3", power: 1), 2, areas[0]);
            OnField(game, Probe("TS_cp_g4"), 2, areas[0], covert: true);
            Assert.AreEqual(1, Power(game, areas[0], 2), "实验组：P2 势力 = 1（暗藏被排除）");

            game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Power, areas[0], 0, () => { });
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[0], 1),
                "势力对抗翻转：P1 以 2:1 获胜并放置 1 个势力标志");
        }
    }
}
