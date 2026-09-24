using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K3 第一批：地区赢取时的**去处替代** → JC079 圣婴②】
    /// 卡文：公开 1分值 触发：当你赢取本地区时，将圣婴置入你的计分区，而非置入其拥有者的牌库底。
    ///
    /// 交付：`EffectData.WinAreaDestination`（`"计分区"`）+ `Condition.Predicate = WinnerIsSelfInSameArea`
    ///  · 判定入口 `EffectEngine.ResolveWinAreaDestination(card, areaUid, winnerId)`（谓词复用 `EvaluateCondition`，
    ///    与 `OnAreaWon` 触发路径**同一实现**）；
    ///  · 消费点 `AreaManager.ResolveAreaWon` 的"送牌库底"扫描（顶层卡 + 结附地区的附属两处），
    ///    与『撤回』同一实现位 → **就地改判**，不依赖入堆叠触发（送库底是同步跑完的，触发是异步结算的）。
    ///  · 数据修正：卡文写"1分值"，但卡数据里 `Score` 为 0 → 已置为 1（否则进计分区不产生任何分数）。
    /// </summary>
    public class P3WinAreaDestinationTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 631 };
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

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 2,
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

        /// <summary>第 idx 个地区的**地区卡本身**的分值（赢取时该实例也会进计分区，故断言需把它单独扣除）。</summary>
        private static int AreaCardScore(GameInitializer game, int areaIndex)
        {
            var instances = game.AreaManager.GetOrderedAreaInstances();
            return (areaIndex >= 0 && areaIndex < instances.Count) ? (instances[areaIndex]?.Template?.Score ?? 0) : 0;
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

        public void TestWinDest_JC079结构()
        {
            var tmpl = Load("JC079_shengying");
            Assert.IsNotNull(tmpl, "JC079 模板应可加载");
            var e = FindEffect(tmpl, "JC079_A_WinAreaToScorePile");
            Assert.IsNotNull(e, "JC079 应有『赢取本地区 → 计分区』效果");
            Assert.AreEqual("计分区", e.WinAreaDestination, "去处替代＝计分区");
            Assert.AreEqual("WinnerIsSelfInSameArea", e.Condition.Predicate, "条件＝本方赢取本卡所在地区");
            Assert.AreEqual(1, tmpl.Score, "卡文『1分值』→ Score=1（数据修正）");
        }

        // ==================== ② 条件：本方赢取成立、敌方赢取不成立 ====================

        public void TestWinDest_条件仅本方赢取成立()
        {
            var game = NewGame();
            var area = Areas(game)[0];
            var baby = OnField(game, Load("JC079_shengying"), 1, area);

            Assert.AreEqual("计分区", EffectEngine.Instance.ResolveWinAreaDestination(baby, area, 1),
                "本方赢取本地区 → 改判为计分区");
            Assert.AreEqual("", EffectEngine.Instance.ResolveWinAreaDestination(baby, area, 2),
                "敌方赢取 → 不改判（照常回拥有者牌库底）");
        }

        // ==================== ③ 端到端：赢取后圣婴进计分区、分数 +1、对照卡回库底 ====================

        public void TestWinDest_端到端赢取后进计分区()
        {
            var game = NewGame();
            var area = Areas(game)[0];
            var baby = OnField(game, Load("JC079_shengying"), 1, area);
            var other = OnField(game, Probe("TS_wd_other"), 1, area);
            int scoreBefore = game.PlayerManager.GetScoreTotal(1);
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int areaScore = AreaCardScore(game, 0);   // 地区卡本身也会进计分区

            game.AreaManager.ResolveAreaWon(area, 1);   // P1 赢取该地区

            Assert.AreEqual(CardZone.ScoreZone, baby.CurrentZone, "圣婴进入计分区（而非牌库底）");
            Assert.AreEqual(scoreBefore + areaScore + 1, game.PlayerManager.GetScoreTotal(1),
                "计分区总分 = 地区卡分 + 圣婴 1 分");
            Assert.AreEqual(CardZone.Deck, other.CurrentZone, "对照：普通角色照常回拥有者牌库底");
            Assert.AreEqual(deckBefore + 1, game.TurnManager.GetDeckOf(1).Count, "对照卡确实进了牌库");
        }

        // ==================== ④ 对照：敌方赢取该地区 → 圣婴回其拥有者牌库底 ====================

        public void TestWinDest_敌方赢取时回库底()
        {
            var game = NewGame();
            var area = Areas(game)[0];
            var baby = OnField(game, Load("JC079_shengying"), 1, area);
            int scoreBefore = game.PlayerManager.GetScoreTotal(2);
            int areaScore = AreaCardScore(game, 0);

            game.AreaManager.ResolveAreaWon(area, 2);   // P2 赢取

            Assert.AreEqual(CardZone.Deck, baby.CurrentZone, "敌方赢取 → 圣婴回拥有者牌库底（不改判）");
            Assert.AreEqual(scoreBefore + areaScore, game.PlayerManager.GetScoreTotal(2),
                "P2 计分区只多了地区卡本身的分，没有圣婴那 1 分");
        }

        // ==================== ⑤ 圣婴不在该地区时不影响其它地区的赢取 ====================

        public void TestWinDest_不在该地区则不参与改判()
        {
            var game = NewGame();
            var areas = Areas(game);
            var baby = OnField(game, Load("JC079_shengying"), 1, areas[1]);   // 圣婴在地区2
            int scoreBefore = game.PlayerManager.GetScoreTotal(1);
            int areaScore = AreaCardScore(game, 0);

            game.AreaManager.ResolveAreaWon(areas[0], 1);   // 赢取地区1

            Assert.AreEqual(CardZone.AreaZone, baby.CurrentZone, "圣婴仍在地区2，未被打扰");
            Assert.AreEqual(scoreBefore + areaScore, game.PlayerManager.GetScoreTotal(1),
                "地区1 的赢取未带动圣婴计分（只有地区卡的分）");
        }
    }
}
