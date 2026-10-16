using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10 实测 BUG 批】翻面 × 横置 × 封印：
    ///  ① **翻面保留横置**：`RevealCard`（背→正）/ 效果 `Latent`（正→背）/ GM `flip` 两向，
    ///     横置状态跨翻面保留；指示物等其余属性照常重置（需求①）。
    ///  ② **付费现身跨堆叠保留横置**：`RevealCovert` 走"离开场上 → 进堆叠区 → 回地区"，
    ///     全过程横置状态不变（需求②）。
    ///  ③ **封印只在宿主版本号变动时回手**：地区↔地区移动**不**回手；离场（回手牌）才回手（需求 2）。
    ///  ④ **翻面统一清空附属 + 封印**：附属进各自拥有者墓地、封印卡回各自拥有者手牌。
    /// </summary>
    public class P4FlipTappedSealTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 装配 / 工具 ====================

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 20261016 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 3 : -1,
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

        /// <summary>把一张探针卡放进指定玩家争夺区（正面）。</summary>
        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            card.ControllerId = playerId;
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        /// <summary>把一张探针卡（背面朝上，暗藏者）放进指定玩家争夺区。</summary>
        private static CardInstance OnFieldCovert(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = OnField(game, tmpl, playerId, area);
            card.SetCovert(true);
            return card;
        }

        /// <summary>把一张探针卡（附属类型）封印到宿主上，返回封印卡实例（走 `TurnManager.SealCard` 唯一出口）。</summary>
        private static CardInstance SealOnto(GameInitializer game, CardInstance host, string uid, int ownerId = 1)
        {
            var seal = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment), ownerId);
            game.TurnManager.SealCard(seal, host, faceDown: false);
            return seal;
        }

        /// <summary>结附（走生产同款单点 `ZoneManager.AttachAndRegister`），返回附属实例。</summary>
        private static CardInstance AttachTo(GameInitializer game, CardInstance host, string uid, int ownerId = 1)
        {
            var att = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment), ownerId);
            game.ZoneManager.AttachAndRegister(att, host);
            return att;
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<StackEffectInstance> pushed) { }
        }

        // ==================== ① 效果翻面 / RevealCard（背→正）保留横置 + 清附属封印 ====================

        public void TestFlip_RevealCard背到正保留横置并清附属封印()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var host = OnFieldCovert(game, Probe("TS_fs_rv_host"), 1, area);
            host.SetTapped(true);                       // 横置状态（翻面后必须保留）
            game.CardManager.AddCounter(host, "时间标志", 2);   // 其他属性（翻面后必须清零）
            var seal = SealOnto(game, host, "TS_fs_rv_seal", 1);
            var att = AttachTo(game, host, "TS_fs_rv_att", 1);

            Assert.IsTrue(host.IsSealed && host.IsCovert && host.IsTapped, "前置：暗藏 + 有封印 + 横置 + 有指示物");

            game.CardManager.RevealCard(host);          // 背→正（现身）

            Assert.IsFalse(host.IsCovert, "已翻为正面");
            Assert.IsTrue(host.IsTapped, "【需求①】翻面保留横置：角色横置翻面后仍横置");
            Assert.AreEqual(0, host.Counters.Count, "其他属性照常重置：指示物清零");
            Assert.AreEqual(CardZone.Hand, seal.CurrentZone, "【需求④】翻面：封印卡回 OWNER 手牌");
            Assert.IsNull(seal.SealedBy, "封印关系断开");
            Assert.AreEqual(CardZone.Graveyard, att.CurrentZone, "【需求④】翻面：附属进各自拥有者墓地");
            Assert.AreEqual(0, host.AttachedCards.Count, "宿主附属列表清空");
        }

        // ==================== ① 效果翻面 / Latent（正→背）保留横置 + 退封印 ====================

        public void TestFlip_Latent正到背保留横置并退封印()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            // JC054 蒙兀死亡蠕虫②：结束步骤"潜伏（将该角色翻面为暗藏者）" —— 走 EffectEngine 的 Latent 动词
            var worm = OnField(game, Load("JC054_menggusiwangruchong"), 1, area);
            var seal = SealOnto(game, worm, "TS_fs_lt_seal", 1);
            worm.SetTapped(true);

            var latent = FindEffect(worm.PlayerTemplate, "JC054_2_EndStepLatent");
            Assert.IsNotNull(latent, "JC054 应有『结束步骤潜伏』效果（ActionVerb.Latent）");
            EffectEngine.Instance.ResolveEffect(latent, worm, null, area);

            Assert.IsTrue(worm.IsCovert, "已翻为暗藏者（潜伏生效）");
            Assert.IsTrue(worm.IsTapped, "【需求①】正→背翻面同样保留横置");
            Assert.AreEqual(CardZone.Hand, seal.CurrentZone, "【需求④】翻面：封印卡回 OWNER 手牌");
        }

        // ==================== ② 付费现身：跨"离场→堆叠→回地区"保留横置 ====================

        public void TestFlip_付费现身跨堆叠保留横置()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            string area = Areas(game)[0];

            var covert = OnFieldCovert(game, Probe("TS_fs_reveal"), 1, area);
            covert.SetTapped(true);                     // 现身之前的横置状态

            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            Assert.IsTrue(tm.RevealCovert(1, covert.UID), "前置：付费现身成功（先进堆叠）");
            Assert.AreEqual(CardZone.Stack, covert.CurrentZone, "现身离开场上进入堆叠区");
            Assert.IsTrue(covert.IsTapped, "【需求②】堆叠区期间横置保留");

            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                tm.Pass(tm.PriorityPlayerId);

            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "现身结算后回到地区");
            Assert.IsTrue(covert.IsTapped, "【需求②】回到地区后横置仍保留");
        }

        // ==================== ③ 地区↔地区移动：封印**不**回手 ====================

        public void TestSeal_地区间移动封印不回手()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_fs_move_host"), 1, areas[0]);
            var seal = SealOnto(game, host, "TS_fs_move_seal", 1);
            Assert.IsTrue(host.IsSealed, "前置：宿主已被封印");

            // 争夺区 → 争夺区（机动）：不刷版本号 ⇒ 不连带脱离
            game.ZoneManager.MoveToArea(host, areas[1], 1);

            Assert.AreEqual(CardZone.SealedZone, seal.CurrentZone, "【需求2】地区间移动：封印卡**不**回手");
            Assert.IsTrue(seal.SealedBy == host, "封印关系保持");
            Assert.IsTrue(host.SealedCards.Contains(seal), "宿主封印列表保持");
            Assert.IsTrue(host.IsSealed, "宿主 IsSealed 标记保持");
            Assert.AreEqual(areas[1], host.ZoneDetailId, "宿主已移动到目标地区");
        }

        // ==================== ③ 离场：封印仍回手（回归护栏） ====================

        public void TestSeal_离场仍回封印()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var host = OnField(game, Probe("TS_fs_leave_host"), 1, area);
            var seal = SealOnto(game, host, "TS_fs_leave_seal", 1);

            game.ZoneManager.MoveToHand(host, 1);   // 离开场上 → 版本号变动

            Assert.AreEqual(CardZone.Hand, seal.CurrentZone, "【需求2】离场：封印卡回 OWNER 手牌");
            Assert.IsNull(seal.SealedBy, "封印关系断开");
            Assert.AreEqual(0, host.SealedCards.Count, "宿主封印列表清空");
            Assert.IsFalse(host.IsSealed, "宿主 IsSealed 标记清除");
        }

        // ==================== GM flip：两向保留横置 + 退封印 ====================

        public void TestFlip_GmFlip正到背保留横置并退封印()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var host = OnField(game, Probe("TS_fs_pm_host"), 1, area);
            var seal = SealOnto(game, host, "TS_fs_pm_seal", 1);
            host.SetTapped(true);

            game.PMCommandManager.ExecuteCommand($"flip {host.UID}");   // 正 → 背

            Assert.IsTrue(host.IsCovert, "flip：已翻为暗藏者");
            Assert.IsTrue(host.IsTapped, "GM flip 正→背保留横置");
            Assert.AreEqual(CardZone.Hand, seal.CurrentZone, "GM flip 退封印（回 OWNER 手牌）");

            game.PMCommandManager.ExecuteCommand($"flip {host.UID}");   // 背 → 正
            Assert.IsFalse(host.IsCovert, "flip：已翻回正面");
            Assert.IsTrue(host.IsTapped, "GM flip 背→正保留横置");
        }
    }
}
