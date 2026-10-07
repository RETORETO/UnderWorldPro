using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【P1·新卡】本批用 P1 新能力实现的第一批卡牌：结构 + 关键行为回归。
    ///
    /// 覆盖卡：JC006 / JC010 / JC029 / JC039 / JC042 / JC058 / JC071 / JC075 / JC088 / JC090 / JC091 / JC102 / JC103 / JC112
    /// （其中 JC090 走**端到端**：消灭本方角色 → OnCardDied → 条件 DiedAllyCharacter → 触发入堆叠）；
    /// 其余以"模板结构 + 关键新机制"断言为主，行为细节由 P1EffectFoundationsTests 覆盖。
    /// </summary>
    public class P1NewCardsTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private sealed class AllEnterGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                return Task.FromResult(r);
            }
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static EffectData Find(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static bool HasVerb(EffectData e, ActionVerb verb)
        {
            if (e?.Actions == null) return false;
            foreach (var a in e.Actions)
                if (a != null && a.Verb == verb) return true;
            return false;
        }

        private static bool HasRestriction(EffectData e, TargetRestriction r)
            => e?.Target?.Restrictions != null && e.Target.Restrictions.Contains(r);

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 11 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static string FirstAreaUid(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static bool WaitUntil(System.Func<bool> cond, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                System.Threading.Thread.Sleep(5);
            }
            return cond();
        }

        // ==================== 结构：14 张卡的效果数据符合卡文 ====================

        public void TestP1Cards_结构注册与卡文一致()
        {
            // JC006：事务快速行动，目标"具魔法领域图标"的角色 → 移回手牌
            var jc006 = Load("JC006_nizhuanyijiezhimen");
            Assert.IsNotNull(jc006, "JC006 模板应可加载");
            var e006 = Find(jc006, "JC006_1_BounceDomainCharacter");
            Assert.IsNotNull(e006, "JC006 应有 BounceDomainCharacter 效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e006.Timing, "JC006 事务打出即结算");
            Assert.AreEqual(PlayTiming.Response, e006.PlayTiming, "JC006 为快速行动");
            Assert.IsTrue(HasRestriction(e006, TargetRestriction.HasMagicDomainIcon), "JC006 目标应限定『具魔法领域图标』");
            Assert.IsTrue(HasVerb(e006, ActionVerb.Bounce), "JC006 动作为移回手牌");

            // JC010：领袖/灵体 + 快速行动横置消灭"已横置"角色
            // （注：2026-09 改为**效果级推进**后本卡恢复接入；"领袖/灵体"两行在进度表里仍是 ⛔，不等整卡）
            var jc010 = Load("JC010_qingshoufeinandier");
            Assert.IsTrue(CardEffectFactory.IsRegistered("JC010_qingshoufeinandier"), "JC010 应已接入");
            var e010k = Find(jc010, "JC010_A_Keywords");
            Assert.IsNotNull(e010k, "JC010 应有领袖/灵体关键字授予（词条行为待规则文本落地，进度表标 ⛔）");
            Assert.AreEqual(EffectTiming.Continuous, e010k.Timing, "关键字以持续授予表达");
            var e010 = Find(jc010, "JC010_1_DestroyTapped");
            Assert.IsNotNull(e010, "JC010 应有 DestroyTapped 激活");
            Assert.AreEqual("快速", e010.ActivateWindow, "JC010 为快速行动");
            Assert.IsTrue(e010.ActivateCondition.Contains("横置本角色"), "JC010 代价=横置自身");
            Assert.IsTrue(HasRestriction(e010, TargetRestriction.Tapped), "JC010 目标应限定『已横置』");
            Assert.IsTrue(HasVerb(e010, ActionVerb.Destroy), "JC010 动作为消灭");
            Assert.IsFalse(HasVerb(e010, ActionVerb.Tap), "JC010 不应把『横置自身』写进 Actions（那是代价，由 ActivateCondition 承担）");

            // JC029：袭击1
            var jc029 = Load("JC029_xinshengxuezu");
            var e029 = Find(jc029, "JC029_1_Strike1");
            Assert.IsNotNull(e029, "JC029 应有袭击1");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e029.Timing, "袭击为现身触发");
            Assert.IsTrue(HasVerb(e029, ActionVerb.DealDamage), "袭击动作为造成伤害");

            // JC039：双激活（减费 / 潜伏本方王座会角色）
            var jc039 = Load("JC039_wangzuohuihuiji");
            var e039a = Find(jc039, "JC039_1_CostReduce");
            Assert.IsNotNull(e039a, "JC039 应有减费激活");
            Assert.AreEqual("费用减少", e039a.Actions[0].Args[0], "JC039① 应为费用减少");
            Assert.AreEqual("组织:王座会", e039a.Actions[0].Args[2], "JC039① 过滤串应为『组织:王座会』");
            Assert.AreEqual("一次性", e039a.Actions[0].Args[3], "JC039① 为一次性（下一张）");
            var e039b = Find(jc039, "JC039_2_LatentAlly");
            Assert.IsNotNull(e039b, "JC039 应有潜伏激活");
            Assert.AreEqual(3, e039b.Cost, "JC039② 费用为 3（快速行动3）");
            Assert.IsTrue(HasRestriction(e039b, TargetRestriction.Organization), "JC039② 目标应按 Organization 筛选");
            Assert.AreEqual("王座会", e039b.Target.Param, "JC039② Organization 参数");
            Assert.IsTrue(HasVerb(e039b, ActionVerb.Latent), "JC039② 动作为潜伏");

            // JC042：减费2（组织或血域，取其一）
            var jc042 = Load("JC042_morixintu");
            var e042 = Find(jc042, "JC042_1_CostReduce2");
            Assert.IsNotNull(e042, "JC042 应有减费效果");
            Assert.AreEqual("2", e042.Actions[0].Args[1], "JC042 减费量为 2");
            Assert.AreEqual("组织:鸣钟教派|域:血", e042.Actions[0].Args[2], "JC042 过滤串应为『或』语义（|）");

            // JC058：现身触发消灭本地区暗藏者
            var jc058 = Load("JC058_jianyidexingjing");
            var e058 = Find(jc058, "JC058_1_DestroyCovert");
            Assert.IsNotNull(e058, "JC058 应有消灭暗藏者效果");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e058.Timing, "JC058 为现身触发");
            Assert.AreEqual(TargetKind.Covert, e058.Target.Kind, "JC058 目标为暗藏者");
            Assert.IsTrue(e058.Target.IsLocalZone, "JC058 限本地区");

            // JC071：+1 防御直到回合结束（本地区、本方、非自身）
            var jc071 = Load("JC071_awalongyinshi");
            var e071 = Find(jc071, "JC071_1_DefenseUp");
            Assert.IsNotNull(e071, "JC071 应有加防效果");
            Assert.AreEqual("直到回合结束", e071.Duration, "JC071 为时效修正");
            Assert.IsTrue(e071.Target.IsLocalZone && HasRestriction(e071, TargetRestriction.Friendly), "JC071 限本地区本方");
            Assert.IsFalse(e071.Target.CanTargetSelf, "JC071 目标为『另一个』角色");
            Assert.AreEqual("防御", e071.Actions[0].Args[0], "JC071 授予防御");

            // JC075：公开 + 快速行动2 横置回手
            var jc075 = Load("JC075_xuedijiuyuandui");
            Assert.IsNotNull(Find(jc075, "JC075_1_Public"), "JC075 应有公开关键字");
            var e075 = Find(jc075, "JC075_2_ReturnAlly");
            Assert.IsNotNull(e075, "JC075 应有回手激活");
            Assert.AreEqual(2, e075.Cost, "JC075 费用为 2");
            Assert.IsTrue(HasVerb(e075, ActionVerb.Bounce), "JC075 动作为移回手牌");
            Assert.IsTrue(HasRestriction(e075, TargetRestriction.Friendly), "JC075 目标为本方角色");

            // JC088：现身触发（印刷费≤2）+ 快速行动2 潜伏
            var jc088 = Load("JC088_zhiyeshashou");
            var e088a = Find(jc088, "JC088_1_DestroyCheapCharacter");
            Assert.IsNotNull(e088a, "JC088 应有消灭廉价角色效果");
            Assert.IsTrue(HasRestriction(e088a, TargetRestriction.CostAtMost), "JC088 目标应限定印刷费用上限");
            Assert.AreEqual("2", e088a.Target.Param, "JC088 费用门槛=2");
            var e088b = Find(jc088, "JC088_2_Latent");
            Assert.IsNotNull(e088b, "JC088 应有潜伏激活");
            Assert.IsTrue(HasVerb(e088b, ActionVerb.Latent), "JC088 动作为潜伏");

            // JC090：结附地区 + 死亡触发放标志
            var jc090 = Load("JC090_linghunqiyue");
            Assert.IsTrue(jc090.TargetRules != null && jc090.TargetRules.Count > 0
                          && jc090.TargetRules[0].Kind == TargetKind.Area, "JC090 打出目标为地区");
            var e090 = Find(jc090, "JC090_1_MarkOnAllyDeath");
            Assert.IsNotNull(e090, "JC090 应有死亡触发放标志效果");
            Assert.AreEqual(EffectTiming.OnCardDied, e090.Timing, "JC090 挂死亡时机（P1 接线）");
            Assert.AreEqual("DiedAllyCharacter", e090.Condition.Predicate, "JC090 条件=本方角色死亡");
            Assert.IsTrue(HasVerb(e090, ActionVerb.PlaceToken), "JC090 动作为放置势力标志");

            // JC091 / JC102：行动阶段事务卡
            var jc091 = Load("JC091_mousha");
            Assert.IsTrue(jc091.LimitToActionPhaseOnly, "JC091 只能在行动阶段打出");
            var e091 = Find(jc091, "JC091_1_DestroyHuman");
            Assert.IsNotNull(e091, "JC091 应有消灭效果");
            Assert.IsTrue(HasRestriction(e091, TargetRestriction.CurrentSubCategory),
                "JC091 目标按**实例当前类别**（卡文只写\"人类角色\"；用户 2026-09 口径：写\"印刷类别为人类\"才读模型）");
            Assert.AreEqual("人类", e091.Target.Param, "JC091 目标为人类角色");
            Assert.IsTrue(HasVerb(e091, ActionVerb.Destroy), "JC091 动作为消灭");

            var jc102 = Load("JC102_yaohuo");
            Assert.IsTrue(jc102.LimitToActionPhaseOnly, "JC102 只能在行动阶段打出");
            var e102 = Find(jc102, "JC102_1_DealDamage");
            Assert.IsNotNull(e102, "JC102 应有伤害效果");
            Assert.AreEqual("1", e102.Actions[0].Args[0], "JC102 造成 1 点伤害");
            Assert.IsTrue(HasVerb(e102, ActionVerb.DealDamage), "JC102 动作为造成伤害");

            // JC103：减费1（梦境行者）
            var jc103 = Load("JC103_sangshilizhidehuajia");
            var e103 = Find(jc103, "JC103_1_CostReduce");
            Assert.IsNotNull(e103, "JC103 应有减费效果");
            Assert.AreEqual("组织:梦境行者", e103.Actions[0].Args[2], "JC103 过滤串应为『组织:梦境行者』");
            Assert.AreEqual("一次性", e103.Actions[0].Args[3], "JC103 为一次性");

            // JC112：公开 + 不能被装备结附 + 减费1（有域图标）
            var jc112 = Load("JC112_heimao");
            Assert.IsNotNull(Find(jc112, "JC112_A_Public"), "JC112 应有公开");
            var e112b = Find(jc112, "JC112_B_CannotBeAttached");
            Assert.IsNotNull(e112b, "JC112 应有『不能被装备结附』");
            Assert.AreEqual(EffectTiming.Static, e112b.Timing, "静止/禁止类用 Static");
            var e112 = Find(jc112, "JC112_1_CostReduceDomainCard");
            Assert.IsNotNull(e112, "JC112 应有减费效果");
            Assert.AreEqual("有域图标", e112.Actions[0].Args[2], "JC112 过滤串应为『有域图标』");
            Assert.IsTrue(e112.ActivateCondition.Contains("牺牲本角色"), "JC112 代价为牺牲自身");
        }

        // ==================== 行为：JC090 死亡触发端到端（含条件筛选） ====================

        public void TestP1Card_JC090_死亡触发放标志_端到端()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;

            string area = FirstAreaUid(game);
            var jc090 = Load("JC090_linghunqiyue");
            var att = game.CardManager.CreateCardInstance(jc090, 1);
            game.ZoneManager.MoveToAttachedZone(att, area, 1);      // 结附于地区（AttachedTo=null）

            // 敌方角色死亡 → JC090 条件（本方角色）不满足 → 不应入堆叠
            var enemyTmpl = new PlayerCardData { UID = "P1_enemy", CardName = "敌方角色", CategoryFlag = CardCategory.Character };
            enemyTmpl._effectsInjected = true;
            var enemy = game.CardManager.CreateCardInstance(enemyTmpl, 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);
            game.CardManager.DestroyInstance(enemy);
            System.Threading.Thread.Sleep(120);   // 给异步触发流程一点时间

            Assert.IsFalse(StackHas(game, att), "敌方角色死亡不应触发『每当一个本方角色死亡时』");

            // 本方角色死亡 → 条件满足 → 触发应入堆叠
            var allyTmpl = new PlayerCardData { UID = "P1_ally", CardName = "本方角色", CategoryFlag = CardCategory.Character };
            allyTmpl._effectsInjected = true;
            var ally = game.CardManager.CreateCardInstance(allyTmpl, 1);
            game.ZoneManager.MoveToArea(ally, area, 1);
            game.CardManager.DestroyInstance(ally);

            Assert.IsTrue(WaitUntil(() => StackHas(game, att)),
                "本方角色死亡应触发 JC090，并经 P1 的 OnCardDied 链路入堆叠");
        }

        private static bool StackHas(GameInitializer game, CardInstance src)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == src.UID) return true;
            return false;
        }

        // ==================== 行为：JC071 时效修正回合结束还原 ====================

        public void TestP1Card_JC071_加防回合结束还原()
        {
            var ee = new EffectEngine();
            // 时效还原链路需要 EffectEngine 能按 UID 取回实例（CM.GetInstance），故注入本测试的 CardManager。
            var cm = new CardManager(); cm._Ready();
            ee.CardManagerRef = cm;

            var tmpl = new PlayerCardData
            {
                UID = "P1_def", CardName = "加防目标", CategoryFlag = CardCategory.Character, Defense = 2,
            };
            tmpl._effectsInjected = true;
            var target = cm.CreateCardInstance(tmpl, 1);
            target.CurrentZone = CardZone.AreaZone;
            target.ResetToBaseValues(false);
            Assert.AreEqual(2, target.CurrentDefense, "前置：基础防御 2");

            var jc071 = Load("JC071_awalongyinshi");
            var effect = Find(jc071, "JC071_1_DefenseUp");
            ee.CurrentTurn = 5;
            ee.ResolveEffect(effect, target, target);       // source 与 target 同一张卡（本测试只看属性落地）
            Assert.AreEqual(3, target.CurrentDefense, "JC071 应 +1 防御");

            ee.ExpireTurnEndEffects(turn: 5);
            Assert.AreEqual(2, target.CurrentDefense, "回合结束应还原防御（时效修正栈）");
        }

        // ==================== 行为：减费过滤串（按新卡实际参数走一遍） ====================

        public void TestP1Card_减费过滤串命中与不命中()
        {
            var ee = new EffectEngine();

            // JC103：组织:梦境行者（一次性）
            var jc103 = Load("JC103_sangshilizhidehuajia");
            var args = Find(jc103, "JC103_1_CostReduce").Actions[0].Args;
            ee.ApplyCostModification(1, -1, 99, args[2], consumeOnce: args[3] == "一次性");

            var dreamTmpl = new PlayerCardData
            {
                UID = "P1_dream", CardName = "梦境牌", CategoryFlag = CardCategory.Character, Organization = "梦境行者",
            };
            dreamTmpl._effectsInjected = true;
            var dream = new CardInstance(dreamTmpl, "p1_dream_1") { OwnerId = 1, ControllerId = 1 };
            var otherTmpl = new PlayerCardData
            {
                UID = "P1_hunter", CardName = "猎手牌", CategoryFlag = CardCategory.Character, Organization = "猎魔人",
            };
            otherTmpl._effectsInjected = true;
            var other = new CardInstance(otherTmpl, "p1_hunter_1") { OwnerId = 1, ControllerId = 1 };

            Assert.AreEqual(-1, ee.GetCostModification(1, dream, dreamTmpl.CardName, dreamTmpl.Organization),
                "JC103 减费应命中【梦境行者】牌");
            Assert.AreEqual(0, ee.GetCostModification(1, other, otherTmpl.CardName, otherTmpl.Organization),
                "JC103 减费不应命中其它组织牌");

            // JC042：组织:鸣钟教派|域:血（或语义，不叠加）
            var jc042 = Load("JC042_morixintu");
            var a42 = Find(jc042, "JC042_1_CostReduce2").Actions[0].Args;
            var ee2 = new EffectEngine();
            ee2.ApplyCostModification(2, -2, 99, a42[2], consumeOnce: true);
            var bellTmpl = new PlayerCardData
            {
                UID = "P1_bell", CardName = "鸣钟牌", CategoryFlag = CardCategory.Sorcery, Organization = "鸣钟教派",
            };
            bellTmpl.MagicDomains = new Array<MagicDomain> { MagicDomain.Blood };   // 同时满足两个条件
            bellTmpl._effectsInjected = true;
            var bell = new CardInstance(bellTmpl, "p1_bell_1") { OwnerId = 2, ControllerId = 2 };
            Assert.AreEqual(-2, ee2.GetCostModification(2, bell, bellTmpl.CardName, bellTmpl.Organization),
                "JC042：同时满足『鸣钟教派』与『血图标』时只应减 2（不叠加为 -4）");

            // 【2026-09 用户实测·JC042】用户报"降低费用效果变成给**多张**卡降低费用了，只降低**下一张**"。
            // 这里把"一次性"语义钉死：消费（＝打出那张牌）之后，同一条修正必须消失，另一张命中牌不得再享受。
            ee2.ConsumeCostModification(2, bell, bellTmpl.Organization);
            Assert.AreEqual(0, ee2.GetCostModification(2, bell, bellTmpl.CardName, bellTmpl.Organization),
                "【一次性】消费后该修正被移除 ⇒ 同一张牌不再减费");

            var ee3 = new EffectEngine();
            ee3.ApplyCostModification(2, -2, 99, a42[2], consumeOnce: true);
            var bell2Tmpl = new PlayerCardData
            {
                UID = "P1_bell2", CardName = "鸣钟牌2", CategoryFlag = CardCategory.Sorcery, Organization = "鸣钟教派",
            };
            bell2Tmpl.MagicDomains = new Array<MagicDomain> { MagicDomain.Blood };
            bell2Tmpl._effectsInjected = true;
            var bell2 = new CardInstance(bell2Tmpl, "p1_bell2_1") { OwnerId = 2, ControllerId = 2 };
            Assert.AreEqual(-2, ee3.GetCostModification(2, bell2, bell2Tmpl.CardName, bell2Tmpl.Organization),
                "第一张命中牌享受 -2");
            ee3.ConsumeCostModification(2, bell2, bell2Tmpl.Organization);   // ＝打出第一张
            var bell3 = new CardInstance(bell2Tmpl, "p1_bell3_1") { OwnerId = 2, ControllerId = 2 };
            Assert.AreEqual(0, ee3.GetCostModification(2, bell3, bell2Tmpl.CardName, bell2Tmpl.Organization),
                "【用户实测回归】打掉第一张后，第二张命中牌**不得**再减费（一次性只作用于下一张）");
            Assert.AreEqual(0, ee3.GetCostModification(2, bell2, bell2Tmpl.CardName, bell2Tmpl.Organization),
                "已打出的那张自身也不应残留减免");
        }

        // ==================== 行为：JC039② 令本方【王座会】角色潜伏（端到端） ====================

        /// <summary>
        /// 用户实测「让己方王座会角色潜伏的效果没有生效」的**规则侧回归**：结附本方秘社的 JC039
        /// 激活②后，目标本方王座会角色必须翻面为暗藏者（`IsCovert`）。
        /// （UI 侧"原地翻面没刷卡面"由 `Arena.Events.OnCardStateChangedHandler` 修，headless 覆盖不到。）
        /// </summary>
        public void TestP1Card_JC039_令本方王座会角色潜伏_端到端()
        {
            var game = NewGame();
            string area = FirstAreaUid(game);

            // 费用：给 P1 三张资产（每张 1 费）
            for (int i = 0; i < 4; i++)
            {
                var assetTmpl = new PlayerCardData
                {
                    UID = $"P1_jc039_asset{i}", CardName = "探针资产", Cost = 0,
                    CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                assetTmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(assetTmpl, 1);
                game.ZoneManager.MoveToAsset(asset, 1);
            }

            // 结附于**本方秘社**（与本方秘社实例建立双向引用，与结算落地同款）
            var emblemTmpl = Load("JC039_wangzuohuihuiji");
            var emblem = game.CardManager.CreateCardInstance(emblemTmpl, 1);
            game.ZoneManager.MoveToHand(emblem, 1);
            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1);
            Assert.IsTrue(councils.Count > 0, "开局 P1 应有 1 张秘社");
            emblem.IsAttached = true;
            emblem.AttachedTo = councils[0];
            councils[0].AttachedCards.Add(emblem);
            game.ZoneManager.MoveToAttachedZone(emblem, councils[0].ZoneDetailId ?? "", 1);

            // 目标：本方【王座会】角色
            var allyTmpl = new PlayerCardData
            {
                UID = "P1_jc039_ally", CardName = "王座会角色", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1, Organization = "王座会",
            };
            allyTmpl._effectsInjected = true;
            var ally = game.CardManager.CreateCardInstance(allyTmpl, 1);
            game.ZoneManager.MoveToArea(ally, area, 1);

            var e = Find(emblemTmpl, "JC039_2_LatentAlly");
            game.TurnManager.PriorityPlayerId = 1;
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(emblem, e, ally, game.CardManager, game.PlayerManager),
                "本方王座会角色应是合法目标（Organization + Friendly 限制）");
            Assert.IsTrue(ActivateEligibility.CanActivate(emblem, game.TurnManager, game.PlayerManager, game.CardManager),
                "结附本方秘社的徽记②应可发动");

            game.EffectEngine.ActivateAbility(emblem, e, ally);
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(ally.IsCovert, "目标本方【王座会】角色应被潜伏（翻面为暗藏者）");
            Assert.AreEqual(CardZone.AreaZone, ally.CurrentZone, "潜伏不改变所在区域");
        }
    }
}
