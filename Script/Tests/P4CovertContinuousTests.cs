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
    /// 【用户口径 · 2026-09】**"暗藏者（背面朝上）的持续/静止效果也不生效"** —— 把"暗藏"这一状态的门禁
    /// 在四个通道上**对齐**：
    ///  ① **触发**：`TriggerCovertMode` 默认 `FaceUp` ⇒ 暗藏者的触发默认被跳过（`TriggerRegistry`）；
    ///  ② **激活**：`ActivateEligibility` 硬门禁 G0 `if (card.IsCovert) return false`（仅声明
    ///    `RequireSourceCovert` 的 JC041① 反向例外）；
    ///  ③ **光环**：`EffectEngine.ReevaluateAuras` 里"源卡必须正面"（暗藏则整光环注销）；
    ///  ④ **持续/静止（本次新增）**：`EffectEngine.RegisterPersistent` 在卡处于暗藏态时**跳过注册**
    ///    （不进持续授予，也不登记地区级静止规则/封锁秘密派遣等）；翻面时由装配层钩子
    ///    （`GameInitializer` 的 `CovertChanged`）**注销/重新注册** ⇒ 潜伏即失效、**现身即恢复**。
    /// 另：两个"读**印刷规则**而非注册表"的判定点各补一次 `IsCovert` 门禁（同口径、两端同解）：
    ///  · `EffectEngine.GetAttachSubCategoryWhitelist`（JC081「除装备与祝福外不受附属」）；
    ///  · `CovertInspectRule.HasPrintedLocalCovertInspect`（JC057「随时检视本地区任一暗藏者」）。
    /// </summary>
    public class P4CovertContinuousTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 909 };
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
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
                TargetRules = category == CardCategory.Attachment
                    ? new Array<TargetRule> { TargetRule.Make(TargetKind.Character) }
                    : new Array<TargetRule>(),
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        // ==================== ① 持续效果：暗藏即注销，现身即恢复 ====================

        public void TestCovertContinuous_持续效果随朝向开关()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, areas[0]);

            Assert.IsTrue(game.EffectEngine.HasContinuous(jc081), "正面在场：持续效果（灵体）已注册");
            Assert.IsTrue(jc081.HasStaticFlag(StaticKind.OnlyAttachWithSubCategories),
                "正面在场：静止规则标记已置位");

            jc081.SetCovert(true);   // 潜伏（背面朝上）
            Assert.IsFalse(game.EffectEngine.HasContinuous(jc081),
                "**暗藏时持续效果注销**（背面朝上的卡不提供持续规则）");
            Assert.IsFalse(jc081.HasStaticFlag(StaticKind.OnlyAttachWithSubCategories),
                "**暗藏时静止规则标记被清除**");

            jc081.SetCovert(false);  // 现身
            Assert.IsTrue(game.EffectEngine.HasContinuous(jc081), "**现身即恢复**：持续效果重新注册");
            Assert.IsTrue(jc081.HasStaticFlag(StaticKind.OnlyAttachWithSubCategories),
                "现身即恢复：静止规则标记重新置位");
        }

        // ==================== ② 地区级静止规则：暗藏时失效 ====================

        public void TestCovertContinuous_地区级静止规则随朝向开关()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc098 = OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);

            Assert.IsTrue(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "正面在场：本地区略过调查对抗（JC098 的静止规则生效）");

            jc098.SetCovert(true);
            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "**暗藏时地区级静止规则失效**（不再略过对抗）");

            jc098.SetCovert(false);
            Assert.IsTrue(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "现身即恢复");
        }

        // ==================== ③ 读印刷规则的判定点也要失效（JC081 结附白名单） ====================

        public void TestCovertContinuous_结附白名单随朝向开关()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, areas[0]);
            var badge = game.CardManager.CreateCardInstance(Probe("TS_cc_badge", CardCategory.Attachment), 1);
            game.ZoneManager.MoveToHand(badge, 1);   // 【徽章】类附属（不在白名单内；打出目标＝角色）

            Assert.AreEqual("装备,祝福", EffectEngine.GetAttachSubCategoryWhitelist(jc081),
                "正面在场：白名单生效");
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(badge, jc081),
                "正面在场：【徽章】附属不能结附（白名单内只有装备/祝福）");

            jc081.SetCovert(true);
            Assert.IsNull(EffectEngine.GetAttachSubCategoryWhitelist(jc081),
                "**暗藏时白名单失效**（背面朝上的卡不提供静止规则）");

            // 暗藏后该卡按"暗藏者"种类被指定：用一个"结附于目标暗藏者"的附属验证——白名单已不再拦截它
            var covertTargeter = game.CardManager.CreateCardInstance(
                CovertAttachProbe("TS_cc_badge2"), 1);
            game.ZoneManager.MoveToHand(covertTargeter, 1);
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(covertTargeter, jc081),
                "暗藏时该卡不再『只受装备与祝福结附』⇒ 非白名单附属可合法指定它");

            jc081.SetCovert(false);
            Assert.AreEqual("装备,祝福", EffectEngine.GetAttachSubCategoryWhitelist(jc081), "现身即恢复");
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(badge, jc081), "现身恢复后【徽章】又被拦下");
        }

        /// <summary>探针附属：打出目标＝**暗藏者**（用于在"暗藏态"下验证白名单失效）。</summary>
        private static PlayerCardData CovertAttachProbe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针附属_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Attachment, Defense = -1,
                TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Covert) },
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        // ==================== ④ 检视授权（JC057）随朝向开关 ====================

        public void TestCovertContinuous_检视授权随朝向开关()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = OnField(game, Load("JC057_jiankongyuan"), 1, areas[0]);
            var enemyCovert = OnField(game, Probe("TS_cc_cov"), 2, areas[0]);
            enemyCovert.SetCovert(true);

            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "正面未横置：授权检视本地区敌方暗藏者");

            jc057.SetCovert(true);
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "**JC057 自身暗藏时其持续授权失效**（背面朝上的卡不提供持续规则）");

            jc057.SetCovert(false);
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager), "现身即恢复");
        }

        // ==================== ⑤ 与触发/激活门禁的一致性（回归） ====================

        public void TestCovertContinuous_触发与激活门禁不受影响()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var card = OnField(game, Load("JC081_yaluoer"), 1, areas[0]);

            // 激活：暗藏者不可作为激活来源（硬门禁 G0；JC081 的激活效果不存在，此处用 CanActivate 的口径验证）
            card.SetCovert(true);
            game.TurnManager.FirstPlayerId = 1;
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            Assert.IsFalse(ActivateEligibility.CanActivate(card, game.TurnManager, game.PlayerManager, game.CardManager),
                "暗藏者不可发动激活能力（与既有硬门禁一致）");
        }
    }
}
