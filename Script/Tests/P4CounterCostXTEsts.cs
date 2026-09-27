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
    /// 【K5·JC055②】安妮斯朵拉「**行动：自本方卡牌上移除 X 个时间标志：本回合中，你打出的下一张灾难牌的
    /// 所需费用减少 X**」（用户口径 **A17/A30**）：
    ///  · 代价：`CounterCostSpec.AmountByPlayerChoice=true` → **X 由玩家逐卡分配**（"点一张卡记录用 1 点 → 确认"），
    ///    **X ≥ 1（不允许为 0）**、上限＝本方场上该标志总数、**入堆叠前支付**、
    ///    无候选/总和为 0/指定卡非法 → **拒绝发动且无副作用**；
    ///  · 支付的数量作为 X：`ValueExpr="代价数量"`（经 `StackEntry.EventAmount` 下发）+ 动作参数 `$X`；
    ///  · 减费：`Gain{"费用减少","$X","子类别:灾难","一次性","打出,现身"}` + `Duration="直到回合结束"`
    ///    → **一次性**（"下一张"语义，用后消费移除、未用则回合结束流失），且
    ///    **打出通道与现身（暗藏者付费翻面）通道都能享受**（用户口径：不仅限正面打出）。
    /// </summary>
    public class P4CounterCostXTEsts : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 552 };
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

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, CardCategory category, int cost = 0,
            CardColor? color = null, params string[] subCategories)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
            };
            if (color.HasValue) tmpl.Colors.Add(color.Value);
            foreach (var s in subCategories) tmpl.SubCategories.Add(s);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>给玩家放 n 张红（Color 8）资产 → 满足 JC055 的忠诚(红 2)与费用。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color = CardColor.Red)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_k5x_asset_{playerId}_{i}", CardCategory.Attachment, color: color), playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId,
            string area, int markers = 0, string markerName = "时间标志")
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            for (int i = 0; i < markers; i++) game.CardManager.AddCounter(card, markerName);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void ReadyToActivate(GameInitializer game, int playerId)
        {
            game.TurnManager.PriorityPlayerId = playerId;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶（激活效果在**结算时**才登记减费）。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① 结构 ====================

        public void TestK5X_JC055结构()
        {
            var tmpl = Load("JC055_annisiduola");
            var e = FindEffect(tmpl, "JC055_B_RemoveMarkersReduceDisasterCost");
            Assert.IsNotNull(e, "JC055 应有『移除 X 个时间标志 → 减费』效果");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("行动", e.ActivateWindow, "卡文『行动』");
            Assert.AreEqual("代价数量", e.ValueExpr, "X ＝本次已支付的标志代价数量");
            Assert.IsNotNull(e.CounterCost, "声明了标志代价");
            Assert.IsTrue(e.CounterCost.AmountByPlayerChoice, "X 由玩家选择（非固定数量）");
            Assert.AreEqual(2, e.CounterCost.Markers.Count, "时间标志/时间标记两种写法都认");

            var gain = e.Actions[0];
            Assert.AreEqual(ActionVerb.Gain, gain.Verb, "动作＝Gain 费用减少");
            Assert.AreEqual("费用减少", gain.Args[0], "授予类型＝费用减少");
            Assert.AreEqual("$X", gain.Args[1], "减免额＝X（动态占位符）");
            Assert.AreEqual("子类别:灾难", gain.Args[2], "只对『灾难』子类别的牌生效");
            Assert.AreEqual("一次性", gain.Args[3], "『下一张』语义＝一次性");
            Assert.AreEqual("打出,现身", gain.Args[4], "打出与现身两个通道都能享受（用户口径）");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝本回合");
        }

        // ==================== ② 代价：逐卡分配 → 扣标志（X≥1） ====================

        public void TestK5X_分配移除标志并下发X()
        {
            var game = NewGame();
            GiveAssets(game, 1, 4);
            string area = FirstArea(game);
            var annie = OnField(game, Load("JC055_annisiduola"), 1, area);
            var carrier = OnField(game, Probe("TS_k5x_carrier", CardCategory.Character), 1, area, markers: 3);
            var ability = FindEffect(annie.PlayerTemplate, "JC055_B_RemoveMarkersReduceDisasterCost");
            ReadyToActivate(game, 1);

            var cands = game.EffectEngine.GetCounterCostCandidates(annie, ability.CounterCost);
            Assert.AreEqual(1, cands.Count, "候选＝本方场上带时间标志的卡（安妮斯朵拉自身无标志）");
            Assert.AreEqual(3, game.EffectEngine.GetCounterCostAvailableTotal(annie, ability.CounterCost),
                "可移除总数＝3");

            var allocation = new System.Collections.Generic.Dictionary<string, int> { { carrier.UID, 2 } };
            game.EffectEngine.ActivateAbility(annie, ability, null, -1, null, null, null, allocation);

            Assert.AreEqual(1, carrier.Counters["时间标志"], "分配 2 个 → 从 3 减到 1");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠（代价在入堆叠前支付）");
            var entry = game.StackManager.Top;
            Assert.AreEqual(2, entry.EventAmount, "支付的 X＝2 随条目下发（供『代价数量』读取）");
        }

        // ==================== ③ X=0（不分配）→ 拒绝发动 ====================

        public void TestK5X_不分配则拒绝发动()
        {
            var game = NewGame();
            GiveAssets(game, 1, 4);
            string area = FirstArea(game);
            var annie = OnField(game, Load("JC055_annisiduola"), 1, area);
            var carrier = OnField(game, Probe("TS_k5x_carrier2", CardCategory.Character), 1, area, markers: 2);
            var ability = FindEffect(annie.PlayerTemplate, "JC055_B_RemoveMarkersReduceDisasterCost");
            ReadyToActivate(game, 1);

            game.EffectEngine.ActivateAbility(annie, ability, null, -1, null, null, null,
                new System.Collections.Generic.Dictionary<string, int> { { carrier.UID, 0 } });   // 总和 0

            Assert.AreEqual(2, carrier.Counters["时间标志"], "未移除任何标志");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        public void TestK5X_无候选则拒绝发动()
        {
            var game = NewGame();
            GiveAssets(game, 1, 4);
            string area = FirstArea(game);
            var annie = OnField(game, Load("JC055_annisiduola"), 1, area);
            var plain = OnField(game, Probe("TS_k5x_plain", CardCategory.Character), 1, area);  // 无标志
            var ability = FindEffect(annie.PlayerTemplate, "JC055_B_RemoveMarkersReduceDisasterCost");
            ReadyToActivate(game, 1);

            game.EffectEngine.ActivateAbility(annie, ability, null, -1, null, null, null,
                new System.Collections.Generic.Dictionary<string, int> { { plain.UID, 1 } });

            Assert.IsTrue(game.StackManager.IsEmpty, "无合法候选 → 拒绝发动（不产生副作用）");
        }

        public void TestK5X_分配超出持有量被拒()
        {
            var game = NewGame();
            GiveAssets(game, 1, 4);
            string area = FirstArea(game);
            var annie = OnField(game, Load("JC055_annisiduola"), 1, area);
            var carrier = OnField(game, Probe("TS_k5x_carrier3", CardCategory.Character), 1, area, markers: 1);
            var ability = FindEffect(annie.PlayerTemplate, "JC055_B_RemoveMarkersReduceDisasterCost");
            ReadyToActivate(game, 1);

            game.EffectEngine.ActivateAbility(annie, ability, null, -1, null, null, null,
                new System.Collections.Generic.Dictionary<string, int> { { carrier.UID, 3 } });   // 只有 1 个

            Assert.AreEqual(1, carrier.Counters["时间标志"], "fail-closed：一个都没扣");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        // ==================== ④ 减费生效：下一张灾难牌 -X（打出通道） ====================

        public void TestK5X_下一张灾难牌减费()
        {
            var game = NewGame();
            GiveAssets(game, 1, 8);
            string area = FirstArea(game);
            var annie = OnField(game, Load("JC055_annisiduola"), 1, area);
            var carrier = OnField(game, Probe("TS_k5x_carrier4", CardCategory.Character), 1, area, markers: 3);
            var ability = FindEffect(annie.PlayerTemplate, "JC055_B_RemoveMarkersReduceDisasterCost");
            ReadyToActivate(game, 1);

            game.EffectEngine.ActivateAbility(annie, ability, null, -1, null, null, null,
                new System.Collections.Generic.Dictionary<string, int> { { carrier.UID, 3 } });
            Assert.IsFalse(game.StackManager.IsEmpty, "前置：已发动");
            ResolveStackOnce(game);   // 激活效果在结算时才登记减费
            Assert.IsTrue(game.StackManager.IsEmpty, "堆叠已结算");

            // 本体减费查询（打出通道）
            var disaster = game.CardManager.CreateCardInstance(
                Probe("TS_k5x_disaster", CardCategory.Sorcery, cost: 4, subCategories: "灾难"), 1);
            Assert.AreEqual(3, game.EffectEngine.GetCostModification(1, disaster, disaster.PlayerTemplate.CardName,
                disaster.PlayerTemplate.Organization, "打出"), "灾难牌（打出）减免 X=3");

            // 非灾难牌不受影响
            var plain = game.CardManager.CreateCardInstance(
                Probe("TS_k5x_plaincard", CardCategory.Sorcery, cost: 4), 1);
            Assert.AreEqual(0, game.EffectEngine.GetCostModification(1, plain, plain.PlayerTemplate.CardName,
                plain.PlayerTemplate.Organization, "打出"), "非灾难牌不减免");

            // 【用户口径】现身（付费翻面）通道同样享受
            Assert.AreEqual(3, game.EffectEngine.GetCostModification(1, disaster, disaster.PlayerTemplate.CardName,
                disaster.PlayerTemplate.Organization, "现身"), "**现身通道同样减免**（不仅限正面打出）");

            // 一次性：消费后不再减免
            game.EffectEngine.ConsumeCostModification(1, disaster, disaster.PlayerTemplate.Organization, "打出");
            Assert.AreEqual(0, game.EffectEngine.GetCostModification(1, disaster, disaster.PlayerTemplate.CardName,
                disaster.PlayerTemplate.Organization, "打出"), "一次性消费后失效（『下一张』语义）");
        }
    }
}
