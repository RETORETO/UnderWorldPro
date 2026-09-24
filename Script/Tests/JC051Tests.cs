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
    /// 【P4·JC051】现身触发的检索『结附』去向（2026-09 用户拍板口径）：
    /// 宿主**按被检索卡自身的 TargetRules 选定**（与"该牌从手牌打出时能否以它为目标"同一实现），
    /// 如检索到 JC012『结附于本方秘社』→ 结附**本方秘社**；
    /// **无合法宿主 → 按"无合法目标"处理**（不结附、牌回拥有者牌库，随后照常洗牌）。
    /// </summary>
    public class JC051Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 13 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
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

        /// <summary>把一张（已加载模板的）卡放进玩家1 的牌库顶。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            var deck = game.DeckManager.GetDeckPlayer1();
            deck.Insert(0, card);
            return card;
        }

        private static CardInstance CouncilOf(GameInitializer game, int playerId)
        {
            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            return (councils != null && councils.Count > 0) ? councils[0] : null;
        }

        // ==================== ① 结构 ====================

        public void TestJC051_结构_现身触发检索结附()
        {
            var tmpl = Load("JC051_nuoennagesita");
            Assert.IsNotNull(tmpl, "JC051 模板应可加载");

            EffectData e = null;
            foreach (var x in tmpl.EffectDataList)
                if (x != null && x.EffectId == "JC051_1_SearchEmblemAttach") e = x;
            Assert.IsNotNull(e, "JC051 应有『寻找徽章附属牌』效果");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e.Timing, "为现身触发");
            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.Search, e.Actions[0].Verb, "动作为检索");
            Assert.AreEqual("子类别:徽章", e.Actions[0].Args[0], "筛选=徽章附属");
            Assert.AreEqual("结附", e.Actions[0].Args[1], "去向=结附");
            Assert.AreEqual(TargetKind.None, e.Target.Kind, "检索不取目标（宿主由被检索卡自身规则决定）");
        }

        // ==================== ② 按被检索卡自身 TargetRules 选宿主 ====================

        /// <summary>检索到 JC012（徽章，结附于本方秘社）→ 结附**本方秘社**（而非地区/对手秘社）。</summary>
        public void TestJC051_检索到徽记结附本方秘社()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var emblem = PutOnDeckTop(game, Load("JC012_weimushouwanghuiji"), 1);
            var myCouncil = CouncilOf(game, 1);
            var enemyCouncil = CouncilOf(game, 2);
            Assert.IsNotNull(myCouncil, "前置：玩家1 应有秘社");
            Assert.IsNotNull(enemyCouncil, "前置：玩家2 应有秘社");

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(emblem.IsAttached, "检索到的徽记应被结附");
            Assert.IsTrue(ReferenceEquals(myCouncil, emblem.AttachedTo),
                "宿主应为**本方秘社**（JC012 自身 TargetRules=Friendly+Council）");
            Assert.AreEqual(CardZone.AttachedZone, emblem.CurrentZone, "结附后进入附属区");
            Assert.IsFalse(ReferenceEquals(emblem.AttachedTo, enemyCouncil), "不应结附到对手秘社");
        }

        /// <summary>检索到"结附于**敌方**秘社"的徽章 → 宿主应是对手秘社（宿主完全由被检索卡自身规则决定）。</summary>
        public void TestJC051_宿主由被检索卡自身规则决定_敌方秘社()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var enemyEmblem = PutOnDeckTop(game, EmblemProbe("T_jc051_enemy_emblem", TargetKind.Council, TargetRestriction.Enemy), 1);
            var myCouncil = CouncilOf(game, 1);
            var enemyCouncil = CouncilOf(game, 2);

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(ReferenceEquals(enemyCouncil, enemyEmblem.AttachedTo),
                "规则要求『敌方秘社』→ 宿主应为对手秘社");
            Assert.IsFalse(ReferenceEquals(enemyEmblem.AttachedTo, myCouncil), "不应结附到本方秘社（证明不是简单取第一个宿主）");
        }

        /// <summary>结附于角色的徽章 → 宿主为该角色（卡类目标同样走被检索卡自身规则）。</summary>
        public void TestJC051_检索到结附角色的徽章()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var myChar = game.CardManager.CreateCardInstance(Probe("T_jc051_host_char"), 1);
            game.ZoneManager.MoveToArea(myChar, area, 1);

            // 规则：本方 + 指定卡名（把宿主唯一钉在 myChar 上；且排除同为"本方角色"的 JC051 自己）
            var emblemTmpl = EmblemProbeWith("T_jc051_char_emblem", TargetKind.Character, "探针_T_jc051_host_char",
                TargetRestriction.Friendly, TargetRestriction.CardName);
            var emblem = PutOnDeckTop(game, emblemTmpl, 1);

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(ReferenceEquals(myChar, emblem.AttachedTo), "『结附于本方指定角色』→ 宿主应为该角色");
            Assert.AreEqual(CardZone.AttachedZone, emblem.CurrentZone, "进入附属区");
        }

        // ==================== ③ 无合法宿主 → 按"无合法目标"处理 ====================

        public void TestJC051_无合法宿主时按无合法目标处理()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            // 徽章要求结附于"敌方秘社上具有不存在类别的秘社"→ 场上无任何合法宿主
            var orphan = PutOnDeckTop(game, EmblemProbe("T_jc051_orphan",
                TargetKind.Council, TargetRestriction.StaticSubCategory, "不存在的类别"), 1);
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsFalse(orphan.IsAttached, "无合法宿主 → 不结附");
            Assert.IsNull(orphan.AttachedTo, "不应绑定任何宿主");
            Assert.AreEqual(CardZone.Deck, orphan.CurrentZone, "牌放回拥有者牌库（按『无合法目标』处理）");
            Assert.AreEqual(deckBefore, game.DeckManager.GetPlayer1DeckCount(),
                "检索把牌取出后无合法宿主 → 必须完整放回牌库（牌库不因此少牌）");
        }

        /// <summary>对照：有合法宿主时牌**不进**牌库（正常结附），证明上面的"回牌库"只发生在无合法宿主时。</summary>
        public void TestJC051_有合法宿主时不回牌库()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);
            var emblem = PutOnDeckTop(game, Load("JC053_mingzhongjiaopaihuiji"), 1);
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(emblem.IsAttached, "有合法宿主（本方秘社）→ 正常结附");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "结附成功后牌不再回牌库");
        }

        // ==================== 辅助 ====================

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>徽章附属探针：自带一条目标规则（决定"合理的目标"是谁）。</summary>
        private static PlayerCardData EmblemProbe(string uid, TargetKind kind, TargetRestriction r, string param = "")
            => EmblemProbeWith(uid, kind, param, r);

        /// <summary>徽章附属探针（可带多条限制，全部满足才命中 = AND）。</summary>
        private static PlayerCardData EmblemProbeWith(string uid, TargetKind kind, string param,
            params TargetRestriction[] restrictions)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Attachment,
            };
            tmpl.SubCategories.Add("徽章");
            var rule = new TargetRule { Kind = kind, Param = param };
            if (restrictions != null)
                foreach (var r in restrictions) rule.Restrictions.Add(r);
            tmpl.TargetRules = new Godot.Collections.Array<TargetRule> { rule };
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
    }
}
