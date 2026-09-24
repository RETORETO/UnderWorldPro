using System.Collections.Generic;
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
    /// 【效果级推进·第 3 批】前提条件已具备的效果一次性落地（10 张卡）：
    ///  · JC026 洛特尼科夫②：进场检索装备牌（展示 + 洗牌）；
    ///  · JC053 鸣钟教派徽记①：横置 → 下一张【鸣钟教派】牌费用 -1（组织过滤串）；
    ///  · JC066 封魔瓶瑟琳娜①：进场把本地区目标角色/暗藏者封印在自己身上；
    ///  · JC076 悟道僧人②：现身触发抓一张；
    ///  · JC085 复生行尸①：资产区【死】→ 白星（先手势力）；
    ///  · JC099 徘徊的鬼魂②：现身把牌库顶一张封印在本地区目标敌方角色上；
    ///  · JC100 尖牙幻象②：现身把自身封印在本地区目标角色上；
    ///  · JC108 乔治·帕佐①：快速行动1横置 → 牌库顶一张封印在目标角色上；
    ///  · JC109 沉沦的巴德勒①：结束步骤触发 → 检索灵体角色牌封印在自己上并洗牌；
    ///  · JC119 守秘人①：现身把一张手牌背面朝上封印在自己身上（信息隔离）。
    ///
    /// 每张卡其余**未实现**的效果行（灵体/声望/图标镜像/动态缩放/免费打出/可选支付…）在进度表里标 ⛔ 并写明原因，
    /// 本套件只锁定"已实现的那一条"。
    /// </summary>
    public class P2MoreCardsTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
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

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 67 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            // 测试无 Arena（生产环境 _view = Arena）：装一个"全部放弃"的触发视图，
            // 避免"带进场触发的卡进场"时触发排序窗口拿到 null 视图而崩（这些用例都自己显式结算目标效果）。
            game.TurnManager.SetTriggerView(new 放弃触发视图());
            return game;
        }

        /// <summary>测试用触发视图：所有触发一律放弃（不通过 onEnterStack 入栈），仅推进流程。</summary>
        private sealed class 放弃触发视图 : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                System.Collections.Generic.List<StackEffectInstance> triggers,
                System.Action<System.Collections.Generic.List<StackEffectInstance>> onEnterStack,
                System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(StackEffectInstance trigger,
                System.Collections.Generic.List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(System.Collections.Generic.List<StackEffectInstance> pushed) { }
        }

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        private static PlayerCardData Probe(string uid, int cost = 1, string subCategory = null,
            CardCategory category = CardCategory.Character, string organization = "",
            MagicDomain[] domains = null, bool inherentSpirit = false)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Organization = organization,
            };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
            if (domains != null) foreach (var d in domains) tmpl.MagicDomains.Add(d);
            if (inherentSpirit)
            {
                // 模板固有的"灵体"关键字（供 `DeckSearchFilter` 的 `关键字:灵体` 命中：HasInherentKeyword 走模板持续授予）
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_kw",
                    Timing = EffectTiming.Continuous,
                    Scope = ZoneScope.AreaZone,
                    ContinuousGrants = new Array<EffectGrant>
                    {
                        new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                    },
                });
            }
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, card);
            return card;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        // ==================== 结构：10 张卡的效果数据与卡文一致 ====================

        public void TestMoreCards_结构与卡文一致()
        {
            // JC026 ②：进场检索装备牌
            var e026 = Find(Load("JC026_luotenikefu"), "JC026_2_SearchEquipment");
            Assert.IsNotNull(e026, "JC026 应有进场检索");
            Assert.AreEqual(EffectTiming.EnterTrigger, e026.Timing, "JC026 为进场触发");
            Assert.AreEqual("子类别:装备", e026.Actions[0].Args[0], "JC026 筛选=装备牌");
            Assert.IsTrue(e026.Actions[0].Args[2].Contains("展示"), "JC026 应展示该牌");

            // JC053 ①：减费（组织:鸣钟教派）
            var e053 = Find(Load("JC053_mingzhongjiaopaihuiji"), "JC053_1_CostReduce");
            Assert.IsNotNull(e053, "JC053 应有减费激活");
            Assert.AreEqual("费用减少", e053.Actions[0].Args[0], "JC053 为费用减少");
            Assert.AreEqual("组织:鸣钟教派", e053.Actions[0].Args[2], "JC053 过滤串=组织:鸣钟教派");

            // JC066 ①：目标封在瑟琳娜身上（本地区，含暗藏者）
            var e066 = Find(Load("JC066_fengmopingselinna"), "JC066_1_SealTarget");
            Assert.IsNotNull(e066, "JC066 应有进场封印");
            Assert.AreEqual(ActionVerb.Seal, e066.Actions[0].Verb, "JC066 动作为封印");
            Assert.AreEqual("目标", e066.Actions[0].Args[0], "JC066 被封印者=目标（卡文『将本地区的目标角色或暗藏者封印于瑟琳娜上』）");
            Assert.AreEqual("自身", e066.Actions[0].Args[1], "JC066 宿主=瑟琳娜自身");
            Assert.IsTrue(e066.Target.IsLocalZone, "JC066 目标限本地区");
            Assert.IsTrue(e066.Target.IncludeCovert, "JC066 卡文含暗藏者 → 允许选暗藏者");

            // JC076 ②：现身抓一张
            var e076 = Find(Load("JC076_wudaosengren"), "JC076_2_UncoverDraw");
            Assert.IsNotNull(e076, "JC076 应有现身抓牌");
            Assert.AreEqual(ActionVerb.Draw, e076.Actions[0].Verb, "JC076 动作为抓牌");

            // JC085 ①：条件持续（资产区【死】→ 白星）
            var e085 = Find(Load("JC085_fushengxingshi"), "JC085_1_WhiteStar");
            Assert.IsNotNull(e085, "JC085 应有条件持续");
            Assert.AreEqual(PrereqKind.DomainLoyalty, e085.PrereqSpec.Kind, "JC085 前提=领域忠诚");
            Assert.AreEqual(MagicDomain.Death, e085.PrereqSpec.Domain, "JC085 前提=死领域");
            Assert.AreEqual("先手势力", e085.ContinuousGrants[0].Value, "JC085 授予白星=先手势力");

            // JC099 ②：牌库顶 → 本地区敌方角色
            var e099 = Find(Load("JC099_paihuaideguihun"), "JC099_2_SealDeckTopOnEnemy");
            Assert.IsNotNull(e099, "JC099 应有现身封印");
            Assert.AreEqual("牌库顶", e099.Actions[0].Args[0], "JC099 被封印者=牌库顶那张");
            Assert.IsTrue(e099.Target.IsLocalZone, "JC099 目标限本地区");
            Assert.IsTrue(e099.Target.Restrictions.Contains(TargetRestriction.Enemy), "JC099 目标须为敌方");

            // JC100 ②：自身 → 本地区目标角色
            var e100 = Find(Load("JC100_jianyahuanxiang"), "JC100_2_SealSelfOnCharacter");
            Assert.IsNotNull(e100, "JC100 应有现身封印");
            Assert.AreEqual("自身", e100.Actions[0].Args[0], "JC100 被封印者=自身");
            Assert.IsTrue(e100.Target.IsLocalZone, "JC100 目标限本地区");
            Assert.IsFalse(e100.Forced, "卡文『你可以』→ 非强制");

            // JC108 ①：快速行动1，横置 → 牌库顶 → 目标角色（无本地区限制）
            var e108 = Find(Load("JC108_qiaozhipazuozhimengren"), "JC108_1_SealDeckTop");
            Assert.IsNotNull(e108, "JC108 应有快速行动封印");
            Assert.AreEqual(EffectTiming.CharacterActivated, e108.Timing, "JC108 为角色激活");
            Assert.AreEqual("快速", e108.ActivateWindow, "JC108 为快速行动");
            Assert.AreEqual(1, e108.Cost, "JC108 费用=1");
            Assert.AreEqual("横置本角色", e108.ActivateCondition, "JC108 代价=横置自身");
            Assert.IsFalse(e108.Target.IsLocalZone, "JC108 卡文无『本地区』→ 不应加地区限制");

            // JC109 ①：结束步骤触发 → 检索灵体角色牌封印在自己上
            var e109 = Find(Load("JC109_chenlundebadelei"), "JC109_1_EndStepSealSpirit");
            Assert.IsNotNull(e109, "JC109 应有结束步骤触发");
            Assert.AreEqual(EffectTiming.OnTurnEnded, e109.Timing, "JC109 为结束步骤（OnTurnEnded）");
            Assert.AreEqual("关键字:灵体;类别:角色", e109.Actions[0].Args[0], "JC109 筛选=灵体角色牌");
            Assert.AreEqual("封印", e109.Actions[0].Args[1], "JC109 去向=封印");
            Assert.AreEqual("自身", e109.Actions[0].Args[2], "JC109 宿主=自身");

            // JC119 ①：手牌背面朝上封印在自己身上
            var e119 = Find(Load("JC119_shoumiren"), "JC119_1_SealHandFaceDown");
            Assert.IsNotNull(e119, "JC119 应有现身封印手牌");
            Assert.AreEqual("手牌", e119.Actions[0].Args[0], "JC119 被封印者=手牌");
            Assert.AreEqual("自身", e119.Actions[0].Args[1], "JC119 宿主=自身");
            Assert.IsTrue(e119.Actions[0].Args[2].Contains("背面朝上"), "JC119 应背面朝上（信息隔离）");
        }

        // ==================== JC026②：检索装备牌 ====================

        public void TestJC026_检索装备牌并展示()
        {
            var game = NewGame();
            var equip = PutOnDeckTop(game, Probe("TS_026_equip", cost: 2, subCategory: "装备",
                category: CardCategory.Attachment));
            var other = PutOnDeckTop(game, Probe("TS_026_human", cost: 2, subCategory: "人类"));

            var revealed = new List<CardInstance>();
            System.Action<CardInstance, int> handler = (c, by) => revealed.Add(c);
            UiNotificationBus.Instance.CardRevealed += handler;
            try
            {
                var src = game.CardManager.CreateCardInstance(Load("JC026_luotenikefu"), 1);
                int handBefore = game.TurnManager.GetHandOf(1).Count;
                // 【稳健性】按 EffectId 取效果，而非"下标 0"（新增效果会打乱下标）
            EffectData search026 = null;
            foreach (var ef in src.PlayerTemplate.EffectDataList)
                if (ef != null && ef.EffectId == "JC026_2_SearchEquipment") search026 = ef;
            Assert.IsNotNull(search026, "JC026 应有检索装备牌效果");
            EffectEngine.Instance.ResolveEffect(search026, src, null);

                Assert.AreEqual(1, revealed.Count, "应公开展示取到的装备牌");
                Assert.IsTrue(ReferenceEquals(equip, revealed[0]), "展示的应是那张装备牌");
                Assert.AreEqual(CardZone.Hand, equip.CurrentZone, "装备牌入手");
                Assert.AreEqual(CardZone.Deck, other.CurrentZone, "非装备牌留在牌库");
                Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
            }
            finally { UiNotificationBus.Instance.CardRevealed -= handler; }
        }

        // ==================== JC053①：减费（组织过滤串） ====================

        public void TestJC053_减费按组织过滤并一次性消费()
        {
            var game = NewGame();
            int pid = 61;
            EffectEngine.Instance.ApplyCostModification(pid, -1, 5, "组织:鸣钟教派", consumeOnce: true);

            Assert.AreEqual(-1, EffectEngine.Instance.GetCostModification(pid, "任意牌", "鸣钟教派"),
                "鸣钟教派组织的牌应减 1");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(pid, "任意牌", "王座会"),
                "非鸣钟教派组织不减费");

            EffectEngine.Instance.ConsumeCostModification(pid, "组织:鸣钟教派");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(pid, "任意牌", "鸣钟教派"),
                "一次性消费后归零");
        }

        // ==================== JC066①：进场封印（本地区目标 → 瑟琳娜身上） ====================

        public void TestJC066_进场把目标封印在自己身上()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var serena = OnField(game, Load("JC066_fengmopingselinna"), 1, area);
            var victim = OnField(game, Probe("TS_066_victim"), 2, area);

            EffectEngine.Instance.ResolveEffect(Find(serena.PlayerTemplate, "JC066_1_SealTarget"), serena, victim);

            Assert.AreEqual(CardZone.SealedZone, victim.CurrentZone, "目标角色应进入封印区（它是封印卡）");
            Assert.IsTrue(ReferenceEquals(serena, victim.SealedBy), "宿主应是瑟琳娜自己（卡文『封印于瑟琳娜上』）");
            Assert.IsTrue(serena.SealedCards.Contains(victim), "瑟琳娜应记录被封印的目标");
            Assert.IsTrue(victim.IsSeal && serena.IsSealed, "封印卡/被封印 标记都应置位");
            Assert.AreEqual(area, serena.ZoneDetailId, "瑟琳娜本身留在原地区参战，不被封走");
        }

        /// <summary>暗藏者也可被封印（卡文"目标角色或暗藏者"）：`IncludeCovert=true` 时应通过目标合法性判定。</summary>
        public void TestJC066_目标可为暗藏者()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var serena = OnField(game, Load("JC066_fengmopingselinna"), 1, area);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_066_covert"), 2);
            covert.SetCovert(true);
            game.ZoneManager.MoveToArea(covert, area, 2);

            var e = Find(serena.PlayerTemplate, "JC066_1_SealTarget");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(serena, e, covert, null),
                "JC066 的封印目标应允许暗藏者");
        }

        // ==================== JC076②：现身抓一张 ====================

        public void TestJC076_现身抓一张牌()
        {
            var game = NewGame();
            var src = game.CardManager.CreateCardInstance(Load("JC076_wudaosengren"), 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            EffectEngine.Instance.ResolveEffect(Find(src.PlayerTemplate, "JC076_2_UncoverDraw"), src, null);

            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "牌库 -1");
        }

        // ==================== JC085①：资产区【死】→ 白星 ====================

        public void TestJC085_资产区死领域时获得白星()
        {
            var game = NewGame();
            var area = FirstArea(game);

            // ① 资产区没有【死】→ 前提不满足 → 不应有白星
            var zombie = OnField(game, Load("JC085_fushengxingshi"), 1, area);
            Assert.AreEqual(0, zombie.GetAttribute("先手势力"), "资产区无【死】时不应有白星");

            // ② 放入一个【死】领域资产（忠诚 = 该领域资产张数）→ 重判前提 → 白星生效
            var asset = game.CardManager.CreateCardInstance(
                Probe("TS_085_deathasset", domains: new[] { MagicDomain.Death }), 1);
            game.ZoneManager.MoveToAsset(asset, 1);
            EffectEngine.Instance.ReevaluatePrereqs(zombie.ControllerId);

            Assert.AreEqual(1, zombie.GetAttribute("先手势力"),
                "资产区有【死】→ 复生行尸应获得白星（先手势力+1）");
        }

        // ==================== JC099②：牌库顶 → 本地区敌方角色 ====================

        public void TestJC099_牌库顶一张封印在敌方角色上()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var ghost = OnField(game, Load("JC099_paihuaideguihun"), 1, area);
            var enemy = OnField(game, Probe("TS_099_enemy"), 2, area);
            var top = PutOnDeckTop(game, Probe("TS_099_top", cost: 3));
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            EffectEngine.Instance.ResolveEffect(Find(ghost.PlayerTemplate, "JC099_2_SealDeckTopOnEnemy"), ghost, enemy);

            Assert.AreEqual(CardZone.SealedZone, top.CurrentZone, "牌库顶那张应被封印");
            Assert.IsTrue(ReferenceEquals(enemy, top.SealedBy), "宿主=目标敌方角色");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "该牌应从牌库列表移除");
            Assert.IsFalse(game.DeckManager.GetPlayer1DeckData().Contains(top), "牌库里不应还留着它");
        }

        // ==================== JC100②：自身封印在本地区目标角色上 ====================

        public void TestJC100_自身封印在目标角色上()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var illusion = OnField(game, Load("JC100_jianyahuanxiang"), 1, area);
            var host = OnField(game, Probe("TS_100_host"), 1, area);

            EffectEngine.Instance.ResolveEffect(Find(illusion.PlayerTemplate, "JC100_2_SealSelfOnCharacter"), illusion, host);

            Assert.AreEqual(CardZone.SealedZone, illusion.CurrentZone, "尖牙幻象自身应进封印区");
            Assert.IsTrue(ReferenceEquals(host, illusion.SealedBy), "宿主=目标角色");
            Assert.IsTrue(host.SealedCards.Contains(illusion), "宿主应记录该封印卡");
        }

        // ==================== JC108①：快速行动1横置 → 牌库顶 → 目标角色 ====================

        public void TestJC108_快速行动封印牌库顶()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var george = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, area);
            var target = OnField(game, Probe("TS_108_target"), 2, area);
            var top = PutOnDeckTop(game, Probe("TS_108_top", cost: 2));

            EffectEngine.Instance.ResolveEffect(Find(george.PlayerTemplate, "JC108_1_SealDeckTop"), george, target);

            Assert.AreEqual(CardZone.SealedZone, top.CurrentZone, "牌库顶那张应被封印");
            Assert.IsTrue(ReferenceEquals(target, top.SealedBy), "宿主=目标角色（无本地区限制）");
        }

        // ==================== JC109①：结束步骤检索灵体角色牌并封印在自己上 ====================

        public void TestJC109_结束步骤检索灵体角色并封印自己()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var badler = OnField(game, Load("JC109_chenlundebadelei"), 1, area);
            var spirit = PutOnDeckTop(game, Probe("TS_109_spirit", cost: 2, inherentSpirit: true));
            var human = PutOnDeckTop(game, Probe("TS_109_human", cost: 2, subCategory: "人类"));
            var deckBefore = game.DeckManager.GetPlayer1DeckCount();

            EffectEngine.Instance.ResolveEffect(Find(badler.PlayerTemplate, "JC109_1_EndStepSealSpirit"), badler, null);

            Assert.AreEqual(CardZone.SealedZone, spirit.CurrentZone, "灵体角色牌应被封印");
            Assert.IsTrue(ReferenceEquals(badler, spirit.SealedBy), "宿主=巴德勒自己（Args[2]=自身）");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "取走的牌离开牌库");
            Assert.AreEqual(CardZone.Deck, human.CurrentZone, "非灵体牌不应被取走");
        }

        // ==================== JC119①：手牌背面朝上封印 + 信息隔离 ====================

        public void TestJC119_手牌背面朝上封印且对对手隐藏()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var keeper = OnField(game, Load("JC119_shoumiren"), 1, area);
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            Assert.IsTrue(handBefore > 0, "前置：玩家1 有手牌");

            EffectEngine.Instance.ResolveEffect(Find(keeper.PlayerTemplate, "JC119_1_SealHandFaceDown"), keeper, null);

            Assert.AreEqual(handBefore - 1, game.TurnManager.GetHandOf(1).Count, "应从手牌取走一张");
            Assert.AreEqual(1, keeper.SealedCards.Count, "守秘人上应有 1 张封印牌");
            var sealedCard = keeper.SealedCards[0];
            Assert.IsTrue(sealedCard.IsSealedFaceDown, "『背面朝上』应置位信息隔离标记");
            Assert.AreEqual(CardZone.SealedZone, sealedCard.CurrentZone, "封印牌应进封印区");

            // 信息隔离：控制者可见，对手只收到占位
            var r = global::ZoneManager.ProjectCard(sealedCard, CardZone.SealedZone, sealedCard.ControllerId);
            Assert.IsTrue(r.IsSealedFaceDown, "投影应携带背面标记");
            Assert.IsFalse(SnapshotFilter.IsHiddenFor(r, 1), "封印者本人应能检视");
            Assert.IsTrue(SnapshotFilter.IsHiddenFor(r, 2), "对手不能检视背面朝上的封印牌");
        }

        /// <summary>联机路径：JC119 取手牌经 `SelectHandCard` 网关（候选只发给控制者本人）。</summary>
        public void TestJC119_联机经网关选一张手牌()
        {
            var game = NewGame();
            var gw = new AutoPickGateway();
            game.SetInputGateway(gw);
            var area = FirstArea(game);
            var keeper = OnField(game, Load("JC119_shoumiren"), 1, area);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ResolveEffectAsync(Find(keeper.PlayerTemplate, "JC119_1_SealHandFaceDown"), keeper, null)
                .Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应发起一次选牌请求");
            Assert.AreEqual(ChoiceRequest.Kind.SelectHandCard, gw.Requests[0].RequestKind, "请求种类=SelectHandCard");
            Assert.AreEqual(1, gw.Requests[0].PlayerId, "应问封印者本人");
            Assert.AreEqual(handBefore, gw.Requests[0].OptionUids.Count, "候选=全部手牌");
            Assert.AreEqual(1, keeper.SealedCards.Count, "选中的牌应被封印");
        }

        // ==================== JC054②：结束步骤强制潜伏 ====================

        public void TestJC054_结束步骤强制潜伏()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var worm = OnField(game, Load("JC054_menggusiwangruchong"), 1, area);
            Assert.IsFalse(worm.IsCovert, "前置：正面在场");

            var e = Find(worm.PlayerTemplate, "JC054_2_EndStepLatent");
            Assert.IsNotNull(e, "JC054 应有结束步骤潜伏");
            Assert.AreEqual(EffectTiming.OnTurnEnded, e.Timing, "JC054② 为结束步骤（OnTurnEnded）");
            Assert.IsTrue(e.Forced, "卡文『强制触发』→ Forced=true（触发窗口不可放弃）");

            EffectEngine.Instance.ResolveEffect(e, worm, null);

            Assert.IsTrue(worm.IsCovert, "结束步骤应把蒙古死亡蠕虫翻面为暗藏者（潜伏）");
        }

        /// <summary>离场护栏：已离场的来源不做"场上动作"（触发语义规则：潜伏只对场上卡有意义）。</summary>
        public void TestJC054_离场后不潜伏()
        {
            var game = NewGame();
            var worm = game.CardManager.CreateCardInstance(Load("JC054_menggusiwangruchong"), 1);
            game.ZoneManager.MoveToGraveyard(worm, 1);

            EffectEngine.Instance.ResolveEffect(Find(worm.PlayerTemplate, "JC054_2_EndStepLatent"), worm, null);

            Assert.IsFalse(worm.IsCovert, "不在场上的来源不应被翻面（离场护栏）");
        }

        /// <summary>记录请求并自动选第一张的网关（联机路径断言用）。</summary>
        private sealed class AutoPickGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public System.Threading.Tasks.Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                string uid = (req?.OptionUids != null && req.OptionUids.Count > 0) ? req.OptionUids[0] : null;
                return System.Threading.Tasks.Task.FromResult(new ChoiceResult
                {
                    RequestId = req?.RequestId, SelectedUid = uid, Cancelled = false,
                });
            }
        }
    }
}
