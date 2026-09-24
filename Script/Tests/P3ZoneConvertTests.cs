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
    /// 【P3·G 区域转换】`ActionVerb.MoveToZone{对象, 目的区, 朝向}`：
    ///  · 对象：`自身`（缺省）/`目标`/`牌库顶`（来源控制者牌库顶一张）；
    ///  · 目的区：`资产区`（缺省）/`争夺区`/`手牌`/`牌库底`；
    ///  · 进入资产区即按**区域**被视作资产（`MapInstanceToTargetKind` 按区域映射；`GetAvailableMana` 亦按资产区未横置卡数计）。
    /// 覆盖卡：**JC126 产业扩张**（牌库顶 → 你的资产区）、**JC120 庄严圣像**（赢取本地区时自身 → 资产区）。
    /// </summary>
    public class P3ZoneConvertTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 163 };
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

        public void TestZoneConvert_结构()
        {
            var ct = Load("JC126_chanyekuozhang");
            Assert.IsNotNull(ct, "JC126 模板应可加载");
            var e126 = FindEffect(ct, "JC126_1_DeckTopToAsset");
            Assert.IsNotNull(e126, "JC126 应有『牌库顶 → 资产区』效果");
            Assert.AreEqual(ActionVerb.MoveToZone, e126.Actions[0].Verb, "动作＝MoveToZone");
            Assert.AreEqual("牌库顶", e126.Actions[0].Args[0], "对象＝牌库顶");
            Assert.AreEqual("资产区", e126.Actions[0].Args[1], "目的区＝资产区");
            Assert.AreEqual("正面", e126.Actions[0].Args[2], "朝向＝正面");

            var statue = Load("JC120_zhuangyanshengxiang");
            Assert.IsNotNull(statue, "JC120 模板应可加载");
            Assert.AreEqual(TargetKind.Area, statue.TargetRules[0].Kind, "打出目标＝地区（结附于目标地区）");
            var e120 = FindEffect(statue, "JC120_A_WinAreaToAsset");
            Assert.IsNotNull(e120, "JC120 应有『赢取本地区 → 资产区』去处替代效果");
            Assert.AreEqual(EffectTiming.Static, e120.Timing, "时机＝静止替代规则（不入堆叠）");
            Assert.AreEqual("WinnerIsSelfInSameArea", e120.Condition.Predicate, "条件＝本方赢取本卡所在地区");
            Assert.AreEqual("资产区", e120.WinAreaDestination, "去处替代＝资产区");
        }

        // ==================== ② JC126：牌库顶 → 资产区（并可作为费用来源） ====================

        public void TestZoneConvert_JC126牌库顶进资产区()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Load("JC126_chanyekuozhang"), 1);

            // 直接结算效果（不经打出，聚焦"区域转换"本身）
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(deckBefore > 0, "前置：牌库非空");
            var topCard = game.TurnManager.GetDeckOf(1)[0];

            var e = FindEffect(card.PlayerTemplate, "JC126_1_DeckTopToAsset");
            EffectEngine.Instance.ResolveEffect(e, card, null, "");

            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "牌库顶那张牌已移出牌库");
            Assert.AreEqual(CardZone.AssetZone, topCard.CurrentZone, "该牌进入资产区");
            Assert.AreEqual(1, topCard.ControllerId, "归来源控制者（玩家1）");
            Assert.IsFalse(topCard.IsCovert, "正面置入（不翻面）");
            // 进入资产区即可作为费用来源 → 可用费用 +1
            Assert.AreEqual(manaBefore + 1, game.PlayerManager.GetAvailableMana(1), "新资产可作为费用来源（可用费用 +1）");
            // 按区域映射为"资产"大类
            Assert.AreEqual(TargetKind.Asset, CardManager.MapInstanceToTargetKind(topCard), "进入资产区后按区域映射为『资产』");
        }

        /// <summary>牌库为空时：无事发生（不崩、不产生幽灵卡）。</summary>
        public void TestZoneConvert_牌库为空则无事发生()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Load("JC126_chanyekuozhang"), 1);

            // 抽干玩家1 牌库
            int guard = 0;
            while (game.TurnManager.GetDeckOf(1).Count > 0 && guard++ < 300)
                game.TurnManager.DrawCardsForPlayer(2, 1);   // 用玩家2 抓牌不消耗玩家1 牌库 → 改为直接移出
            foreach (var c in new List<CardInstance>(game.TurnManager.GetDeckOf(1)))
                game.TurnManager.RemoveCardFromDeckOf(1, c);
            Assert.AreEqual(0, game.TurnManager.GetDeckOf(1).Count, "前置：玩家1 牌库已空");

            int assetsBefore = game.ZoneManager.GetZoneCards(CardZone.AssetZone, 1).Count;
            var e = FindEffect(card.PlayerTemplate, "JC126_1_DeckTopToAsset");
            EffectEngine.Instance.ResolveEffect(e, card, null, "");

            Assert.AreEqual(assetsBefore, game.ZoneManager.GetZoneCards(CardZone.AssetZone, 1).Count, "牌库为空 → 资产区无变化");
        }

        // ==================== ③ JC120：赢取本地区 → 自身进入资产区 ====================
        // 去处替代的**判定入口**口径（`EffectEngine.ResolveWinAreaDestination`）：只判"本卡 + 本地区 + 赢取者"
        // 三元组，不移动卡；实际移动由 `AreaManager.ResolveAreaWon` 的归位扫描执行（见 ③b 生产路径用例）。
        public void TestZoneConvert_JC120赢取本地区自身进资产区()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            string other = Areas(game)[1];

            var statue = game.CardManager.CreateCardInstance(Load("JC120_zhuangyanshengxiang"), 1);
            // 结附于地区：附属进入附属区，ZoneDetailId ＝ 该地区
            game.ZoneManager.MoveToAttachedZone(statue, area, 1);
            Assert.AreEqual(CardZone.AttachedZone, statue.CurrentZone, "前置：附属在场（附属区）");

            Assert.AreEqual("资产区", EffectEngine.Instance.ResolveWinAreaDestination(statue, area, 1),
                "本方赢取本卡所在地区 → 去处＝资产区");
            Assert.AreEqual("", EffectEngine.Instance.ResolveWinAreaDestination(statue, area, 2),
                "敌方赢取 → 无去处替代（照常送库底）");
            Assert.AreEqual("", EffectEngine.Instance.ResolveWinAreaDestination(statue, other, 1),
                "赢取的是别的地区 → 无去处替代（卡文限定『本地区』）");
            Assert.AreEqual(CardZone.AttachedZone, statue.CurrentZone, "判定入口不移动卡");
        }

        // ==================== ③b JC120：**走生产路径**（真的赢取地区）→ 自身进资产区 ====================
        // 上一用例是"手工结算效果"，绕过了"赢取结算 → 触发 → 送牌库底"的顺序；本用例走真实路径，
        // 用来锁定"赢取结算里送库底扫描**不能**把庄严圣像也送回牌库"（与 JC079 同类问题）。
        public void TestZoneConvert_JC120赢取生产路径()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var statue = game.CardManager.CreateCardInstance(Load("JC120_zhuangyanshengxiang"), 1);
            game.ZoneManager.MoveToAttachedZone(statue, area, 1);
            Assert.AreEqual(CardZone.AttachedZone, statue.CurrentZone, "前置：附属在场（附属区）");

            game.AreaManager.ResolveAreaWon(area, 1);   // 生产路径：赢取结算（含送牌库底扫描）

            // 让触发/堆叠跑完（若触发走堆叠）
            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AssetZone, statue.CurrentZone,
                "赢取本地区 → 庄严圣像进入**资产区**（而不是被赢取结算的送库底扫描带走）");
            Assert.AreEqual(TargetKind.Asset, CardManager.MapInstanceToTargetKind(statue), "按区域被视作资产");
            Assert.AreEqual(1, statue.OwnerId, "归属＝赢取者（本方 P1）");
            Assert.IsFalse(statue.IsCovert, "正面置入（资产区不留暗藏）");
            Assert.AreEqual(0, game.ZoneManager.GetZoneCards(CardZone.AttachedZone, 0, area).Count,
                "已离开该地区的附属区（不再结附于地区）");
        }

        /// <summary>条件谓词口径：敌方赢取同一地区 → 不满足（不移动）。</summary>
        public void TestZoneConvert_JC120敌方赢取条件不成立()        {
            var game = NewGame();
            string area = Areas(game)[0];

            var statue = game.CardManager.CreateCardInstance(Load("JC120_zhuangyanshengxiang"), 1);
            game.ZoneManager.MoveToAttachedZone(statue, area, 1);

            var e = FindEffect(statue.PlayerTemplate, "JC120_1_WinAreaMoveSelfToAsset");
            bool met = EffectEngine.Instance.EvaluateCondition("WinnerIsSelfInSameArea", statue, statue, statue,
                triggeringPlayerId: 2 /* 敌方赢取 */, triggeringAreaId: area);
            Assert.IsFalse(met, "敌方赢取 → 条件不成立");

            bool metSelf = EffectEngine.Instance.EvaluateCondition("WinnerIsSelfInSameArea", statue, statue, statue,
                triggeringPlayerId: 1, triggeringAreaId: area);
            Assert.IsTrue(metSelf, "本方赢取本地区 → 条件成立");
        }
    }
}
