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
    /// 【P3·K27 来源门禁 + "墓地行动"（从墓地正面打出）→ JC085 复生行尸②】
    /// 卡文：墓地行动：你可以从墓地中正面打出复生行尸，就如同其在你的手上一样。
    ///      （你仍需要支付其费用，且需满足其忠诚需求。）
    ///
    /// 交付：
    ///  · **来源门禁**（服务端权威，原先完全缺失——"只认手牌"只靠候选投影/UI 约束）：
    ///    `TurnManager.PlayCard` 默认只允许**手牌**来源；声明 `PlayerCardData.PlayableFromGraveyard`
    ///    （卡文"墓地行动"）的卡额外放行**墓地**来源；其余来源一律拒绝；
    ///  · **打出候选**：`LegalityProjection` 把墓地中声明该标志的卡列进 `PlayTargets`；
    ///  · 费用/忠诚/行动步骤等门禁照常（"就如同其在你的手上一样"）。
    /// </summary>
    public class P3GraveyardPlayTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character, int cost = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_gp_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        // ==================== ① 来源门禁：非手牌来源一律拒绝 ====================

        /// <summary>普通卡从**牌库/资产区**"打出"应被拒（此前无来源校验，可直接越权打出）。</summary>
        public void TestGraveyardPlay_非手牌来源被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)7);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            // 一张普通角色卡（未声明可墓地打出）放在**墓地**里 → 拒绝
            var plain = game.CardManager.CreateCardInstance(Probe("TS_gp_plain"), 1);
            game.ZoneManager.MoveToGraveyard(plain, 1);
            Assert.IsFalse(game.TurnManager.PlayCard(1, plain.UID), "普通卡不能从墓地打出（来源门禁）");
            Assert.AreEqual(CardZone.Graveyard, plain.CurrentZone, "卡仍在墓地");

            // 放在**牌库**里 → 拒绝
            var inDeck = game.CardManager.CreateCardInstance(Probe("TS_gp_deck"), 1);
            game.ZoneManager.MoveToDeckBottom(inDeck, 1);
            Assert.IsFalse(game.TurnManager.PlayCard(1, inDeck.UID), "不能从牌库打出");
            Assert.AreEqual(CardZone.Deck, inDeck.CurrentZone, "卡仍在牌库");
        }

        // ==================== ② 墓地行动：JC085 可从墓地正面打出 ====================

        public void TestGraveyardPlay_JC085可从墓地打出()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)7);        // JC085 颜色 7
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);
            Assert.IsTrue(zombie.PlayerTemplate.PlayableFromGraveyard, "JC085 声明了『墓地行动』");

            Assert.IsTrue(game.TurnManager.PlayCard(1, zombie.UID), "墓地行动：可从墓地正面打出");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, zombie.CurrentZone, "结算后进入争夺区（站场）");
            Assert.IsFalse(zombie.IsCovert, "**正面**打出（不是暗藏进场）");
        }

        /// <summary>费用照常支付（卡文"你仍需要支付其费用"）。</summary>
        public void TestGraveyardPlay_费用照常支付()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)7);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            int cost = zombie.PlayerTemplate.Cost;

            Assert.IsTrue(game.TurnManager.PlayCard(1, zombie.UID));
            Assert.AreEqual(manaBefore - cost, game.PlayerManager.GetAvailableMana(1), $"支付了印刷费用 {cost}");
        }

        /// <summary>费用不足 → 拒绝（与手牌同款门禁）。</summary>
        public void TestGraveyardPlay_费用不足被拒()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, zombie.UID), "无资产 → 付不起费用 → 拒绝");
            Assert.AreEqual(CardZone.Graveyard, zombie.CurrentZone, "卡仍在墓地");
        }

        // ==================== ③ 打出候选：墓地中的"墓地行动"卡也进投影 ====================

        public void TestGraveyardPlay_打出候选包含墓地行动卡()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3, (CardColor)7);

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_gp_plain2"), 1);
            game.ZoneManager.MoveToGraveyard(plain, 1);
            var inHand = game.CardManager.CreateCardInstance(Probe("TS_gp_hand"), 1);
            game.ZoneManager.MoveToHand(inHand, 1);

            var snap = LegalityProjection.Build(game.CardManager, game.PlayerManager, game.TurnManager,
                game.ZoneManager, 1);

            bool hasZombie = false, hasPlain = false, hasHand = false;
            foreach (var p in snap.PlayTargets)
            {
                if (p.SourceUid == zombie.UID) hasZombie = true;
                if (p.SourceUid == plain.UID) hasPlain = true;
                if (p.SourceUid == inHand.UID) hasHand = true;
            }
            Assert.IsTrue(hasZombie, "墓地中的『墓地行动』卡进了打出候选");
            Assert.IsFalse(hasPlain, "墓地中的普通卡**不**进打出候选");
            Assert.IsTrue(hasHand, "手牌卡照常在候选里");
        }

        // ==================== ④ 手牌打出不受影响（回归保护） ====================

        public void TestGraveyardPlay_手牌打出照常()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)7);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToHand(zombie, 1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, zombie.UID), "从**手牌**打出照常允许");
            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.AreaZone, zombie.CurrentZone, "结算后进入争夺区");
        }
    }
}
