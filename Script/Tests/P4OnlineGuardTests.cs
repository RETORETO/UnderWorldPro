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
    ///  【P2·联机回包权威复检 + 选择窗可读性】回归：
    ///  ① **检索**（`SelectDeckCard`）：回包必须命中"服务端本次下发的候选"（在检索者牌库 ∧ 通过筛选）；
    ///    回包越权（如对手手牌）→ **越权牌取不走**；【A53】检索为强制，故改为**兜底取第一张合法候选**
    ///    （原"整次检索不生效、不洗牌"口径随 A53 作废——否则异常客户端可用一次越权回包规避强制检索）。
    ///    原实现只判"实例存在" ⇒ 客户端可回包任意 UID 把它当检索结果取走。
    ///  ② **弃牌**（`SelectHandCard`）：同理，回包必须是"该弃牌者手牌中、本批尚未选中的牌"。
    ///  ③ **选择窗可读性**：牌库/对手手牌在客户端是**隐藏占位**（无模板/无卡面），
    ///    故候选显示名由**服务端**随请求下发（`ChoiceRequest.OptionLabels`），客户端不重算规则。
    /// </summary>
    public class P4OnlineGuardTests : TestRunner
    {
        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 4242 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Sorcery,
            string org = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
            };
            if (org != null) tmpl.Organization = org;
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>脚本化网关：记录请求，并按 `AnswerUid` / `AnswerIndex` 回包。</summary>
        private sealed class ScriptedGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public string AnswerUid;          // 指定回包 UID（null＝首个候选）
            public bool Cancel = false;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = Cancel };
                if (!Cancel)
                {
                    r.SelectedUid = AnswerUid
                        ?? (req?.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        /// <summary>造一个"检索：子类别:角色 → 手牌"的探针效果（供联机网关路径驱动）。</summary>
        private static EffectData SearchEffect()
            => new EffectData
            {
                EffectId = "TS_probe_search",
                Timing = EffectTiming.OnSorceryCast,
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        Args = new Array<string> { "类别:角色", "手牌", "", "牌库" },
                    },
                },
            };

        // ==================== ① 检索：合规回包 → 取走并洗牌 ====================

        public void TestOnlineGuard_检索合规回包生效()
        {
            var game = NewGame();
            var gw = new ScriptedGateway();
            game.EffectEngine.InputGateway = gw;   // 生产服务端由 MatchHost 注入（见 MatchIsolationTests）

            // 牌库：放两张"角色"探针（匹配）与一张"事务"探针（不匹配）
            var match = game.CardManager.CreateCardInstance(Probe("TS_og_match", CardCategory.Character), 1);
            game.ZoneManager.MoveToDeckBottom(match, 1);
            var other = game.CardManager.CreateCardInstance(Probe("TS_og_other", CardCategory.Character), 1);
            game.ZoneManager.MoveToDeckBottom(other, 1);
            var noMatch = game.CardManager.CreateCardInstance(Probe("TS_og_nomatch"), 1);
            game.ZoneManager.MoveToDeckBottom(noMatch, 1);

            var src = game.CardManager.CreateCardInstance(Probe("TS_og_src"), 1);
            game.ZoneManager.MoveToHand(src, 1);

            var e = SearchEffect();
            gw.AnswerUid = match.UID;
            game.EffectEngine.ResolveEffectAsync(e, src, null, "").GetAwaiter().GetResult();

            Assert.AreEqual(1, gw.Requests.Count, "发了一次检索请求");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectDeckCard, gw.Requests[0].RequestKind, "请求种类＝选牌库牌");
            Assert.IsTrue(gw.Requests[0].OptionUids.Contains(match.UID),
                "候选含牌库里通过筛选的角色牌（测试牌组自带角色牌 → 候选不止我放的这些）");
            Assert.IsFalse(gw.Requests[0].OptionUids.Contains(noMatch.UID), "未通过筛选的牌不在候选内");
            Assert.AreEqual(gw.Requests[0].OptionUids.Count, gw.Requests[0].OptionLabels.Count,
                "**候选显示名与候选同序同数**（客户端占位卡看不到卡名，靠服务端下发）");
            Assert.AreEqual(gw.Requests[0].OptionUids.Count, gw.Requests[0].OptionCardRefs.Count,
                "候选卡面引用同样随请求下发");
            Assert.AreEqual(CardZone.Hand, match.CurrentZone, "合规回包 → 该牌被取到手牌");
        }

        // ==================== ② 检索：越权回包 → 越权牌被拒；按强制检索兜底取第一张合法候选 ====================

        /// <summary>
        /// 【A53·2026-09 用户拍板】检索改为**强制**后，越权回包的处理口径：
        ///  · **越权的那张牌仍然取不走**（安全属性不变：候选永远只来自检索者牌库 ∧ 通过筛选）；
        ///  · 但**不再**"整次检索不生效"（那会让异常客户端用一次越权回包规避强制检索）→
        ///    按确定性兜底取**第一张合法候选**，随后照常洗牌。
        /// </summary>
        public void TestOnlineGuard_检索越权回包被拒并兜底取第一张()
        {
            var game = NewGame();
            var gw = new ScriptedGateway();
            game.EffectEngine.InputGateway = gw;

            var match = game.CardManager.CreateCardInstance(Probe("TS_og_m2", CardCategory.Character), 1);
            game.ZoneManager.MoveToDeckBottom(match, 1);

            // 越权目标：**玩家2 手牌**里的一张牌（不在检索者牌库、也不在候选内）
            var enemyHand = game.CardManager.CreateCardInstance(Probe("TS_og_enemy"), 2);
            game.ZoneManager.MoveToHand(enemyHand, 2);

            var src = game.CardManager.CreateCardInstance(Probe("TS_og_src2"), 1);
            game.ZoneManager.MoveToHand(src, 1);
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;

            var e = SearchEffect();
            gw.AnswerUid = enemyHand.UID;   // 越权回包
            game.EffectEngine.ResolveEffectAsync(e, src, null, "").GetAwaiter().GetResult();

            Assert.AreEqual(1, gw.Requests.Count, "发了一次检索请求");
            string firstCandidateUid = gw.Requests[0].OptionUids[0];
            Assert.IsFalse(gw.Requests[0].OptionUids.Contains(enemyHand.UID), "越权牌从未进入候选");

            Assert.AreEqual(CardZone.Hand, enemyHand.CurrentZone, "越权回包的牌**没有被取走**（安全属性不变）");
            Assert.AreEqual(2, enemyHand.ControllerId, "归属未被改动");
            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count,
                "兜底取走一张**合法候选**（强制检索不可被越权回包规避）");

            bool fallbackInHand = false;
            foreach (var c in game.TurnManager.GetHandOf(1))
                if (c != null && c.UID == firstCandidateUid) fallbackInHand = true;
            Assert.IsTrue(fallbackInHand, "兜底取走的正是**第一张合法候选**（已进检索者手牌）");
        }

        // ==================== ③ 弃牌：越权回包 → 拒绝 ====================

        public void TestOnlineGuard_弃牌越权回包被拒()
        {
            var game = NewGame();
            var gw = new ScriptedGateway();
            game.EffectEngine.InputGateway = gw;

            // 弃牌者＝玩家1：手牌两张
            var h1 = game.CardManager.CreateCardInstance(Probe("TS_og_h1", CardCategory.Character), 1);
            game.ZoneManager.MoveToHand(h1, 1);
            var h2 = game.CardManager.CreateCardInstance(Probe("TS_og_h2", CardCategory.Character), 1);
            game.ZoneManager.MoveToHand(h2, 1);

            // 越权目标：玩家1 **场上**的卡（不在手牌候选内）
            var onField = game.CardManager.CreateCardInstance(Probe("TS_og_field", CardCategory.Character), 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];
            game.ZoneManager.MoveToArea(onField, area, 1);

            var src = game.CardManager.CreateCardInstance(Probe("TS_og_src3"), 1);
            game.ZoneManager.MoveToHand(src, 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;   // 在布置完成后取基准

            var e = new EffectData
            {
                EffectId = "TS_probe_discard",
                Timing = EffectTiming.OnSorceryCast,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "自己", "自选" } },
                },
            };
            gw.AnswerUid = onField.UID;     // 越权回包（场上卡，不在手牌候选中）
            game.EffectEngine.ResolveEffectAsync(e, src, null, "").GetAwaiter().GetResult();

            Assert.AreEqual(1, gw.Requests.Count, "发了一次弃牌请求");
            Assert.IsTrue(gw.Requests[0].OptionLabels.Count > 0, "候选显示名随请求下发");
            Assert.AreEqual(CardZone.AreaZone, onField.CurrentZone, "越权回包的卡**没有被弃掉**（仍在场上）");
            // 本批一次都没落地（回包非法 → 中止本批）
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "手牌未被弃掉");
        }
    }
}
