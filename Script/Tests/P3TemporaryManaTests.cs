using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K19】临时费用池（JC115 备用资金「快速行动，横置：你获得1」）。
    /// 口径（用户 2026-09，§7.4b A26）：
    ///  · 本作**没有"有色费用"**——所有费用都是**无色费用**；
    ///  · 临时费用**支付时优先花费**（先花临时、不足再横置资产）；
    ///  · **任意步骤结束时清除**（六个步骤的每一次阶段流转＝上一步骤结束）；
    ///  · 可用费用（`GetAvailableMana`）＝未横置资产数 + 临时费用。
    /// </summary>
    public class P3TemporaryManaTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 115 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Attachment)
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

        /// <summary>给玩家 n 张资产卡（可用费用来源）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(Probe($"TS_tm_asset_{playerId}_{i}"), playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static int UntappedAssets(GameInitializer game, int playerId)
        {
            int n = 0;
            foreach (var c in game.ZoneManager.GetZoneCards(CardZone.AssetZone, playerId))
                if (!c.IsTapped) n++;
            return n;
        }

        // ==================== ① 结构：JC115 用"横置自身 + 获得临时费用" ====================

        public void TestTempMana_JC115结构()
        {
            var tmpl = Load("JC115_beiyongzijin");
            Assert.IsNotNull(tmpl, "JC115 模板应可加载");
            var e = FindEffect(tmpl, "JC115_A_TapForTemporaryMana");
            Assert.IsNotNull(e, "JC115 应有『横置 → 获得1』效果");
            Assert.AreEqual(EffectTiming.AttachmentActivated, e.Timing, "时机＝附属激活");
            Assert.AreEqual("快速", e.ActivateWindow, "卡文『快速行动』");
            Assert.AreEqual("横置本角色", e.ActivateCondition, "代价＝横置本卡");
            Assert.AreEqual(ActionVerb.GrantTemporaryMana, e.Actions[0].Verb, "动作＝获得临时费用");
            Assert.AreEqual("1", e.Actions[0].Args[0], "数量＝1");
        }

        // ==================== ② 结算效果 → 可用费用 +1 ====================

        public void TestTempMana_获得1点_可用费用增加()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Load("JC115_beiyongzijin"), 1);
            var host = game.CardManager.CreateCardInstance(Probe("TS_tm_host"), 1);
            game.ZoneManager.MoveToArea(host, game.AreaManager.GetOrderedAreaUids()[0], 1);
            game.ZoneManager.MoveToAttachedZone(card, host.ZoneDetailId, 1);

            int before = game.PlayerManager.GetAvailableMana(1);
            var e = FindEffect(card.PlayerTemplate, "JC115_A_TapForTemporaryMana");
            EffectEngine.Instance.ResolveEffect(e, card, null, "");

            Assert.AreEqual(1, game.PlayerManager.GetTemporaryMana(1), "获得 1 点临时费用");
            Assert.AreEqual(before + 1, game.PlayerManager.GetAvailableMana(1), "可用费用 +1");
        }

        // ==================== ③ 支付时优先花临时费用（资产保持未横置） ====================

        public void TestTempMana_支付优先花临时费用()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3);
            int assetsBefore = UntappedAssets(game, 1);
            game.PlayerManager.GrantTemporaryMana(1, 2);

            Assert.IsTrue(game.PlayerManager.SpendMana(1, 2), "支付 2（全部由临时费用承担）");

            Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(1), "临时费用被优先花光");
            Assert.AreEqual(assetsBefore, UntappedAssets(game, 1), "资产卡一张都没被横置");

            // 再用：临时费用已空 → 这回横置资产
            Assert.IsTrue(game.PlayerManager.SpendMana(1, 1), "再支付 1");
            Assert.AreEqual(assetsBefore - 1, UntappedAssets(game, 1), "不足部分横置资产");

            // 混合支付：1 张资产 + 1 点临时 → 只横置 1 张
            game.PlayerManager.GrantTemporaryMana(1, 1);
            Assert.IsTrue(game.PlayerManager.SpendMana(1, 2), "混合支付 2");
            Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(1), "临时费用优先用完");
            Assert.AreEqual(assetsBefore - 2, UntappedAssets(game, 1), "只额外横置 1 张资产");
        }

        // ==================== ④ 步骤结束清除临时费用 ====================

        public void TestTempMana_步骤结束时清除()
        {
            var game = NewGame();
            GiveAssets(game, 1, 2);
            game.PlayerManager.GrantTemporaryMana(1, 3);
            game.PlayerManager.GrantTemporaryMana(2, 1);
            Assert.AreEqual(3, game.PlayerManager.GetTemporaryMana(1), "前置：P1 有 3 点");

            // 任意步骤结束＝一次阶段流转（`TurnManager.EnterPhase`）：双方连续让过 → 推进阶段（与既有测试同一驱动方式）
            var phaseBefore = game.TurnManager.CurrentPhase;
            for (int i = 0; i < 4 && game.TurnManager.CurrentPhase == phaseBefore; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Assert.IsTrue(game.TurnManager.CurrentPhase != phaseBefore, "前置：已推进到下一阶段（＝上一步骤结束）");

            Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(1), "步骤结束 → P1 临时费用清除");
            Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(2), "步骤结束 → P2 临时费用一并清除（双方都清）");
            Assert.AreEqual(UntappedAssets(game, 1), game.PlayerManager.GetAvailableMana(1), "可用费用回到＝未横置资产数");
        }

        // ==================== ⑤ 费用不足时仍拒绝（临时费用不能凭空支付） ====================

        public void TestTempMana_不足仍拒绝()
        {
            var game = NewGame();
            game.PlayerManager.GrantTemporaryMana(1, 2);
            Assert.IsTrue(game.PlayerManager.SpendMana(1, 2), "2 点临时费用可支付 2");
            Assert.IsFalse(game.PlayerManager.SpendMana(1, 1), "临时费用已花光且无资产 → 拒绝支付");
        }
    }
}
