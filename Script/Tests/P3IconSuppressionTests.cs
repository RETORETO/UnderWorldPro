using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K20：**持续型"失去图标"** → JC123 饮魂者朱迪②】
    /// 卡文：持续：只要饮魂者朱迪在场，所有具有锁定标记的角色或暗藏者失去所有［黑星］和［白星］能力图标。
    ///
    /// 交付：`EffectData.IconSuppression`（`IconSuppressionSpec{Icons, RequireCounters}`）
    ///  · 应用/还原＝`EffectEngine.ReapplyIconSuppressions()` **统一重算**：先按记录**精确还原**上一次抑制，
    ///    再按当前局面重算受影响集合（幂等、可反复调用）；
    ///  · 抑制量＝该图标的**当前有效贡献**（`势力` 取 `CurrentPower`；`先手势力` 取授予的『先手势力』）；
    ///  · 触点点（来源增减 / 受影响卡增减标志 / 进出场）都已接线 → **标志消失、来源离场即恢复**；
    ///  · 卡库里"锁定标记/锁定标志"是同一概念的两种写法 → 两种都登记（等效）。
  /// </summary>
    public class P3IconSuppressionTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1153 };
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

        private static PlayerCardData Probe(string uid, int power = 0, int firstStrikePower = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5,
                Power = power, FirstStrikePower = firstStrikePower,
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

        private static int PowerOf(GameInitializer game, string area, int playerId)
            => AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager, area, playerId,
                CombatManager.CombatType.Power);

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

        public void TestSuppress_JC123结构()
        {
            var tmpl = Load("JC123_yinhunzhezhudi");
            Assert.IsNotNull(tmpl, "JC123 模板应可加载");
            var e = FindEffect(tmpl, "JC123_2_LockedLoseStarIcons");
            Assert.IsNotNull(e, "JC123 应有『具锁定标记者失去黑星白星』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.IconSuppression, "声明了持续失去图标");
            Assert.AreEqual(2, e.IconSuppression.Icons.Count, "失去黑星 + 白星");
            Assert.AreEqual(2, e.IconSuppression.RequireCounters.Count, "锁定标记/锁定标志两种写法都认");
        }

        // ==================== ② 具标志者失去黑星（印刷势力被抹掉） ====================

        public void TestSuppress_具标志者失去黑星()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC123_yinhunzhezhudi"), 1, areas[0]);
            var marked = OnField(game, Probe("TS_is_marked", power: 2), 2, areas[0]);
            var plain = OnField(game, Probe("TS_is_plain", power: 2), 2, areas[0]);

            game.CardManager.AddCounter(marked, "锁定标记");   // 加标志 → 自动重算

            Assert.AreEqual(0, marked.CurrentPower, "具锁定标记者失去黑星（势力 → 0）");
            Assert.AreEqual(2, plain.CurrentPower, "无标记者不受影响");
        }

        // ==================== ③ 白星（先手势力，含授予）同样失去 ====================

        public void TestSuppress_白星同样失去()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC123_yinhunzhezhudi"), 1, areas[0]);
            var marked = OnField(game, Probe("TS_is_white", power: 1, firstStrikePower: 2), 2, areas[0]);
            marked.Grant("Attribute", "先手势力", 1);

            game.CardManager.AddCounter(marked, "锁定标志");   // 另一种写法，同样应命中

            Assert.AreEqual(0, marked.CurrentPower, "黑星（含先手时的印刷白星）被抹掉");
            Assert.AreEqual(0, marked.GetAttribute("先手势力"), "授予的白星也被抹掉");
            Assert.AreEqual(0, PowerOf(game, areas[0], 2), "该玩家在本地区的势力合计 = 0");
        }

        // ==================== ④ 标志被移除 → 恢复 ====================

        public void TestSuppress_标志移除后恢复()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC123_yinhunzhezhudi"), 1, areas[0]);
            var marked = OnField(game, Probe("TS_is_recover", power: 2), 2, areas[0]);
            game.CardManager.AddCounter(marked, "锁定标记");
            Assert.AreEqual(0, marked.CurrentPower, "前置：被抑制");

            game.CardManager.RemoveCounter(marked, "锁定标记");

            Assert.AreEqual(2, marked.CurrentPower, "标志移除 → 黑星恢复（重算已接线）");
        }

        // ==================== ⑤ 来源离场 → 恢复 ====================

        public void TestSuppress_来源离场后恢复()
        {
            var game = NewGame();
            var areas = Areas(game);
            var judy = OnField(game, Load("JC123_yinhunzhezhudi"), 1, areas[0]);
            var marked = OnField(game, Probe("TS_is_leave", power: 3), 2, areas[0]);
            game.CardManager.AddCounter(marked, "锁定标记");
            Assert.AreEqual(0, marked.CurrentPower, "前置：被抑制");

            game.ZoneManager.MoveToHand(judy, 1);   // 来源离场 → 注销并重算

            Assert.AreEqual(3, marked.CurrentPower, "来源离场 → 黑星恢复");
        }

        // ==================== ⑥ 重算幂等：反复调用不会越抹越多 ====================

        public void TestSuppress_重算幂等()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC123_yinhunzhezhudi"), 1, areas[0]);
            var marked = OnField(game, Probe("TS_is_idem", power: 4), 2, areas[0]);
            game.CardManager.AddCounter(marked, "锁定标记");

            EffectEngine.Instance.ReapplyIconSuppressions();
            EffectEngine.Instance.ReapplyIconSuppressions();

            Assert.AreEqual(0, marked.CurrentPower, "反复重算仍是 0（不会重复扣成负数）");

            game.CardManager.RemoveCounter(marked, "锁定标记");
            EffectEngine.Instance.ReapplyIconSuppressions();

            Assert.AreEqual(4, marked.CurrentPower, "还原也精确（回到 4，不多不少）");
        }
    }
}
