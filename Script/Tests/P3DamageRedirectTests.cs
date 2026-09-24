using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K3·**持续型**伤害重定向 → JC077 庇佑圣灵 / JC121 空鸣御守】
    /// 用户 2026-09 口径：这两张卡的伤害条目是**持续效果**（卡库里的"触发："写法属效果数据错误），
    /// 不是入堆叠的触发、也不走"替代式效应"框架。
    ///
    /// 交付：`EffectData.DamageRedirect` → `DamageRedirectSpec.Victim`
    ///  · `FriendlyNonSpiritLocal`（JC077）：本卡所在地区内、同控制者、**非灵体**的角色将受到的伤害 → 改由本卡承受；
    ///  · `AttachedHost`（JC121）：本卡**结附的宿主**将受到的伤害 → 改由本卡承受；
    ///  · 消费点＝`CardManager.DealDamageToInstance`（伤害唯一收口）→ `EffectEngine.ResolveDamageRedirect`；
    ///  · **一次伤害只重定向一次**（防乒乓）；承受方自身的"防止"（灵体/护盾）仍在其落地时照常判定。
  /// </summary>
    public class P3DamageRedirectTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 991 };
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

        private static PlayerCardData Probe(string uid, int defense = 5, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = defense,
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

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        /// <summary>
        /// 给指定玩家在该地区放一个带 N 个魔法领域图标的角色 —— 用于**压制**承受方（庇佑圣灵/空鸣御守都是**灵体**）的
        /// "灵体防止"判据（该判据是"对手领域图标**更少**才防止"）。
        /// 需要验证"伤害真的落到承受方身上"的用例必须先把对手的领域图标数拉到 ≥ 本方，否则伤害会被灵体防止掉。
        /// </summary>
        private static CardInstance PlaceDomainBlocker(GameInitializer game, int playerId, string area, int domains)
        {
            var tmpl = Probe($"TS_rd_domain_{playerId}_{domains}", defense: 9);
            for (int i = 0; i < domains; i++) tmpl.MagicDomains.Add(MagicDomain.Mind);
            return OnField(game, tmpl, playerId, area);
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

        public void TestRedirect_两张卡结构()
        {
            var jc077 = Load("JC077_biyoushengling");
            var e77 = FindEffect(jc077, "JC077_B_RedirectFriendlyDamageToSelf");
            Assert.IsNotNull(e77, "JC077 应有『伤害改由本卡承受』效果");
            Assert.AreEqual(EffectTiming.Continuous, e77.Timing, "用户口径：**持续效果**（不是触发）");
            Assert.IsNotNull(e77.DamageRedirect, "声明了伤害重定向");
            Assert.AreEqual(DamageRedirectVictim.FriendlyNonSpiritLocal, e77.DamageRedirect.Victim, "受保护对象＝本地区非灵体本方角色");

            var jc121 = Load("JC121_kongmingyushou");
            var e121 = FindEffect(jc121, "JC121_B_RedirectHostDamageToSelf");
            Assert.IsNotNull(e121, "JC121 应有『伤害改由本卡承受』效果");
            Assert.AreEqual(EffectTiming.Continuous, e121.Timing, "持续效果");
            Assert.AreEqual(DamageRedirectVictim.AttachedHost, e121.DamageRedirect.Victim, "受保护对象＝结附宿主");
        }

        // ==================== ② JC077：本地区非灵体本方角色受伤 → 改由庇佑圣灵承受 ====================

        public void TestRedirect_JC077本地区本方角色()
        {
            var game = NewGame();
            var areas = Areas(game);
            var guardian = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);
            var friend = OnField(game, Probe("TS_rd_friend"), 1, areas[0]);
            PlaceDomainBlocker(game, 2, areas[0], domains: 2);   // 压制"灵体防止"，让伤害真的落到庇佑圣灵身上
            guardian.CurrentDefense = 10;                        // 临时提高防御：否则 1 点伤害就会打死这张灵体，Damage 随死亡清零、读不到数值
            int friendDamageBefore = friend.Damage;
            int guardianDamageBefore = guardian.Damage;

            game.CardManager.DealDamageToInstance(friend, 2);

            Assert.AreEqual(friendDamageBefore, friend.Damage, "受保护角色**没有**吃伤害");
            Assert.AreEqual(guardianDamageBefore + 2, guardian.Damage, "伤害改由庇佑圣灵承受");
        }

        // ==================== ③ JC077 的边界：敌方 / 其它地区 / 灵体 都不受保护 ====================

        public void TestRedirect_JC077边界()
        {
            var game = NewGame();
            var areas = Areas(game);
            var guardian = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);
            var enemy = OnField(game, Probe("TS_rd_enemy"), 2, areas[0]);
            var allyOtherArea = OnField(game, Probe("TS_rd_other_area"), 1, areas[1]);
            var allySpirit = OnField(game, Probe("TS_rd_spirit"), 1, areas[0]);
            allySpirit.Grant("Keyword", "灵体", 1);   // 灵体角色（非灵体条件不满足）

            int g0 = guardian.Damage;
            game.CardManager.DealDamageToInstance(enemy, 1);
            Assert.AreEqual(g0, guardian.Damage, "敌方角色不受保护（伤害不转移）");
            Assert.AreEqual(1, enemy.Damage, "敌方角色自己吃伤害");

            game.CardManager.DealDamageToInstance(allyOtherArea, 1);
            Assert.AreEqual(g0, guardian.Damage, "其它地区的本方角色不受保护");

            game.CardManager.DealDamageToInstance(allySpirit, 1);
            Assert.AreEqual(g0, guardian.Damage, "**灵体**本方角色不受保护（卡文『非灵体的本方角色』）");
        }

        // ==================== ④ 来源离场（或解附）后重定向失效 ====================

        public void TestRedirect_来源离场后失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var guardian = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);
            var friend = OnField(game, Probe("TS_rd_friend2"), 1, areas[0]);
            PlaceDomainBlocker(game, 2, areas[0], domains: 2);   // 压制"灵体防止"，让伤害落到庇佑圣灵身上
            guardian.CurrentDefense = 10;                        // 同上：便于观察伤害数值

            game.CardManager.DealDamageToInstance(friend, 1);
            Assert.AreEqual(1, guardian.Damage, "前置：重定向生效");

            game.ZoneManager.MoveToHand(guardian, 1);   // 来源离场 → 登记注销

            game.CardManager.DealDamageToInstance(friend, 1);
            Assert.AreEqual(1, friend.Damage, "来源离场后：伤害照常落在原目标身上");
        }

        // ==================== ⑤ JC121：结附宿主受保护 ====================

        public void TestRedirect_JC121结附宿主()
        {
            var game = NewGame();
            var areas = Areas(game);
            var host = OnField(game, Probe("TS_rd_host"), 1, areas[0]);
            var other = OnField(game, Probe("TS_rd_other"), 1, areas[0]);
            var charm = game.CardManager.CreateCardInstance(Load("JC121_kongmingyushou"), 1);
            game.ZoneManager.MoveToArea(charm, areas[0], 1);
            Attach(game, host, charm);
            PlaceDomainBlocker(game, 2, areas[0], domains: 2);   // 压制"灵体防止"
            charm.CurrentDefense = 10;                           // 同上：便于观察伤害数值

            game.CardManager.DealDamageToInstance(host, 3);

            Assert.AreEqual(0, host.Damage, "宿主没有吃伤害（改由空鸣御守承受）");
            Assert.AreEqual(3, charm.Damage, "空鸣御守承受了这 3 点伤害");

            game.CardManager.DealDamageToInstance(other, 1);
            Assert.AreEqual(1, other.Damage, "未结附的其它角色不受影响");
        }

        // ==================== ⑥ 一次伤害只重定向一次（防乒乓） ====================

        public void TestRedirect_不来回乒乓()
        {
            var game = NewGame();
            var areas = Areas(game);
            // 两个"庇佑圣灵"式来源：若允许往返重定向会无限递归；本用例验证只转移一次
            var guardianA = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);
            var guardianB = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);
            var friend = OnField(game, Probe("TS_rd_pingpong"), 1, areas[0]);
            PlaceDomainBlocker(game, 2, areas[0], domains: 3);   // 压制"灵体防止"（两张来源共 2 个领域图标）
            guardianA.CurrentDefense = 10;                       // 同上：便于观察伤害数值
            guardianB.CurrentDefense = 10;

            game.CardManager.DealDamageToInstance(friend, 2);

            int total = guardianA.Damage + guardianB.Damage;
            Assert.AreEqual(2, total, "伤害只被转移一次（合计 2 点落在某一张来源卡上，没有翻滚叠加）");
            Assert.AreEqual(0, friend.Damage, "原目标仍未被伤害");
        }

        // ==================== ⑦ 承受方的"灵体防止"照常生效（落地时判定） ====================

        public void TestRedirect_承受方灵体防止仍生效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var guardian = OnField(game, Load("JC077_biyoushengling"), 1, areas[0]);   // 本身是灵体
            var friend = OnField(game, Probe("TS_rd_prevent"), 1, areas[0]);
            // 灵体防止判据：本地区我方魔法领域图标数 > 对手（造一个我方领域角色 + 敌方无领域）
            var mindAlly = OnField(game, Probe("TS_rd_domain"), 1, areas[0]);
            mindAlly.MagicDomains.Add(MagicDomain.Mind);

            game.CardManager.DealDamageToInstance(friend, 2);

            Assert.AreEqual(0, friend.Damage, "原目标没吃伤害");
            Assert.AreEqual(0, guardian.Damage, "伤害落到灵体（庇佑圣灵）身上后被其『灵体』防止（我方领域图标更多）");
        }
    }
}
