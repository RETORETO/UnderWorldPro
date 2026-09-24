using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·图标镜像 → JC066 封魔瓶瑟琳娜②】
    /// 卡文：持续：瑟琳娜额外具有被封印在其上的角色牌能力栏内的所有能力图标。
    ///
    /// 交付：`ContinuousPrereqSpec.MirrorSealedIcons` —— 持续授予的**来源改写**：
    ///  · 本效果不看卡脚本静态声明的 `ContinuousGrants`，而是逐张读取"**被封印在本卡上的角色牌**"的
    ///    **印刷能力图标**（调查/战斗/势力 + 先手三项），按 `EffectEngine.IconToAttribute` 的既有映射
    ///    （黑刀=战斗 / 黑眼=调查 / 黑星=势力 / 白刀=先手战斗 / 白眼=先手调查 / 白星=先手势力）
    ///    汇总成属性授予，**额外**加到本卡上；
    ///  · 只镜像**角色牌**（非角色封印牌不提供能力图标）、只读**印刷**图标（封印牌已不在场）、
    ///    **不含魔法领域图标**（领域 ≠ 能力图标）；
    ///  · 授予量随封印牌集合变化 → `ReevaluatePrereqs`（封印落地 / 任意换区都会触发）**撤销重算**，
  ///    登记键＝卡 UID + 效果 Id（与同卡其它持续效果互不干扰）。
    /// 同时锁定 JC066①的**封印方向**：卡文"将本地区的目标角色或暗藏者封印**于瑟琳娜上**"
    ///  → `Seal{目标, 自身}`（被封印者＝目标，宿主＝瑟琳娜），镜像才能读到 `瑟琳娜.SealedCards`。
    /// </summary>
    public class P3MirrorSealedIconsTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 233 };
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

        /// <summary>带印刷能力图标的角色探针（战力/调查/势力 + 三个先手值）。</summary>
        private static PlayerCardData IconProbe(string uid, int strength = 0, int investigation = 0, int power = 0,
            int fsStrength = 0, int fsInvestigation = 0, int fsPower = 0, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "图标_" + uid, Cost = 1,
                CategoryFlag = category, Defense = 2,
                Strength = strength, Investigation = investigation, Power = power,
                FirstStrikeStrength = fsStrength, FirstStrikeInvestigation = fsInvestigation, FirstStrikePower = fsPower,
            };
            if ((category & CardCategory.Character) != 0) tmpl.SubCategories.Add("人类");
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

        /// <summary>把瑟琳娜放到场上（触发持续效果注册）。</summary>
        private static CardInstance PlaceSerena(GameInitializer game, string area)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC066_fengmopingselinna"), 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            return card;
        }

        /// <summary>把一张探针卡**直接封印**到宿主上（走 `SealCard` → 与生产路径同一收口，含重判时机）。</summary>
        private static CardInstance SealOnto(GameInitializer game, CardInstance host, string uid, int ownerId,
            int strength = 0, int investigation = 0, int power = 0, int fsStrength = 0, int fsInvestigation = 0, int fsPower = 0,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = IconProbe(uid, strength, investigation, power, fsStrength, fsInvestigation, fsPower, category);
            var sealedCard = game.CardManager.CreateCardInstance(tmpl, ownerId);
            game.TurnManager.SealCard(sealedCard, host, faceDown: false);
            return sealedCard;
        }

        // ==================== ① 结构 ====================

        public void TestMirror_JC066结构()
        {
            var tmpl = Load("JC066_fengmopingselinna");
            Assert.IsNotNull(tmpl, "JC066 模板应可加载");
            var e = FindEffect(tmpl, "JC066_2_MirrorSealedIcons");
            Assert.IsNotNull(e, "JC066 应有『镜像封印牌能力图标』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.PrereqSpec, "声明了结构化前提/授予来源");
            Assert.IsTrue(e.PrereqSpec.MirrorSealedIcons, "授予来源＝镜像被封印角色牌的能力图标");
        }

        /// <summary>①的封印方向：被封印者＝目标，宿主＝瑟琳娜（否则镜像永远读不到 `SealedCards`）。</summary>
        public void TestMirror_封印方向被封印者为目标()
        {
            var e = FindEffect(Load("JC066_fengmopingselinna"), "JC066_1_SealTarget");
            Assert.IsNotNull(e, "JC066 应有进场封印效果");
            Assert.AreEqual("目标", e.Actions[0].Args[0], "被封印者＝本次选中的目标");
            Assert.AreEqual("自身", e.Actions[0].Args[1], "宿主＝瑟琳娜自身（卡文『封印于瑟琳娜上』）");
        }

        // ==================== ② 封印后镜像生效（图标 → 属性） ====================

        public void TestMirror_封印后获得对应属性()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);

            int baseStrength = serena.CurrentStrength;
            int basePower = serena.CurrentPower;

            // 封印一张"战力2 + 势力1 + 先手调查1"的角色牌
            SealOnto(game, serena, "TS_mi_a", 2, strength: 2, power: 1, fsInvestigation: 1);

            Assert.AreEqual(baseStrength + 2, serena.CurrentStrength, "封印牌战力2 → 瑟琳娜 +2 战斗（黑刀×2）");
            Assert.AreEqual(basePower + 1, serena.CurrentPower, "封印牌势力1 → 瑟琳娜 +1 势力（黑星×1）");
            Assert.AreEqual(1, serena.GetAttribute("先手调查"), "封印牌先手调查1 → 瑟琳娜 +1（白眼×1）");
        }

        // ==================== ③ 多张封印牌累加 ====================

        public void TestMirror_多张封印牌累加()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);
            int baseVal = serena.CurrentStrength;

            SealOnto(game, serena, "TS_mi_b1", 2, strength: 1);
            SealOnto(game, serena, "TS_mi_b2", 2, strength: 3);

            Assert.AreEqual(baseVal + 4, serena.CurrentStrength, "同属性多来源累加（黑刀 1+3=4）");
        }

        // ==================== ④ 解除封印后镜像撤销 ====================

        public void TestMirror_封印牌离场后撤销()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);
            int baseVal = serena.CurrentStrength;

            var sealedCard = SealOnto(game, serena, "TS_mi_c", 2, strength: 2);
            Assert.AreEqual(baseVal + 2, serena.CurrentStrength, "前置：镜像生效");

            // 封印牌离场（回拥有者手牌）→ 换区 → ReevaluatePrereqs 重算
            game.ZoneManager.MoveToHand(sealedCard, 2);

            Assert.AreEqual(baseVal, serena.CurrentStrength, "封印牌离场 → 镜像授予被撤销（回到基础值）");
        }

        // ==================== ⑤ 只镜像"角色牌"：非角色封印牌不提供图标 ====================

        public void TestMirror_非角色封印牌不镜像()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);
            int baseVal = serena.CurrentStrength;
            int baseInvestigation = serena.CurrentInvestigation;

            // 附属卡（非角色）：即便带印刷数值也不提供能力图标
            SealOnto(game, serena, "TS_mi_d", 2, strength: 3, investigation: 3, category: CardCategory.Attachment);

            Assert.AreEqual(baseVal, serena.CurrentStrength, "非角色封印牌不镜像（战斗不变）");
            Assert.AreEqual(baseInvestigation, serena.CurrentInvestigation, "非角色封印牌不镜像（调查不变）");
        }

        // ==================== ⑥ 只读印刷图标：封印牌自身不带图标即不镜像 ====================

        public void TestMirror_无图标封印牌不镜像()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);
            int baseVal = serena.CurrentStrength;

            SealOnto(game, serena, "TS_mi_e", 2);   // 无任何印刷图标

            Assert.AreEqual(baseVal, serena.CurrentStrength, "无能力图标的封印牌 → 不授予任何属性");
        }

        // ==================== ⑦ 端到端：进场触发封印 → 镜像生效 ====================

        public void TestMirror_端到端进场封印后镜像()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var serena = PlaceSerena(game, area);
            var victim = game.CardManager.CreateCardInstance(
                IconProbe("TS_mi_f", strength: 2, investigation: 1), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            int baseStrength = serena.CurrentStrength;
            int baseInvestigation = serena.CurrentInvestigation;

            // 走真实结算：进场触发把目标封印在瑟琳娜上
            EffectEngine.Instance.ResolveEffect(
                FindEffect(serena.PlayerTemplate, "JC066_1_SealTarget"), serena, victim);

            Assert.IsTrue(serena.SealedCards.Contains(victim), "前置：目标被封印在瑟琳娜上");
            Assert.AreEqual(baseStrength + 2, serena.CurrentStrength, "结算后镜像生效：+2 战斗");
            Assert.AreEqual(baseInvestigation + 1, serena.CurrentInvestigation, "结算后镜像生效：+1 调查");
        }
    }
}
