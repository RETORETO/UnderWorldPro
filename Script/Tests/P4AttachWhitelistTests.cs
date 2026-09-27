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
    /// 【JC081 亚洛尔·[前缀]】「**除装备与祝福外不受附属**」（用户 2026-09 口径：**与 JC001 类似实现即可**）。
    ///
    /// 与 JC001「不能被装备结附」（`StaticKind.CannotBeAttached`，一律禁止）同类但更细：
    /// 由**子类别白名单**表达 —— `StaticKind.OnlyAttachWithSubCategories`（`Param = "装备,祝福"`），
    /// 语义＝"**只有**命中白名单的附属能结附到本卡"。
    ///
    /// 判定**单一真相源**（引擎静态 helper，读卡面印刷静止规则，两端同解，不依赖 EffectEngine 实例）：
    ///  · `EffectEngine.GetAttachSubCategoryWhitelist(target)`：未声明该规则 → **null**（不施加限制）；
    ///    声明了 → 白名单串（本卡＝`装备,祝福`）；
    ///  · `EffectEngine.PassesAttachWhitelist(attaching, whitelist)`：附属**当前**子类别命中任一白名单项 → 放行；
    ///    白名单为空串 → **放行**（fail-open：防 Param 拼错把整张卡封死）。
    ///
    /// 消费点两处（与 JC001 同款两处拦截）：
    ///  ① 打出阶段 `CardManager.IsLegalPlayTarget` —— 非法附属**不能以本卡为目标**（UI 不会把它列为合法目标）；
    ///  ② 结算兜底 `TurnManager.ResolveEntry` 附属分支 —— 绕过 UI 直接入栈时**进墓地**（不建立结附关系）。
    /// </summary>
    public class P4AttachWhitelistTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 1081 };
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

        /// <summary>探针角色（无静止规则，用作对照靶标）。</summary>
        private static PlayerCardData CharProbe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 5,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>探针附属：指定子类别，打出目标＝角色（打出阶段只受白名单规则拦截）。</summary>
        private static PlayerCardData AttachProbe(string uid, params string[] subCategories)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针附属_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Attachment, Defense = -1,
                SubCategories = new Array<string>(),
                TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Character) },
            };
            foreach (var s in subCategories) tmpl.SubCategories.Add(s);
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

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static CardInstance InHand(GameInitializer game, PlayerCardData tmpl, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToHand(card, playerId);
            return card;
        }

        // ==================== ① 结构：JC081 声明了子类别白名单静止规则 ====================

        public void TestAttachWhitelist_JC081结构()
        {
            var tmpl = Load("JC081_yaluoer");
            var e = FindEffect(tmpl, "JC081_C_OnlyEquipAndBlessingAttachments");
            Assert.IsNotNull(e, "JC081 应有『除装备与祝福外不受附属』静止效果");
            Assert.AreEqual(EffectTiming.Static, e.Timing, "静止规则（非触发/持续）");
            Assert.AreEqual(ZoneScope.AreaZone, e.Scope, "在争夺区生效（与 JC001 同款）");
            Assert.IsNotNull(e.StaticEffects, "应声明 StaticEffects");
            Assert.AreEqual(1, e.StaticEffects.Count, "一条静止规则");
            Assert.AreEqual(StaticKind.OnlyAttachWithSubCategories, e.StaticEffects[0].Kind, "种类＝子类别白名单");
            Assert.AreEqual("装备,祝福", e.StaticEffects[0].Param, "白名单＝装备/祝福");
        }

        // ==================== ② 判定表：白名单命中/未命中/未声明/fail-open ====================

        public void TestAttachWhitelist_命中白名单放行()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);

            string whitelist = EffectEngine.GetAttachSubCategoryWhitelist(jc081);
            Assert.AreEqual("装备,祝福", whitelist, "从卡实例读到的白名单＝装备,祝福");

            var equip = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_equip", "装备", "武器"), 1);
            var blessing = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_bless", "状态", "祝福"), 1);
            Assert.IsTrue(EffectEngine.PassesAttachWhitelist(equip, whitelist), "【装备】类附属命中白名单 → 放行");
            Assert.IsTrue(EffectEngine.PassesAttachWhitelist(blessing, whitelist), "【祝福】类附属命中白名单 → 放行");
        }

        public void TestAttachWhitelist_未命中白名单拒绝()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);
            string whitelist = EffectEngine.GetAttachSubCategoryWhitelist(jc081);

            var badge = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_badge", "徽章"), 1);   // JC012 同款
            var curse = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_curse", "诅咒"), 1);   // JC089 同款
            var plain = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_plain"), 1);
            Assert.IsFalse(EffectEngine.PassesAttachWhitelist(badge, whitelist), "【徽章】不在白名单 → 拒绝");
            Assert.IsFalse(EffectEngine.PassesAttachWhitelist(curse, whitelist), "【诅咒】不在白名单 → 拒绝");
            Assert.IsFalse(EffectEngine.PassesAttachWhitelist(plain, whitelist), "无命中的子类别 → 拒绝");
        }

        public void TestAttachWhitelist_白名单为空则放行()
        {
            var game = NewGame();
            var equip = game.CardManager.CreateCardInstance(AttachProbe("TS_aw_open"), 1);
            Assert.IsTrue(EffectEngine.PassesAttachWhitelist(equip, ""), "白名单为空串 → fail-open 放行");
            Assert.IsTrue(EffectEngine.PassesAttachWhitelist(equip, " , "), "只有空白/逗号 → 视作空白名单放行");
        }

        public void TestAttachWhitelist_未声明规则不受限()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var plainChar = OnField(game, CharProbe("TS_aw_plainchar"), 1, area);
            Assert.IsNull(EffectEngine.GetAttachSubCategoryWhitelist(plainChar),
                "未声明该规则的角色 → 返回 null（调用方不施加任何限制）");
        }

        // ==================== ③ 打出阶段拦截（CardManager.IsLegalPlayTarget） ====================

        public void TestAttachWhitelist_打出阶段拦截非法附属()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);

            var badge = InHand(game, AttachProbe("TS_aw_badge2", "徽章"), 1);
            var curse = InHand(game, AttachProbe("TS_aw_curse2", "诅咒"), 1);
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(badge, jc081), "【徽章】附属不能以 JC081 为打出目标");
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(curse, jc081), "【诅咒】附属不能以 JC081 为打出目标");
        }

        public void TestAttachWhitelist_打出阶段放行装备与祝福附属()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);

            var equip = InHand(game, AttachProbe("TS_aw_equip2", "装备", "武器"), 1);
            var blessing = InHand(game, AttachProbe("TS_aw_bless2", "祝福"), 1);
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(equip, jc081), "【装备】类附属合法（白名单内）");
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(blessing, jc081), "【祝福】类附属合法（白名单内）");
        }

        public void TestAttachWhitelist_不误伤其它角色()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var plainChar = OnField(game, CharProbe("TS_aw_other"), 1, area);
            var badge = InHand(game, AttachProbe("TS_aw_badge3", "徽章"), 1);
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(badge, plainChar),
                "未声明白名单的角色不受限制：非装备附属照样合法");
        }

        // ==================== ④ 权威路径：PlayCard 在入堆叠前拒绝非法目标 ====================

        public void TestAttachWhitelist_PlayCard拒绝非法目标()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);
            var badge = InHand(game, AttachProbe("TS_aw_badge4", "徽章"), 1);

            bool ok = game.TurnManager.PlayCard(1, badge.UID, null, jc081.UID);
            Assert.IsFalse(ok, "打出阶段被拦截：返回 false");
            Assert.AreEqual(CardZone.Hand, badge.CurrentZone, "被拒绝的附属留在手牌（未入堆叠）");
            Assert.IsTrue(game.StackManager.IsEmpty, "堆叠为空（未入堆叠）");
            Assert.IsFalse(badge.IsAttached, "未建立结附关系");
        }

        // ==================== ⑤ 结算兜底：绕过 UI 直接入栈 → 非法附属进墓地 ====================

        public void TestAttachWhitelist_结算兜底非法附属进墓地()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);
            var badge = InHand(game, AttachProbe("TS_aw_badge5", "徽章"), 1);

            // 绕过打出阶段校验，直接把"结附 JC081"的附属条目压入堆叠（模拟越权/历史快照/直接注入）
            game.StackManager.Push(new StackEntry(1, badge)
            {
                Kind = ResolveKind.Attachment,
                SourceCardUid = badge.UID,
                TargetCardUid = jc081.UID,
            });
            Assert.IsFalse(game.StackManager.IsEmpty, "前置：附属条目已入堆叠");

            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(game.StackManager.IsEmpty, "堆叠已结算完");
            Assert.AreEqual(CardZone.Graveyard, badge.CurrentZone, "结算兜底：非法附属进墓地");
            Assert.IsFalse(jc081.AttachedCards != null && jc081.AttachedCards.Contains(badge),
                "未建立结附关系（宿主未收录）");
        }

        public void TestAttachWhitelist_结算兜底放行装备附属()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);
            var equip = InHand(game, AttachProbe("TS_aw_equip5", "装备", "武器"), 1);

            game.StackManager.Push(new StackEntry(1, equip)
            {
                Kind = ResolveKind.Attachment,
                SourceCardUid = equip.UID,
                TargetCardUid = jc081.UID,
            });

            for (int i = 0; i < 6 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.AttachedZone, equip.CurrentZone, "白名单内的【装备】附属正常结附");
            Assert.AreEqual(jc081, equip.AttachedTo, "宿主＝JC081");
        }
    }
}
