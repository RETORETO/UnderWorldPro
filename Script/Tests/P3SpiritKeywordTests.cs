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
    /// 【2026-09 修复·用户实测"JC022 灵体没有生效"】＋**同类排查**
    ///
    /// 规则：灵体角色被分配伤害时，若**本方**在本地区的魔法领域图标数 **多于对手** → 伤害被完全防止
    /// （`DamageRules.IsSpiritPrevention` → 接到 `CardManager.DamagePreventer`）。
    ///
    /// 缺陷：以下 6 张卡**卡文以「灵体」开头**，但效果脚本里没有授予【灵体】关键词 ⇒ `HasActiveKeyword("灵体")`
    /// 为假 ⇒ 灵体防止伤害**永不生效**（战斗失败后对方的伤害分配照常落地）：
    /// JC013 貘 / JC022 引路仙子 / JC100 尖牙幻象 / JC105 噩梦领主 / JC110 闲人的绿地 / JC113 五行道丹精。
    /// 修复：按 JC077/JC111 同款"持续关键字授予"补齐（`ContinuousGrants` 授 `Keyword:灵体`）。
    /// </summary>
    public class P3SpiritKeywordTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>卡文以「灵体」开头、需自行授予该关键词的卡（用户 2026-09 确认一并修复）。</summary>
        private static readonly string[] SpiritCards =
        {
            "JC013_mo", "JC022_yinluxianzi", "JC100_jianyahuanxiang",
            "JC105_emenglingzhu", "JC110_xianrendelvdi", "JC113_wuxingdaodanjing",
        };

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 20260924 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            return game;
        }

        private static PlayerCardData Load(string f)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var t = Godot.ResourceLoader.Load<PlayerCardData>(Dir + f + ".tres");
            if (t != null) CardEffectFactory.EnsureEffects(t);
            return t;
        }

        private static PlayerCardData Probe(string uid, int domains, int defense = 9)
        {
            var t = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            for (int i = 0; i < domains; i++) t.MagicDomains.Add((MagicDomain)i);
            t._effectsInjected = true;
            return t;
        }

        private static CardInstance OnField(GameInitializer g, PlayerCardData t, int pid, string area)
        {
            var c = g.CardManager.CreateCardInstance(t, pid);
            g.ZoneManager.MoveToArea(c, area, pid);
            return c;
        }

        private static string FirstArea(GameInitializer g) => g.AreaManager.GetOrderedAreaUids()[0];

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();
            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger,
                List<string> validTargetUids, System.Action<string> onPicked) => onPicked?.Invoke(null);
            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① 结构：6 张卡都必须授予『灵体』关键词 ====================

        public void TestSpirit_六张灵体卡都授予关键词()
        {
            foreach (var f in SpiritCards)
            {
                var tmpl = Load(f);
                Assert.IsNotNull(tmpl, $"{f} 应可加载");
                bool found = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.ContinuousGrants == null) continue;
                    foreach (var g in e.ContinuousGrants)
                        if (g.Kind == "Keyword" && g.Value == "灵体") found = true;
                }
                Assert.IsTrue(found, $"{f} 卡文以「灵体」开头 ⇒ 脚本必须授予 Keyword:灵体（否则灵体防止伤害失效）");
            }
        }

        // ==================== ② 行为：进场即"灵体"生效 ====================

        public void TestSpirit_进场后灵体生效()
        {
            var game = NewGame();
            foreach (var f in SpiritCards)
            {
                var inst = OnField(game, Load(f), 1, FirstArea(game));
                Assert.IsTrue(DamageRules.IsSpirit(inst), $"{f} 进场后 `IsSpirit` 必须为真");
            }
        }

        // ==================== ③ 端到端：我方领域图标更多 → 伤害被防止 ====================

        /// <summary>
        /// 用户场景：本地区我方只有 JC022（1 个领域图标），对方只有 JC016（**无领域图标**，`[-1]` 哨兵不计），
        /// 战斗失败后被分配伤害 ⇒ 必须被灵体**完全防止**。
        /// </summary>
        public void TestSpirit_JC022防止战斗伤害()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var spirit = OnField(game, Load("JC022_yinluxianzi"), 1, area);
            OnField(game, Load("JC016_zhixingbujingrui"), 2, area);

            Assert.IsTrue(DamageRules.IsSpirit(spirit), "JC022 是灵体");
            Assert.AreEqual(1, spirit.MagicDomains.Count, "JC022 有 1 个魔法领域图标");
            Assert.IsTrue(DamageRules.IsSpiritPrevention(spirit, game.ZoneManager),
                "本方图标(1) > 对手(0) ⇒ 防止");

            game.CardManager.DealDamageToInstance(spirit, 3);   // 战斗败方被分配的伤害走同一收口

            Assert.AreEqual(0, spirit.Damage, "伤害被灵体完全防止（不落地、不判死）");
        }

        /// <summary>反向：对手领域图标**不少于**我方 ⇒ 不防止（严格小于才防止）。</summary>
        public void TestSpirit_对手图标不少于时不防止()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var spirit = OnField(game, Load("JC022_yinluxianzi"), 1, area);
            OnField(game, Probe("TS_sp_foe", domains: 2), 2, area);   // 对手 2 个领域图标 > 我方 1

            Assert.IsFalse(DamageRules.IsSpiritPrevention(spirit, game.ZoneManager),
                "对手图标(2) 不小于本方(1) ⇒ 不防止");

            spirit.CurrentDefense = 10;   // 临时提高防御：否则 1 点就打死，Damage 随死亡清零、读不到数值
            game.CardManager.DealDamageToInstance(spirit, 3);
            Assert.AreEqual(3, spirit.Damage, "不防止时伤害照常落地");
        }

        /// <summary>增援后本方图标变多，灵体防止随之成立（实时按地区领域数判定）。</summary>
        public void TestSpirit_本方增援后开始防止()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var spirit = OnField(game, Load("JC022_yinluxianzi"), 1, area);   // 我方 1
            var foe = OnField(game, Probe("TS_sp_foe2", domains: 2), 2, area); // 对手 2 → 不防止
            Assert.IsFalse(DamageRules.IsSpiritPrevention(spirit, game.ZoneManager), "前置：不防止");

            OnField(game, Probe("TS_sp_ally", domains: 2), 1, area);          // 我方 3 > 对手 2
            Assert.IsTrue(DamageRules.IsSpiritPrevention(spirit, game.ZoneManager),
                "本方领域图标反超后 ⇒ 恢复防止");
            Assert.IsNotNull(foe, "对手仍在场");
        }

        /// <summary>暗藏者（背面）不提供效果 ⇒ 灵体休眠，伤害不被防止（2026-09 通用红线）。</summary>
        public void TestSpirit_暗藏时灵体休眠()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var spirit = OnField(game, Load("JC022_yinluxianzi"), 1, area);
            OnField(game, Load("JC016_zhixingbujingrui"), 2, area);
            spirit.SetCovert(true);

            Assert.IsFalse(DamageRules.IsSpirit(spirit), "背面朝上 ⇒ 灵体不提供（休眠）");
            game.CardManager.DealDamageToInstance(spirit, 2);
            Assert.AreEqual(0, spirit.Damage, "暗藏者免疫伤害（不写伤害），但原因不是灵体防止");
        }
    }
}
