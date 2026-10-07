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
    /// 【P3·C】`ActionVerb.PutToArea`（把目标牌放进本次结算的目标地区）+
    /// **K15 双目标（地区 + 你墓地角色牌）**用于事务卡：JC092 死灵召唤 / JC133 操控死尸。
    /// 口径（用户 2026-09）：**照常广播进场触发**；JC133 的"额外具有不死生物"＝**叠加**类别（不替换）。
    /// </summary>
    public class P3PutToAreaTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 23 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character,
            string sub = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            if (sub != null) tmpl.SubCategories.Add(sub);
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

        /// <summary>给玩家提供费用（无色费用 + 颜色忠诚）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_pt_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① 结构 ====================

        public void TestPutToArea_JC092结构()
        {
            var tmpl = Load("JC092_silingzhaohuan");
            Assert.IsNotNull(tmpl, "JC092 模板应可加载");
            Assert.IsFalse(tmpl.LimitToActionPhaseOnly, "『行动』→ 限行动阶段 ⇒ **非快速**：不得在响应窗口打出（A档＝自己行动步骤且堆叠为空）");

            var e = FindEffect(tmpl, "JC092_1_ReviveGraveyardCharacterCovert");
            Assert.IsNotNull(e, "JC092 应有『墓地角色牌暗藏进场』效果");
            Assert.AreEqual(2, e.TargetSlots.Count, "两个槽：目标地区 + 你墓地中的角色牌");
            Assert.AreEqual(TargetKind.Area, e.TargetSlots[0].Spec.Kind, "槽0＝地区");
            Assert.IsFalse(e.TargetSlots[0].Optional, "地区必选");
            Assert.AreEqual(TargetKind.Graveyard, e.TargetSlots[1].Spec.Kind, "槽1＝墓地牌");
            Assert.AreEqual("类别:角色", e.TargetSlots[1].Spec.Filter, "只限角色牌");
            Assert.AreEqual(ActionVerb.PutToArea, e.Actions[0].Verb, "动作＝PutToArea");
            Assert.AreEqual("暗藏", e.Actions[0].Args[0], "朝向＝暗藏");
        }

        public void TestPutToArea_JC133结构()
        {
            var tmpl = Load("JC133_caokongsishi");
            Assert.IsNotNull(tmpl, "JC133 模板应可加载");
            var e = FindEffect(tmpl, "JC133_1_ReviveGraveyardCharacterFaceUp");
            Assert.IsNotNull(e, "JC133 应有『墓地角色牌正面进场』效果");
            Assert.AreEqual(2, e.TargetSlots.Count, "两个槽：目标地区 + 你墓地中的角色牌");
            Assert.AreEqual("正面", e.Actions[0].Args[0], "朝向＝正面");
            Assert.AreEqual("不死生物", e.Actions[0].Args[1], "附加子类别＝不死生物");
        }

        // ==================== ② 候选：你墓地中的角色牌（排除非本方/非法术） ====================

        public void TestPutToArea_候选为你墓地中的角色牌()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var mine = game.CardManager.CreateCardInstance(Probe("TS_pt_mine"), 1);
            game.ZoneManager.MoveToGraveyard(mine, 1);
            var myAttachment = game.CardManager.CreateCardInstance(Probe("TS_pt_att", CardCategory.Attachment), 1);
            game.ZoneManager.MoveToGraveyard(myAttachment, 1);
            var foeCard = game.CardManager.CreateCardInstance(Probe("TS_pt_foe"), 2);
            game.ZoneManager.MoveToGraveyard(foeCard, 2);

            var e = FindEffect(Load("JC092_silingzhaohuan"), "JC092_1_ReviveGraveyardCharacterCovert");
            var choices = new List<TargetChoice> { TargetChoice.Area(a2) };
            var cand = MultiTargetSlots.CollectSlot(e, 1, 1, choices, game.ZoneManager, game.CardManager,
                game.PlayerManager, null, game.AreaManager);

            Assert.IsTrue(cand.Cards.Exists(c => ReferenceEquals(c, mine)), "候选含『你墓地中的角色牌』");
            Assert.IsFalse(cand.Cards.Exists(c => ReferenceEquals(c, myAttachment)), "非角色牌不进候选（筛选 类别:角色）");
            Assert.IsFalse(cand.Cards.Exists(c => ReferenceEquals(c, foeCard)), "对手墓地的牌不进候选（Friendly）");
        }

        // ==================== ③ 端到端：JC092 暗藏进场 ====================

        /// <summary>JC092：墓地角色牌 → 以暗藏者状态进入所选地区，并**照常广播进场触发**。</summary>
        public void TestPutToArea_JC092暗藏进场()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 2, (CardColor)7);   // JC092 费用 2、颜色 7

            var graveCard = game.CardManager.CreateCardInstance(Probe("TS_pt_revive"), 1);
            game.ZoneManager.MoveToGraveyard(graveCard, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC092_silingzhaohuan"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(graveCard.UID) };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "应能打出 JC092");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, graveCard.CurrentZone, "墓地角色牌被置入争夺区");
            Assert.AreEqual(a2, graveCard.ZoneDetailId, "落点＝所选目标地区");
            Assert.IsTrue(graveCard.IsCovert, "以**暗藏者**状态进场");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡结算后进墓地");
        }

        // ==================== ④ 端到端：JC133 正面进场 + 额外子类别 ====================

        /// <summary>JC133：墓地角色牌 → 正面进入所选地区，并**额外获得**「不死生物」（叠加不替换）。</summary>
        public void TestPutToArea_JC133正面进场与额外类别()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 5, (CardColor)7);   // JC133 费用 5、颜色忠诚要求 颜色7:1

            var graveCard = game.CardManager.CreateCardInstance(Probe("TS_pt_zombie", sub: "人类"), 1);
            game.ZoneManager.MoveToGraveyard(graveCard, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC133_caokongsishi"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(graveCard.UID) };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "应能打出 JC133");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, graveCard.CurrentZone, "墓地角色牌被置入争夺区");
            Assert.AreEqual(a2, graveCard.ZoneDetailId, "落点＝所选目标地区");
            Assert.IsFalse(graveCard.IsCovert, "**正面**进场（不翻面）");
            Assert.IsTrue(graveCard.HasSubCategory("不死生物"), "额外具有『不死生物』类别");
            Assert.IsTrue(graveCard.HasSubCategory("人类"), "原类别保留（**叠加**，不替换）");
        }

        // ==================== ⑤ 目标丢失：堆叠期间离墓 → 只跳过该目标（沿用 K15 口径） ====================

        public void TestPutToArea_目标离墓则只跳过该目标()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a2 = areas[1];
            GiveAssets(game, 1, 2, (CardColor)7);

            var graveCard = game.CardManager.CreateCardInstance(Probe("TS_pt_lost"), 1);
            game.ZoneManager.MoveToGraveyard(graveCard, 1);
            var card = game.CardManager.CreateCardInstance(Load("JC092_silingzhaohuan"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var choices = new List<TargetChoice> { TargetChoice.Area(a2), TargetChoice.Card(graveCard.UID) };
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "前置：成功打出");

            // 堆叠期间该牌被移回手牌（版本号变化 → 目标丢失）
            game.ZoneManager.MoveToHand(graveCard, 1);
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Hand, graveCard.CurrentZone, "丢失的目标不被移动（仍在手牌）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡仍进墓地");
        }
    }
}
