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
    /// 【2026-09·通用红线】"效果只在**场上（正面）**提供"回归（用户口径 A60/A61）：
    ///
    /// 用户原话要点：「效果除非明确描述了在其他地方生效，比如暗藏者触发、墓地行动等，
    /// 才能在不是场上的情况触发；其他情况都只能在场上触发。」
    ///
    /// 推论（与既有 D27「暗藏者的持续/静止效果不生效」同一条）：
    ///  · **背面朝上（暗藏者）的卡不提供任何效果** —— 关键字/词条、印刷静止规则、去处替代、激活能力……
    ///    一律休眠；要"由暗藏者发动"必须由卡文**显式声明**（`RequireSourceCovert` = 暗藏者专用激活，
    ///    如 JC041）；
    ///  · **不在场上**（手牌/牌库/墓地/封印区/堆叠）的卡同样不默认提供效果 ——
    ///    跨区生效必须显式声明（`ZoneScope.HandZone` 手牌行动 / `Graveyard` 墓地行动 /
    ///    `PlayableFromGraveyard` / 构筑级触发等）。
    ///
    /// 本套件锁定本轮排查出的**六处"读卡面/词条但缺在场或正面门禁"的判定点**：
    ///  ① 声望计数（`RenownRule`）② 撤回（`AreaManager`）③ 灵体（`DamageRules`）
    ///  ④ 屏障与关键字目标筛选（`TargetValidity`）⑤ JC121 印刷"不能移动"（`CardManager`，改按"在场"判定）
    ///  ⑥ 赢取时的去处替代（`EffectEngine.ResolveWinAreaDestination`）+ 激活权威门禁（`ActivateAbility`）。
    /// </summary>
    public class P3FieldZoneGateTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 20260921 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 4 : -1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>带**卡面固有词条**的探针（走 `HasInherentKeyword`，不依赖持续效果注册时机）。</summary>
        private static PlayerCardData KeywordProbe(string uid, string keyword,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = Probe(uid, category);
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_cont",
                Timing = EffectTiming.Continuous,
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = keyword, Amount = 1 },
                },
            });
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        /// <summary>以**暗藏者（背面朝上）**状态进入争夺区（与"派遣暗藏者"同序：先翻面、再移动）。</summary>
        private static CardInstance OnFieldCovert(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            card.SetCovert(true);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        // ==================== ① 声望：暗藏者不算"声望角色" ====================

        public void TestZoneGate_暗藏者不提供声望()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, KeywordProbe("TS_zg_ren_f", "声望"), 1, area);
            var covert = OnFieldCovert(game, KeywordProbe("TS_zg_ren_c", "声望"), 1, area);
            var enemyFaceUp = OnField(game, KeywordProbe("TS_zg_ren_e", "声望"), 2, area);

            Assert.IsTrue(faceUp.HasActiveKeyword("声望"), "正面声望角色 ⇒ 提供词条");
            Assert.IsFalse(covert.HasActiveKeyword("声望"), "**背面（暗藏）不提供词条**");

            Assert.AreEqual(1, RenownRule.CountRenownCharacters(game.ZoneManager, area, 1),
                "P1 的声望角色数只算正面那一张（暗藏不计）");
            Assert.AreEqual(1, RenownRule.CountRenownCharacters(game.ZoneManager, area, 2), "P2 同样 1 张");

            // 严格多于才 +1：1:1 平 → 不放置（修复前暗藏被计入 = 2:1 → 会误放标志）
            int placed = RenownRule.ApplyIfMajority(game.ZoneManager, game.AreaManager, area);
            Assert.AreEqual(0, placed, "双方声望角色相等（暗藏不计入）→ 不放势力标志");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(area, 1), "未误放标志");
        }

        // ==================== ② 撤回：暗藏者不被"救回手牌" ====================

        public void TestZoneGate_暗藏者不提供撤回()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, KeywordProbe("TS_zg_wd_f", "撤回"), 1, area);
            var covert = OnFieldCovert(game, KeywordProbe("TS_zg_wd_c", "撤回"), 1, area);

            game.AreaManager.ResolveAreaWon(area, 2);   // 对手赢取该地区 → 归位扫描

            Assert.AreEqual(CardZone.Hand, faceUp.CurrentZone, "正面【撤回】卡回 OWNER 手牌");
            Assert.AreEqual(CardZone.Deck, covert.CurrentZone,
                "**暗藏卡不提供【撤回】** ⇒ 按常规回牌库底（修复前会被误判回手牌）");
        }

        // ==================== ③ 灵体：暗藏者不算灵体 ====================

        public void TestZoneGate_暗藏者不提供灵体()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, KeywordProbe("TS_zg_sp_f", "灵体"), 1, area);
            var covert = OnFieldCovert(game, KeywordProbe("TS_zg_sp_c", "灵体"), 1, area);

            Assert.IsTrue(DamageRules.IsSpirit(faceUp), "正面灵体 ⇒ 提供词条");
            Assert.IsFalse(DamageRules.IsSpirit(covert), "背面（暗藏）灵体 ⇒ 休眠");
        }

        // ==================== ④ 屏障 / 关键字目标筛选：暗藏者不提供 ====================

        public void TestZoneGate_暗藏者不提供屏障()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, KeywordProbe("TS_zg_bar_f", "屏障"), 2, area);
            var covert = OnFieldCovert(game, KeywordProbe("TS_zg_bar_c", "屏障"), 2, area);

            Assert.IsTrue(TargetValidity.IsBarrierBlocking(faceUp, 1), "正面屏障卡不能被敌方来源指定");
            Assert.IsFalse(TargetValidity.IsBarrierBlocking(covert, 1),
                "**暗藏卡不提供【屏障】** ⇒ 不拦（修复前读卡面固有 → 误拦）");
        }

        public void TestZoneGate_暗藏者不被关键字目标筛选选中()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, KeywordProbe("TS_zg_kw_f", "灵体"), 2, area);
            var covert = OnFieldCovert(game, KeywordProbe("TS_zg_kw_c", "灵体"), 2, area);
            var restrictions = new List<TargetRestriction> { TargetRestriction.HasKeyword };

            Assert.IsTrue(TargetValidity.CheckRestrictions(restrictions, "灵体", faceUp, 1),
                "正面灵体满足『关键字:灵体』目标限制");
            Assert.IsFalse(TargetValidity.CheckRestrictions(restrictions, "灵体", covert, 1),
                "**暗藏卡不提供词条** ⇒ 不满足『关键字:灵体』（修复前按卡面固有 → 误判满足）");
        }

        // ==================== ⑤ JC121 印刷「不能移动」：只在场上生效 ====================

        /// <summary>对照：在场上（未结附）时，仍然不能移动（既有口径 A18/A19 不受本批影响）。</summary>
        public void TestZoneGate_JC121_在场未结附时仍不能移动()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(guard, areas[0], 1);

            game.CardManager.MoveCardToArea(guard, areas[1]);
            Assert.AreEqual(areas[0], guard.ZoneDetailId, "在场未结附 → 移动仍被拒绝");
        }

        /// <summary>【本轮修复】印刷规则**只在场上提供**：卡在墓地/手牌时被"放置进场"不受"不能移动"拦截。</summary>
        public void TestZoneGate_JC121_不在场上时不拦放置进场()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var guard = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToGraveyard(guard, 1);
            Assert.AreEqual(CardZone.Graveyard, guard.CurrentZone, "前置：该卡在墓地（不在场上）");
            Assert.IsFalse(guard.IsExertingEffects, "不在场上 ⇒ 其印刷规则休眠");

            game.CardManager.MoveCardToArea(guard, areas[0]);   // 效果把它"放置进场"

            Assert.AreEqual(CardZone.AreaZone, guard.CurrentZone,
                "**卡不在场上时其印刷『不能移动』不生效** ⇒ 放置进场照常（修复前会被误拦）");
        }

        // ==================== ⑥a 赢取去处替代：暗藏卡不提供 ====================

        public void TestZoneGate_暗藏卡的去处替代不生效()
        {
            var game = NewGame(1);
            string area = FirstArea(game);

            var faceUp = OnField(game, Load("JC079_shengying"), 1, area);
            var covert = OnFieldCovert(game, Load("JC079_shengying"), 1, area);

            Assert.AreEqual("计分区", EffectEngine.Instance.ResolveWinAreaDestination(faceUp, area, 1),
                "正面圣婴 → 去处替代＝计分区");
            Assert.AreEqual("", EffectEngine.Instance.ResolveWinAreaDestination(covert, area, 1),
                "**暗藏的圣婴不提供去处替代**");

            game.AreaManager.ResolveAreaWon(area, 1);   // 由 P1 自己赢取（谓词 WinnerIsSelfInSameArea 成立）

            Assert.AreEqual(CardZone.ScoreZone, faceUp.CurrentZone, "正面圣婴 → 计分区");
            Assert.AreEqual(CardZone.Deck, covert.CurrentZone, "暗藏圣婴 → 照常回牌库底（修复前会误进计分区）");
        }

        // ==================== ⑥b 激活权威门禁：暗藏卡只能发动"暗藏者限定"能力 ====================

        private static EffectData ActivationEffect(string id, bool requireCovert = false)
            => new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速行动",
                Cost = 0,
                RequireSourceCovert = requireCovert,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };

        public void TestZoneGate_暗藏卡不能发动普通激活能力()
        {
            var game = NewGame(1);
            string area = FirstArea(game);
            var tmpl = Probe("TS_zg_act_normal");
            tmpl.EffectDataList.Add(ActivationEffect("TS_zg_act_normal_e"));
            tmpl._effectsInjected = true;
            var unit = OnFieldCovert(game, tmpl, 1, area);

            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            EffectEngine.Instance.ActivateAbility(unit, tmpl.EffectDataList[0], null, -1);

            Assert.AreEqual(0, game.StackManager.Entries.Count,
                "**背面朝上的卡不得发动未声明『暗藏者限定』的激活能力**（权威侧 G0 门禁）");
        }

        public void TestZoneGate_暗藏卡可发动暗藏者限定能力()
        {
            var game = NewGame(1);
            string area = FirstArea(game);
            var tmpl = Probe("TS_zg_act_covert");
            tmpl.EffectDataList.Add(ActivationEffect("TS_zg_act_covert_e", requireCovert: true));
            tmpl._effectsInjected = true;
            var unit = OnFieldCovert(game, tmpl, 1, area);

            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            EffectEngine.Instance.ActivateAbility(unit, tmpl.EffectDataList[0], null, -1);

            Assert.AreEqual(1, game.StackManager.Entries.Count,
                "显式声明『只有作为暗藏者时可以发动』的能力（JC041 口径）仍可由暗藏卡发动");
        }
    }
}
