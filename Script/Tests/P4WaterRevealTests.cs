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
    /// 【P3·K21·JC041 禁婆】「**快速暗藏者行动2：令禁婆由暗藏者状态现身。你只能在其位于水域地区时发动此能力。**」
    /// （用户口径 **A29** 追问答复）：
    ///  · 这是**暗藏者专用**的激活能力——"**区别于其他效果大多正面在场才能生效，这个效果只有作为暗藏者时可以激活**"
    ///    → `EffectData.RequireSourceCovert`；
    ///  · 「**水域**」是**地区子类别**（地区卡模板 `AreaCardData.SubCategories`，例 DQ001＝`["遗迹","水域"]`）
    ///    → `EffectData.RequireSourceAreaSubCategory` + `AreaManager.AreaHasSubCategory`；
    ///  · 两个前提在**候选侧**（`ActivateEligibility.MeetsSourcePrereqs`）与**权威侧**
    ///    （`EffectEngine.ActivateAbility`）**同一实现**判定；
    ///  · 效果＝`Uncover{"自身"}`（经 `CardManager.RevealCard` 收口现身，照常广播进场/现身时机）。
    /// </summary>
    public class P4WaterRevealTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 410 };
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

        /// <summary>注册一个带指定地区子类别的地区（返回其 UID）。</summary>
        private static string RegisterArea(GameInitializer game, string uid, int slot, params string[] subCategories)
        {
            var data = new AreaCardData { UID = uid, CardName = "地区_" + uid };
            foreach (var s in subCategories) data.SubCategories.Add(s);
            var inst = game.CardManager.CreateCardInstance(data, 0);
            game.AreaManager.RegisterArea(uid, slot, inst);
            return uid;
        }

        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_wr_asset_{playerId}_{i}", CardName = "探针资产", Cost = 0,
                    CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl._effectsInjected = true;
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

        private static CardInstance Covert(GameInitializer game, string area, int playerId, int markers = 0)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC041_jinpo"), playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            card.SetCovert(true);                     // 以暗藏者状态位于该地区
            return card;
        }

        /// <summary>该能力此刻是否在**候选侧**（`ActivateEligibility`）的可发动列表里。</summary>
        private static bool CanActivate(GameInitializer game, CardInstance card, EffectData ability)
        {
            var list = ActivateEligibility.GetEligibleAbilities(card.ControllerId, game.TurnManager,
                game.PlayerManager, game.CardManager);
            if (list == null) return false;
            foreach (var (src, ab) in list)
                if (ReferenceEquals(src, card) && ReferenceEquals(ab, ability)) return true;
            return false;
        }

        // ==================== ① 结构 ====================

        public void TestWater_JC041结构()
        {
            var tmpl = Load("JC041_jinpo");
            var e = FindEffect(tmpl, "JC041_A_RevealSelfFromCovertInWater");
            Assert.IsNotNull(e, "JC041 应有『由暗藏者状态现身』能力");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("快速", e.ActivateWindow, "快速行动");
            Assert.AreEqual(2, e.Cost, "快速（暗藏者）行动2");
            Assert.IsTrue(e.RequireSourceCovert, "**只有作为暗藏者时可以激活**（A29）");
            Assert.AreEqual("水域", e.RequireSourceAreaSubCategory, "只能在位于水域地区时发动");
            Assert.AreEqual(ActionVerb.Uncover, e.Actions[0].Verb, "动作＝现身");
            Assert.AreEqual("自身", e.Actions[0].Args[0], "令**自身**由暗藏者状态现身");

            // 另一条（早已交付）仍在：现身强制触发双方各弃一张
            var e2 = FindEffect(tmpl, "JC041_1_EachPlayerDiscardsOne");
            Assert.IsNotNull(e2, "『现身强制触发：每位玩家各弃一张手牌』仍在");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e2.Timing, "时机＝现身");
            Assert.IsTrue(e2.Forced, "强制触发");
        }

        // ==================== ② 位于水域的暗藏者：可发动 → 现身（不移动地区） ====================

        public void TestWater_水域暗藏者可发动并现身()
        {
            var game = NewGame();
            string water = RegisterArea(game, "AreaSlot_W1", 5, "遗迹", "水域");
            GiveMana(game, 1, 4);
            var card = Covert(game, water, 1, markers: 0);
            var ability = FindEffect(card.PlayerTemplate, "JC041_A_RevealSelfFromCovertInWater");
            game.TurnManager.PriorityPlayerId = 1;

            Assert.IsTrue(CanActivate(game, card, ability), "位于水域的暗藏者 → 可发动");
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            game.EffectEngine.ActivateAbility(card, ability, null);

            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "支付 2 费");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
        }

        // ==================== ③ 非水域地区：不可发动（候选侧 false + 权威侧拒绝） ====================

        public void TestWater_非水域不可发动()
        {
            var game = NewGame();
            string city = RegisterArea(game, "AreaSlot_C1", 6, "城市");
            GiveMana(game, 1, 4);
            var card = Covert(game, city, 1);
            var ability = FindEffect(card.PlayerTemplate, "JC041_A_RevealSelfFromCovertInWater");
            game.TurnManager.PriorityPlayerId = 1;

            Assert.IsFalse(CanActivate(game, card, ability), "**不在水域 → 不可发动**（候选侧）");
            game.EffectEngine.ActivateAbility(card, ability, null);
            Assert.IsTrue(game.StackManager.IsEmpty, "权威侧同样拒绝：未入堆叠");
            Assert.IsTrue(card.IsCovert, "仍是暗藏者（未现身）");
        }

        // ==================== ④ 正面角色（非暗藏者）：不可发动 ====================

        public void TestWater_非暗藏者不可发动()
        {
            var game = NewGame();
            string water = RegisterArea(game, "AreaSlot_W2", 7, "水域");
            GiveMana(game, 1, 4);
            var card = game.CardManager.CreateCardInstance(Load("JC041_jinpo"), 1);
            game.ZoneManager.MoveToArea(card, water, 1);      // **正面**（未暗藏）
            var ability = FindEffect(card.PlayerTemplate, "JC041_A_RevealSelfFromCovertInWater");
            game.TurnManager.PriorityPlayerId = 1;

            Assert.IsFalse(CanActivate(game, card, ability), "**正面角色 → 不可发动**（卡文『暗藏者行动』语义）");
            game.EffectEngine.ActivateAbility(card, ability, null);
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        // ==================== ⑤ 端到端：发动后经 RevealCard 现身（正面、仍在原地区） ====================

        public void TestWater_端到端现身()
        {
            var game = NewGame();
            string water = RegisterArea(game, "AreaSlot_W3", 8, "水域");
            GiveMana(game, 1, 4);
            var card = Covert(game, water, 1);
            var ability = FindEffect(card.PlayerTemplate, "JC041_A_RevealSelfFromCovertInWater");
            game.TurnManager.PriorityPlayerId = 1;

            Assert.IsTrue(CanActivate(game, card, ability), "前置：可发动");
            game.EffectEngine.ActivateAbility(card, ability, null);

            // 双方连续让过 → 结算堆叠顶
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsFalse(card.IsCovert, "**已由暗藏者状态现身**");
            Assert.AreEqual(water, card.ZoneDetailId, "现身不改变所在地区");
            Assert.AreEqual(CardZone.AreaZone, card.CurrentZone, "仍在争夺区");
        }

        // ==================== ⑥ AreaHasSubCategory 基础判定 ====================

        public void TestWater_地区子类别判定()
        {
            var game = NewGame();
            string water = RegisterArea(game, "AreaSlot_W4", 9, "遗迹", "水域");
            string city = RegisterArea(game, "AreaSlot_C4", 10, "城市");
            Assert.IsTrue(game.AreaManager.AreaHasSubCategory(water, "水域"), "水域地区命中");
            Assert.IsFalse(game.AreaManager.AreaHasSubCategory(city, "水域"), "城市地区不命中");
            Assert.IsFalse(game.AreaManager.AreaHasSubCategory("不存在的地区", "水域"), "未知地区 → false（fail-closed）");
        }
    }
}
