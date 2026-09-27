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
    /// 【P3·K10】**额外费用**（用户 2026-09 口径 A21–A24）：
    ///  · 额外费用＝**打出这张牌本身的费用之一**：在**付费阶段**支付、**卡入堆叠之前**付清，
    ///    **不在堆叠里结算、不可被响应**（打出的"支付费用阶段"的一部分）；
    ///  · 三种形态：
    ///    ① `SacrificeCharacter`（**JC049 深渊细语**）——**必须支付**；**无合法牺牲对象则不能打出**；
    ///       牺牲者由**打出者自己选择**（候选＝本方场上正面角色；口径与 P3·K22 牺牲代价同一实现）；
    ///    ② `PayTargetCost`（**JC135 余震**）——X ＝**先选定目标**的**印刷费用**；0 费目标 → **不支付**；
    ///       合法目标费用超出可用费用 → **不能打出**；
    ///    ③ `RevealHandCards`（JC081，下一批）——可选，不展示＝全额支付。
    /// 覆盖卡：**JC049 深渊细语**（额外费用行）、**JC135 余震**（两行整体）。
    /// </summary>
    public class P4ExtraCostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 1357 };
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

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, CardCategory category, int cost = 0,
            CardColor? color = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 1 : -1,
            };
            if (color.HasValue) tmpl.Colors.Add(color.Value);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>给玩家放 n 张**无色**资产（未横置）→ 只提供费用，不提供忠诚。</summary>
        private static void GiveMana(GameInitializer game, int playerId, int n, string tag = "mana",
            CardColor? color = null)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_k10_{tag}_{playerId}_{i}", CardCategory.Attachment, color: color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>把一张 JC 卡放进某玩家手牌（打出流程的前置）。</summary>
        private static CardInstance IntoHand(GameInitializer game, string fileName, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(Load(fileName), playerId);
            game.ZoneManager.MoveToHand(card, playerId);
            return card;
        }

        private static void ReadyToPlay(GameInitializer game, int playerId, bool actionPhase)
        {
            game.TurnManager.PriorityPlayerId = playerId;
            if (actionPhase) game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        /// <summary>把一个附属结附到宿主上（供 JC135 的"目标附属"）。</summary>
        private static CardInstance AttachProbe(GameInitializer game, CardInstance host, int cost, string uid)
        {
            var att = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment, cost), host.ControllerId);
            att.IsAttached = true;
            att.AttachedTo = host;
            host.AttachedCards.Add(att);
            game.ZoneManager.MoveToAttachedZone(att, host.ZoneDetailId ?? FirstArea(game), host.ControllerId);
            return att;
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestK10_结构()
        {
            var jc049 = Load("JC049_shenyuanxiyu");
            Assert.IsNotNull(jc049.ExtraCost, "JC049 声明了额外费用");
            Assert.AreEqual(ExtraCostKind.SacrificeCharacter, jc049.ExtraCost.Kind, "额外费用＝牺牲一个角色");
            Assert.IsTrue(jc049.ExtraCost.Mandatory, "**必须支付**（A21：无合法牺牲对象则不能打出）");

            var jc135 = Load("JC135_yuzhen");
            Assert.IsNotNull(jc135.ExtraCost, "JC135 声明了额外费用");
            Assert.AreEqual(ExtraCostKind.PayTargetCost, jc135.ExtraCost.Kind, "额外费用＝支付 X 费（X＝目标印刷费用）");
            Assert.IsTrue(jc135.ExtraCost.Mandatory, "必须支付");
            Assert.AreEqual(1, jc135.TargetRules.Count, "打出目标＝一条规则");
            Assert.AreEqual(TargetKind.Attachment, jc135.TargetRules[0].Kind, "目标＝附属");

            var e135 = FindEffect(jc135, "JC135_1_DestroyAttachmentCostX");
            Assert.IsNotNull(e135, "JC135 应有『消灭目标附属』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e135.Timing, "事务卡打出即结算");
            Assert.AreEqual(PlayTiming.Response, e135.PlayTiming, "快速行动");
            Assert.AreEqual(ActionVerb.Destroy, e135.Actions[0].Verb, "动作＝消灭");
        }

        public void TestK10_需要玩家选择的额外费用()
        {
            var game = NewGame();
            Assert.IsTrue(game.TurnManager.NeedsExtraCostPlayerChoice(Load("JC049_shenyuanxiyu")),
                "JC049 需要玩家先选牺牲者（View 据此弹窗）");
            Assert.IsFalse(game.TurnManager.NeedsExtraCostPlayerChoice(Load("JC135_yuzhen")),
                "JC135 的 X 由目标费用决定，不需要前置选择");
        }

        // ==================== ② JC049：无合法牺牲对象 ⇒ 不能打出 ====================

        public void TestK10_JC049无牺牲对象不能打出()
        {
            var game = NewGame();
            GiveMana(game, 1, 4, "jc049_loy", CardColor.Red);   // 忠诚(红 2) + 费用 2
            var card = IntoHand(game, "JC049_shenyuanxiyu", 1);
            ReadyToPlay(game, 1, actionPhase: true);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID), "场上没有可牺牲的角色 → 不能打出");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣任何费用");
        }

        // ==================== ③ JC049：牺牲在入堆叠之前、由打出者选择 ====================

        public void TestK10_JC049牺牲先于入堆叠()
        {
            var game = NewGame();
            GiveMana(game, 1, 4, "jc049_loy2", CardColor.Red);
            string area = FirstArea(game);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_k10_victim", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(victim, area, 1);
            var keeper = game.CardManager.CreateCardInstance(Probe("TS_k10_keeper", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(keeper, area, 1);

            var card = IntoHand(game, "JC049_shenyuanxiyu", 1);
            ReadyToPlay(game, 1, actionPhase: true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, null, -1, victim.UID),
                "指定牺牲者 → 应能打出");

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "被牺牲者进墓地（牺牲＝死亡）");
            Assert.AreEqual(CardZone.AreaZone, keeper.CurrentZone, "未被选中的角色仍在场上");
            Assert.IsFalse(game.StackManager.IsEmpty, "卡已入堆叠（等待响应结算）");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "常规费用 2 已支付");
        }

        // ==================== ④ JC049：非法指定 ⇒ fail-closed ====================

        public void TestK10_JC049非法牺牲候选被拒()
        {
            var game = NewGame();
            GiveMana(game, 1, 4, "jc049_loy3", CardColor.Red);
            string area = FirstArea(game);
            var mine = game.CardManager.CreateCardInstance(Probe("TS_k10_mine", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(mine, area, 1);
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_k10_enemy", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);

            var card = IntoHand(game, "JC049_shenyuanxiyu", 1);
            ReadyToPlay(game, 1, actionPhase: true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, null, -1, enemy.UID),
                "指定**敌方**角色作牺牲 → 拒绝打出（fail-closed）");
            Assert.AreEqual(CardZone.AreaZone, enemy.CurrentZone, "敌方角色未受影响");
            Assert.AreEqual(CardZone.AreaZone, mine.CurrentZone, "本方角色也未受影响");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
        }

        // ==================== ⑤ JC135：0 费附属 ⇒ 不支付额外费用 ====================

        public void TestK10_JC135零费附属不支付额外费用()
        {
            var game = NewGame();
            GiveMana(game, 1, 2, "jc135_m0");
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_k10_host", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var att = AttachProbe(game, host, cost: 0, uid: "TS_k10_att0");

            var card = IntoHand(game, "JC135_yuzhen", 1);
            ReadyToPlay(game, 1, actionPhase: false);   // 快速行动：有优先权即可
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, att.UID), "0 费附属 → 可打出且不付额外费用");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "只支付基础费用 2（额外费用 0）");
        }

        // ==================== ⑥ JC135：X ＝目标印刷费用 ====================

        public void TestK10_JC135支付目标印刷费用()
        {
            var game = NewGame();
            GiveMana(game, 1, 6, "jc135_m3");
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_k10_host2", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var att = AttachProbe(game, host, cost: 3, uid: "TS_k10_att3");

            var card = IntoHand(game, "JC135_yuzhen", 1);
            ReadyToPlay(game, 1, actionPhase: false);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, att.UID), "3 费附属 → 应能打出");
            Assert.AreEqual(manaBefore - (2 + 3), game.PlayerManager.GetAvailableMana(1),
                "支付 基础 2 + 额外 X=3 ＝ 5");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
        }

        // ==================== ⑦ JC135：可用费用不足以覆盖 基础+X ⇒ 拒绝 ====================

        public void TestK10_JC135费用不足被拒()
        {
            var game = NewGame();
            GiveMana(game, 1, 3, "jc135_poor");   // 3 点：基础 2 + X 3 = 5 > 3
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_k10_host3", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var att = AttachProbe(game, host, cost: 3, uid: "TS_k10_att3b");

            var card = IntoHand(game, "JC135_yuzhen", 1);
            ReadyToPlay(game, 1, actionPhase: false);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, att.UID),
                "合法目标费用均大于可用费用 → 不能打出");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
            Assert.AreEqual(CardZone.AttachedZone, att.CurrentZone, "目标附属仍在场（无副作用）");
        }

        // ==================== ⑧ JC135：需要先选目标 ====================

        public void TestK10_JC135未选目标被拒()
        {
            var game = NewGame();
            GiveMana(game, 1, 6, "jc135_notarget");
            var card = IntoHand(game, "JC135_yuzhen", 1);
            ReadyToPlay(game, 1, actionPhase: false);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID), "未给目标 → X 无从确定 → 拒绝打出");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
        }

        // ==================== ⑩ JC081：展示手牌减费（可选额外费用） ====================

        public void TestK10_JC081结构与过滤器()
        {
            var jc081 = Load("JC081_yaluoer");
            Assert.IsNotNull(jc081.ExtraCost, "JC081 声明了额外费用");
            Assert.AreEqual(ExtraCostKind.RevealHandCards, jc081.ExtraCost.Kind, "额外费用＝展示任意数量手牌");
            Assert.IsFalse(jc081.ExtraCost.Mandatory, "**可以不展示**（不展示＝全额支付，A22）");
            Assert.AreEqual("组织:圣贤|域:神圣", jc081.ExtraCost.RevealFilter,
                "【圣贤】牌＝组织:圣贤；【圣】图标＝神圣领域图标（A35）");

            // 灵体前缀由既有词条通道承担（Engine 级伤害防止消费）
            var spirit = FindEffect(jc081, "JC081_A_Spirit");
            Assert.IsNotNull(spirit, "JC081 应有【灵体】持续效果");
            Assert.AreEqual("灵体", spirit.ContinuousGrants[0].Value, "授予【灵体】关键字");
        }

        public void TestK10_JC081展示合格牌减费()
        {
            var game = NewGame();
            // JC081 无色忠诚需求：给 8 点费用（Cost=7）
            GiveMana(game, 1, 8, "jc081_mana", CardColor.White);   // 忠诚(白 3) + 费用 7
            var jc081 = IntoHand(game, "JC081_yaluoer", 1);

            // 手牌里的"展示候选"：一张【圣贤】组织牌 + 一张神圣领域牌 + 一张无关牌
            var sage = game.CardManager.CreateCardInstance(Probe("TS_k10_sage", CardCategory.Sorcery), 1);
            sage.PlayerTemplate.Organization = "圣贤";
            game.ZoneManager.MoveToHand(sage, 1);
            var holy = game.CardManager.CreateCardInstance(Probe("TS_k10_holy", CardCategory.Sorcery), 1);
            holy.PlayerTemplate.MagicDomains.Add(MagicDomain.Divine);
            game.ZoneManager.MoveToHand(holy, 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_k10_plain", CardCategory.Sorcery), 1);
            game.ZoneManager.MoveToHand(plain, 1);

            ReadyToPlay(game, 1, actionPhase: true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            var reveal = new List<string> { sage.UID, holy.UID };

            Assert.IsTrue(game.TurnManager.PlayCard(1, jc081.UID, null, null, null, -1, null, reveal),
                "展示两张合格牌 → 应能打出");
            Assert.AreEqual(manaBefore - (7 - 2), game.PlayerManager.GetAvailableMana(1),
                "费用 7 − 减免 2 ＝ 5");

            // 不合格牌不减免
            var game2 = NewGame();
            GiveMana(game2, 1, 8, "jc081_mana2", CardColor.White);
            var card2 = IntoHand(game2, "JC081_yaluoer", 1);
            var plain2 = game2.CardManager.CreateCardInstance(Probe("TS_k10_plain2", CardCategory.Sorcery), 1);
            game2.ZoneManager.MoveToHand(plain2, 1);
            ReadyToPlay(game2, 1, actionPhase: true);
            int mana2 = game2.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(game2.TurnManager.PlayCard(1, card2.UID, null, null, null, -1, null,
                new List<string> { plain2.UID }), "展示不合格牌 → 仍可打出");
            Assert.AreEqual(mana2 - 7, game2.PlayerManager.GetAvailableMana(1), "不合格牌不减免（照付 7）");
        }

        public void TestK10_JC081不展示即全额()
        {
            var game = NewGame();
            GiveMana(game, 1, 8, "jc081_mana3", CardColor.White);
            var card = IntoHand(game, "JC081_yaluoer", 1);
            var sage = game.CardManager.CreateCardInstance(Probe("TS_k10_sage2", CardCategory.Sorcery), 1);
            sage.PlayerTemplate.Organization = "圣贤";
            game.ZoneManager.MoveToHand(sage, 1);

            ReadyToPlay(game, 1, actionPhase: true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID), "**不展示**（未提供候选）→ 仍可按全额打出（A22）");
            Assert.AreEqual(manaBefore - 7, game.PlayerManager.GetAvailableMana(1), "全额支付 7");
        }

        public void TestK10_JC081非法展示候选failclosed()
        {
            var game = NewGame();
            GiveMana(game, 1, 8, "jc081_mana4", CardColor.White);
            var card = IntoHand(game, "JC081_yaluoer", 1);
            string area = FirstArea(game);
            var onField = game.CardManager.CreateCardInstance(Probe("TS_k10_fieldcard", CardCategory.Sorcery), 1);
            game.ZoneManager.MoveToArea(onField, area, 1);   // 不在手牌

            ReadyToPlay(game, 1, actionPhase: true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, null, null, -1, null,
                new List<string> { onField.UID }), "展示候选不在手牌 → 拒绝打出（fail-closed）");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
        }

        // ==================== ⑪ JC135 端到端：结算后消灭该附属 ====================

        public void TestK10_JC135端到端消灭附属()
        {
            var game = NewGame();
            GiveMana(game, 1, 4, "jc135_e2e");
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_k10_host4", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var att = AttachProbe(game, host, cost: 1, uid: "TS_k10_att1");

            var card = IntoHand(game, "JC135_yuzhen", 1);
            ReadyToPlay(game, 1, actionPhase: false);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, att.UID), "前置：成功打出");
            Assert.AreEqual(manaBefore - 3, game.PlayerManager.GetAvailableMana(1), "支付 2 + 1");

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Graveyard, att.CurrentZone, "结算后目标附属被消灭");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡结算后进墓地");
            Assert.IsNull(att.AttachedTo, "结附关系已断开");
        }

        // ==================== ⑫ 联机：需要玩家选择的额外费用（服务端把选择回环给该玩家） ====================
        //  · JC049 牺牲对象 → `ChoiceRequest.Kind.SelectTarget`（A33 指定复用该类型；场上高亮点选）；
        //  · JC081 展示手牌 → `ChoiceRequest.Kind.SelectHandCards`（**多选**，可空＝不展示、全额支付；取消＝不打出）；
        //  两类都由**服务端接管**（`TryBeginExtraCostPlayOnline` → `PlayWithExtraCostAsync`），
        //  回包一律**服务端权威复检**（不在候选内 → 整次中止，fail-closed）。

        /// <summary>联机桩：记请求并脚本化回包（按请求种类分别给答案）。</summary>
        private sealed class OnlineExtraCostGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public string TargetAnswer;                 // SelectTarget 回包（null＝回第一个候选）
            public bool CancelTarget;
            public bool CancelHandCards;
            public bool ConfirmHandCards = true;
            public readonly List<string> HandCardAnswers = new List<string>();

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var res = new Script.Net.ChoiceResult { RequestId = req.RequestId };
                switch (req.RequestKind)
                {
                    case Script.Net.ChoiceRequest.Kind.SelectTarget:
                        res.Cancelled = CancelTarget;
                        if (!CancelTarget)
                            res.SelectedUid = TargetAnswer
                                ?? (req.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                        break;
                    case Script.Net.ChoiceRequest.Kind.SelectHandCards:
                        res.Cancelled = CancelHandCards;
                        res.Confirmed = ConfirmHandCards;
                        foreach (var uid in HandCardAnswers) res.SelectedUids.Add(uid);
                        break;
                    default:
                        res.Cancelled = true;
                        break;
                }
                return System.Threading.Tasks.Task.FromResult(res);
            }
        }

        /// <summary>JC049 前置：4 点费用（含红忠诚2）+ 场上两个本方角色，返回 (卡, 角色A, 角色B)。</summary>
        private static (CardInstance card, CardInstance a, CardInstance b) SetupJc049Online(
            GameInitializer game, string tag)
        {
            GiveMana(game, 1, 4, tag, CardColor.Red);
            string area = FirstArea(game);
            var a = game.CardManager.CreateCardInstance(Probe($"TS_{tag}_a", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(a, area, 1);
            var b = game.CardManager.CreateCardInstance(Probe($"TS_{tag}_b", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(b, area, 1);
            var card = IntoHand(game, "JC049_shenyuanxiyu", 1);
            ReadyToPlay(game, 1, actionPhase: true);
            return (card, a, b);
        }

        public void TestK10_联机_JC049回环选牺牲()
        {
            var game = NewGame();
            var (card, a, b) = SetupJc049Online(game, "ol049a");
            var gw = new OnlineExtraCostGateway { TargetAnswer = a.UID };
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, card.UID, null, null).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "联机额外费用流程应成功");
            Assert.AreEqual(1, gw.Requests.Count, "只发一次选择请求（JC049 无手牌多选）");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectTarget, gw.Requests[0].RequestKind,
                "复用 `Kind.SelectTarget`（A33：非新窗口类型）");
            Assert.IsTrue(gw.Requests[0].ForceChoice, "必须支付 → 强制选择（客户端不提供跳过）");
            Assert.AreEqual(2, gw.Requests[0].OptionUids.Count, "候选＝2 个本方正面角色");
            Assert.IsTrue(gw.Requests[0].OptionUids.Contains(a.UID) && gw.Requests[0].OptionUids.Contains(b.UID),
                "候选为场上本方角色");
            Assert.AreEqual(2, gw.Requests[0].OptionCardRefs.Count, "随请求下发候选展示引用");

            Assert.AreEqual(CardZone.Graveyard, a.CurrentZone, "回包选中的角色被牺牲（进墓地）");
            Assert.AreEqual(CardZone.AreaZone, b.CurrentZone, "未选中的角色仍在场上");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "常规费用照付");
            Assert.IsFalse(game.StackManager.IsEmpty, "卡已入堆叠");
        }

        public void TestK10_联机_JC049回包越权被拒()
        {
            var game = NewGame();
            var (card, a, b) = SetupJc049Online(game, "ol049b");
            string area = FirstArea(game);
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ol049_enemy", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);

            var gw = new OnlineExtraCostGateway { TargetAnswer = enemy.UID };   // 敌方角色不在候选内
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, card.UID, null, null).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "回包不在服务端候选内 → 整次打出中止（服务端权威复检）");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡仍在手牌");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣任何费用");
            Assert.AreEqual(CardZone.AreaZone, enemy.CurrentZone, "敌方角色未受影响");
            Assert.AreEqual(CardZone.AreaZone, a.CurrentZone, "本方角色也未受影响");
        }

        public void TestK10_联机_JC049取消则不打出()
        {
            var game = NewGame();
            var (card, a, b) = SetupJc049Online(game, "ol049c");
            var gw = new OnlineExtraCostGateway { CancelTarget = true };
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, card.UID, null, null).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消/超时 → 不打出");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡留手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不扣任何费用（A23 同口径）");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        public void TestK10_联机_JC049无候选不发请求()
        {
            var game = NewGame();
            GiveMana(game, 1, 4, "ol049d", CardColor.Red);
            var card = IntoHand(game, "JC049_shenyuanxiyu", 1);
            ReadyToPlay(game, 1, actionPhase: true);
            var gw = new OnlineExtraCostGateway();
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, card.UID, null, null).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "无合法牺牲对象 → 不能打出（A21）");
            Assert.AreEqual(0, gw.Requests.Count, "**不发**选择请求（问了也没得选）");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡留手牌");
        }

        public void TestK10_联机_JC081多选展示减费()
        {
            var game = NewGame();
            GiveMana(game, 1, 8, "ol081a", CardColor.White);
            var jc081 = IntoHand(game, "JC081_yaluoer", 1);
            var sage = game.CardManager.CreateCardInstance(Probe("TS_ol081_sage", CardCategory.Sorcery), 1);
            sage.PlayerTemplate.Organization = "圣贤";
            game.ZoneManager.MoveToHand(sage, 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_ol081_plain", CardCategory.Sorcery), 1);
            game.ZoneManager.MoveToHand(plain, 1);
            ReadyToPlay(game, 1, actionPhase: true);

            var gw = new OnlineExtraCostGateway();
            gw.HandCardAnswers.Add(sage.UID);
            gw.HandCardAnswers.Add(plain.UID);      // 不合格牌：不减免
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, jc081.UID, null, null).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "联机 JC081 应能打出");
            Assert.AreEqual(1, gw.Requests.Count, "只发一次多选请求");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectHandCards, gw.Requests[0].RequestKind,
                "请求种类＝展示任意数量手牌（多选）");
            Assert.IsFalse(gw.Requests[0].ForceChoice, "允许一张都不展示（A22）");
            var reqUids = gw.Requests[0].OptionUids;
            Assert.IsTrue(reqUids.Contains(sage.UID) && reqUids.Contains(plain.UID),
                "候选含打出者手牌里的其它牌");
            Assert.IsFalse(reqUids.Contains(jc081.UID),
                "候选**不含正在打出的本卡自身**（防自我减费：JC081 自身组织就是【圣贤】）");
            Assert.AreEqual(reqUids.Count, gw.Requests[0].OptionLabels.Count, "候选显示名与候选同序同数");
            int sageIdx = reqUids.IndexOf(sage.UID);
            int plainIdx = reqUids.IndexOf(plain.UID);
            Assert.IsTrue(gw.Requests[0].OptionLabels[sageIdx].Contains("合格 -1"),
                "合格标记由**服务端**给出（客户端不重算）");
            Assert.IsTrue(gw.Requests[0].OptionLabels[plainIdx].Contains("不合格"), "不合格标记同样由服务端给出");
            Assert.AreEqual(reqUids.Count, gw.Requests[0].NumberMax, "最多可选张数＝候选数");

            Assert.AreEqual(manaBefore - (7 - 1), game.PlayerManager.GetAvailableMana(1),
                "费用 7 − 合格 1 张 ＝ 6（不合格牌不减免）");
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
        }

        public void TestK10_联机_JC081不勾选即全额()
        {
            var game = NewGame();
            GiveMana(game, 1, 8, "ol081b", CardColor.White);
            var jc081 = IntoHand(game, "JC081_yaluoer", 1);
            var sage = game.CardManager.CreateCardInstance(Probe("TS_ol081_sage2", CardCategory.Sorcery), 1);
            sage.PlayerTemplate.Organization = "圣贤";
            game.ZoneManager.MoveToHand(sage, 1);
            ReadyToPlay(game, 1, actionPhase: true);

            var gw = new OnlineExtraCostGateway();   // 确认但不勾选任何牌
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, jc081.UID, null, null).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "不展示 → 仍可打出（A22）");
            Assert.AreEqual(manaBefore - 7, game.PlayerManager.GetAvailableMana(1), "全额支付 7（0 减免）");
        }

        public void TestK10_联机_JC081取消则不打出()
        {
            var game = NewGame();
            GiveMana(game, 1, 8, "ol081c", CardColor.White);
            var jc081 = IntoHand(game, "JC081_yaluoer", 1);
            var sage = game.CardManager.CreateCardInstance(Probe("TS_ol081_sage3", CardCategory.Sorcery), 1);
            sage.PlayerTemplate.Organization = "圣贤";
            game.ZoneManager.MoveToHand(sage, 1);
            ReadyToPlay(game, 1, actionPhase: true);

            var gw = new OnlineExtraCostGateway { CancelHandCards = true };
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.PlayWithExtraCostAsync(1, jc081.UID, null, null).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消＝不打出（A23）");
            Assert.AreEqual(CardZone.Hand, jc081.CurrentZone, "卡留手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不扣任何费用");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        public void TestK10_联机_无网关不接管()
        {
            var game = NewGame();
            var (card, a, b) = SetupJc049Online(game, "ol049e");

            Assert.IsFalse(game.PlayerActionService.TryBeginExtraCostPlayOnline(1, card.UID, null, null),
                "**未注入输入网关**（单机/纯本地）→ 不接管，走既有同步 `PlayCard`");

            var gw = new OnlineExtraCostGateway { CancelTarget = true };
            game.TurnManager.InputGateway = gw;
            Assert.IsTrue(game.PlayerActionService.TryBeginExtraCostPlayOnline(1, card.UID, null, null),
                "已注入网关 → 服务端接管（选择回环给该玩家）");

            var jc135 = IntoHand(game, "JC135_yuzhen", 1);
            Assert.IsFalse(game.PlayerActionService.TryBeginExtraCostPlayOnline(1, jc135.UID, null, null),
                "JC135 的 X 由目标费用决定（不需要玩家前置选择）→ 不接管");
        }

        public void TestK10_联机_SelectHandCards超时档()
        {
            Assert.AreEqual(60, Script.Net.NetworkInputGateway.TimeoutSecondsFor(
                Script.Net.ChoiceRequest.Kind.SelectHandCards), "展示手牌多选＝60s（与选卡同档）");
        }
    }
}
