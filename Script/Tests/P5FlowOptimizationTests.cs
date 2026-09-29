using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.IO;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【流程优化批次】按用户 2026-09 口径实现并锁定：
    ///  1. 步骤中文名（12 步，含"机动轮"）与"免让过步骤"判定（重置 / 恢复 / 移交先手）；
    ///  2. 免让过推进：重置 / 恢复步骤结算完自身效果后**栈空即自动推进**，不再等双方让过；
    ///  3. 优先权交换（修复）：打出卡 / 暗藏者现身**入堆叠后**优先权归"本步骤天生优先玩家"，
    ///     不因"谁打出"而被错误翻转（后手在 ActionSecond 内出牌后仍保有优先权）；
    ///  4. 开局发手牌按秘社 `StartingHandSize`（无秘社回退 6）；
    ///  5. 调度步骤结算：所选手牌置**牌库底** → 从牌库**顶**抽等量 → 洗牌（手牌/牌库张数守恒）；
    ///  6. 卡组构筑校验：总张数 ≥ 50（秘社限制的细粒度断言见 P5CouncilRestrictionTests）。
    /// </summary>
    public class P5FlowOptimizationTests : TestRunner
    {
        // ==================== ① 步骤中文名 + 免让过判定 ====================

        public void TestPhaseDisplayName_十二步全中文()
        {
            var expect = new Dictionary<global::TurnManager.TurnPhase, string>
            {
                [global::TurnManager.TurnPhase.StartReset] = "重置步骤",
                [global::TurnManager.TurnPhase.StartPrepare] = "准备步骤",
                [global::TurnManager.TurnPhase.StartDraw] = "抓牌步骤",
                [global::TurnManager.TurnPhase.ActionFirst] = "先手行动步骤",
                [global::TurnManager.TurnPhase.ActionSecond] = "后手行动步骤",
                [global::TurnManager.TurnPhase.PreCombat] = "机动轮",
                [global::TurnManager.TurnPhase.CombatInvestigation] = "调查对抗步骤",
                [global::TurnManager.TurnPhase.CombatStrength] = "战斗对抗步骤",
                [global::TurnManager.TurnPhase.CombatPower] = "势力对抗步骤",
                [global::TurnManager.TurnPhase.EndCleanup] = "结束步骤",
                [global::TurnManager.TurnPhase.EndRecover] = "恢复步骤",
                [global::TurnManager.TurnPhase.EndPassTurn] = "移交先手",
            };
            foreach (var kv in expect)
            {
                Assert.AreEqual(kv.Value, global::TurnManager.GetPhaseDisplayName(kv.Key),
                    $"步骤 {kv.Key} 的中文名应为「{kv.Value}」");
            }

            // 对抗步骤带地区序号前缀
            Assert.AreEqual("地区2 调查对抗步骤",
                global::TurnManager.GetPhaseDisplayName(global::TurnManager.TurnPhase.CombatInvestigation, 2),
                "对抗步骤应带『地区N』前缀");
            Assert.AreEqual("调查对抗步骤",
                global::TurnManager.GetPhaseDisplayName(global::TurnManager.TurnPhase.CombatInvestigation, 0),
                "无地区序号时不带前缀");
            Assert.AreEqual("重置步骤",
                global::TurnManager.GetPhaseDisplayName(global::TurnManager.TurnPhase.StartReset, 3),
                "非对抗步骤不带地区序号");
        }

        public void TestIsAutoAdvanceStep_免让过步骤集合()
        {
            Assert.IsTrue(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.StartReset), "重置步骤免让过");
            Assert.IsTrue(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.EndRecover), "恢复步骤免让过");
            Assert.IsTrue(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.EndPassTurn), "移交先手免让过");

            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.StartPrepare), "准备步骤仍需让过");
            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.StartDraw), "抓牌步骤仍需让过");
            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.ActionFirst), "先手行动仍需让过");
            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.ActionSecond), "后手行动仍需让过");
            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.PreCombat), "机动轮仍需让过");
            Assert.IsFalse(global::TurnManager.IsAutoAdvanceStep(global::TurnManager.TurnPhase.EndCleanup), "结束步骤仍需让过");
        }

        // ==================== ② 免让过推进 ====================

        /// <summary>重置步骤免让过：StartGame 后应立即从"重置"前进到"准备"（不等人让过）。</summary>
        public void TestAutoAdvance_重置步骤立即推进到准备步骤()
        {
            var game = NewGame();
            game.TurnManager.StartGame();

            Assert.AreEqual(global::TurnManager.TurnPhase.StartPrepare, game.TurnManager.CurrentPhase,
                "重置步骤免让过：StartGame 后应立即停在『准备步骤』（而非仍停在重置步骤）");
        }

        /// <summary>
        /// 恢复步骤免让过：进入恢复步骤后应**不再原地等待让过**。
        /// 由于「恢复 → 移交先手 → 重置」三者全部免让过，连锁推进后最终停在下一回合的『准备步骤』。
        /// </summary>
        public void TestAutoAdvance_恢复步骤立即推进()
        {
            var game = NewGame();
            int turnBefore = game.TurnManager.TurnCount;

            game.TurnManager.EnterPhase(global::TurnManager.TurnPhase.EndRecover);

            Assert.IsFalse(game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.EndRecover,
                "恢复步骤免让过：不应仍停在『恢复步骤』等待让过");
            Assert.AreEqual(turnBefore + 1, game.TurnManager.TurnCount,
                "恢复 → 移交先手（免让过）应已换人开新回合");
            Assert.AreEqual(global::TurnManager.TurnPhase.StartPrepare, game.TurnManager.CurrentPhase,
                "连锁免让过（恢复→移交先手→重置）后应停在下一回合『准备步骤』");
        }

        /// <summary>移交先手免让过：直接换人开新回合（FirstPlayerId 翻转、TurnCount +1）。</summary>
        public void TestAutoAdvance_移交先手直接换人开新回合()
        {
            var game = NewGame();
            int turnBefore = game.TurnManager.TurnCount;
            int firstBefore = game.TurnManager.FirstPlayerId;

            game.TurnManager.EnterPhase(global::TurnManager.TurnPhase.EndPassTurn);

            Assert.AreEqual(turnBefore + 1, game.TurnManager.TurnCount, "移交先手应开新回合（TurnCount +1）");
            Assert.AreEqual(firstBefore == 1 ? 2 : 1, game.TurnManager.FirstPlayerId, "先手玩家应交换");
            Assert.AreEqual(global::TurnManager.TurnPhase.StartPrepare, game.TurnManager.CurrentPhase,
                "新回合『重置步骤』免让过 → 应已推进到『准备步骤』");
        }

        // ==================== ③ 优先权交换 ====================

        /// <summary>ActionSecond 期间后手（天生优先）出牌 → 优先权仍在后手手中（不被翻转给先手）。</summary>
        public void TestPriority_后手行动中后手出牌优先权不转移()
        {
            var game = NewGame();
            ProvideMana(game, 2);

            game.TurnManager.FirstPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;
            game.TurnManager.PriorityPlayerId = game.TurnManager.GetPriorityPlayer();
            Assert.AreEqual(2, game.TurnManager.GetPriorityPlayer(),
                "ActionSecond 的天生优先玩家＝后手（玩家2）");

            var card = MakePlayableCard(game, 2, "TS_p5_p2");
            bool played = game.TurnManager.PlayCard(2, card.UID);
            Assert.IsTrue(played, "后手应能成功打出响应型探针卡");

            Assert.AreEqual(2, game.TurnManager.PriorityPlayerId,
                "后手（天生优先玩家）出牌后优先权应仍在他手中，不得被拉回先手");
        }

        /// <summary>ActionSecond 期间先手（非天生优先）出牌 → 优先权归还后手（天生优先玩家）。</summary>
        public void TestPriority_后手行动中先手出牌优先权归后手()
        {
            var game = NewGame();
            ProvideMana(game, 1);

            game.TurnManager.FirstPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;
            game.TurnManager.PriorityPlayerId = game.TurnManager.GetPriorityPlayer();

            var card = MakePlayableCard(game, 1, "TS_p5_p1");
            bool played = game.TurnManager.PlayCard(1, card.UID);
            Assert.IsTrue(played, "先手应能成功打出响应型探针卡");

            Assert.AreEqual(2, game.TurnManager.PriorityPlayerId,
                "先手（非天生优先）出牌后应把优先权交给天生优先玩家（后手，玩家2）");
        }

        /// <summary>ActionFirst 期间先手（天生优先）出牌 → 优先权仍在先手手中。</summary>
        public void TestPriority_先手行动中先手出牌优先权保持()
        {
            var game = NewGame();
            ProvideMana(game, 1);

            game.TurnManager.FirstPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            game.TurnManager.PriorityPlayerId = game.TurnManager.GetPriorityPlayer();

            var card = MakePlayableCard(game, 1, "TS_p5_p1b");
            bool played = game.TurnManager.PlayCard(1, card.UID);
            Assert.IsTrue(played, "先手应能成功打出响应型探针卡");

            Assert.AreEqual(1, game.TurnManager.PriorityPlayerId,
                "先手（天生优先玩家）出牌后优先权应仍在他手中");
        }

        // ==================== ④ 起手手牌按秘社 ====================

        public void TestOpeningHand_按秘社起手数发牌()
        {
            var game = new GameInitializer { DebugSeed = 5 };
            game.InitializeAll();

            // 秘社起手数 8（≠ 默认 6），经 P1DeckOverride 注入（与联机房间同路径）
            game.P1DeckOverride = BuildDeckWithCouncil(handSize: 8);
            game.P2DeckOverride = BuildDeckWithCouncil(handSize: 8);

            game.StartMatch();

            Assert.AreEqual(8, game.CardManager.GetHandCardsByOwner(1).Count,
                "玩家1 起手手牌数应＝秘社 StartingHandSize（8），而非写死的 6");
            Assert.AreEqual(8, game.CardManager.GetHandCardsByOwner(2).Count,
                "玩家2 起手手牌数应＝秘社 StartingHandSize（8）");
        }

        // ==================== ⑤ 调度结算（置底 → 顶抽等量 → 洗牌） ====================

        /// <summary>脚本化网关：收到 SelectMulligan 时按前 N 张回选。</summary>
        private sealed class MulliganGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public int Pick1, Pick2;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult
                {
                    RequestId = req?.RequestId, Cancelled = false, Confirmed = true,
                };
                if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectMulligan)
                {
                    int n = playerId == 1 ? Pick1 : Pick2;
                    for (int i = 0; i < n && i < req.OptionUids.Count; i++)
                        r.SelectedUids.Add(req.OptionUids[i]);
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        public void TestMulligan_置底顶抽等量并洗牌()
        {
            var game = NewGame();
            game.PlayerActionService.DrawCards(1, 6);
            game.PlayerActionService.DrawCards(2, 6);

            int hand1Before = game.CardManager.GetHandCardsByOwner(1).Count;
            int deck1Before = game.DeckManager.GetPlayer1DeckCount();
            Assert.IsTrue(hand1Before > 0, "前置：玩家1 已有起手手牌");

            var gw = new MulliganGateway { Pick1 = 3, Pick2 = 0 };
            game.TurnManager.InputGateway = gw;

            game.TurnManager.BeginMulliganAsync().GetAwaiter().GetResult();

            Assert.AreEqual(2, gw.Requests.Count, "应向双方各下发一次调度请求");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectMulligan, gw.Requests[0].RequestKind,
                "请求种类＝SelectMulligan");
            Assert.IsTrue(gw.Requests[0].OptionUids.Count > 0, "候选＝该玩家起手手牌");

            Assert.AreEqual(hand1Before, game.CardManager.GetHandCardsByOwner(1).Count,
                "玩家1 调度后手牌张数守恒（置底 N 张 → 顶抽 N 张）");
            Assert.AreEqual(deck1Before, game.DeckManager.GetPlayer1DeckCount(),
                "玩家1 调度后牌库张数守恒（移出 N 张 → 抽走 N 张）");
        }

        public void TestMulligan_空选择等价不调度()
        {
            var game = NewGame();
            game.PlayerActionService.DrawCards(1, 6);
            game.PlayerActionService.DrawCards(2, 6);

            int hand1Before = game.CardManager.GetHandCardsByOwner(1).Count;
            int deck1Before = game.DeckManager.GetPlayer1DeckCount();

            game.TurnManager.InputGateway = new MulliganGateway { Pick1 = 0, Pick2 = 0 };
            game.TurnManager.BeginMulliganAsync().GetAwaiter().GetResult();

            Assert.AreEqual(hand1Before, game.CardManager.GetHandCardsByOwner(1).Count, "空选择：手牌不变");
            Assert.AreEqual(deck1Before, game.DeckManager.GetPlayer1DeckCount(), "空选择：牌库不变");
        }

        /// <summary>调度结束后应已进入第一回合（重置免让过 → 停在准备步骤）。</summary>
        public void TestMulligan_结束后进入第一回合()
        {
            var game = NewGame();
            game.PlayerActionService.DrawCards(1, 6);
            game.PlayerActionService.DrawCards(2, 6);

            game.TurnManager.InputGateway = new MulliganGateway { Pick1 = 1, Pick2 = 1 };
            game.TurnManager.BeginMulliganAsync().GetAwaiter().GetResult();

            Assert.AreEqual(1, game.TurnManager.TurnCount, "调度完成后应已开始第一回合（TurnCount=1）");
            Assert.AreEqual(global::TurnManager.TurnPhase.StartPrepare, game.TurnManager.CurrentPhase,
                "第一回合『重置步骤』免让过 → 应停在『准备步骤』");
            Assert.IsFalse(game.TurnManager.IsMulliganPhase, "调度阶段标记应已清除");
        }

        // ==================== ⑥ 卡组构筑校验（张数） ====================

        public void TestDeckValidate_张数不足被拒()
        {
            var validator = new Script.Net.Godot.GameDeckValidator();
            var deck = BuildPlainDeck(copies: 49);

            bool ok = validator.Validate(Script.IO.DeckListLoader.ToJsonText(deck), null,
                out _, out _, out int count, out string error);

            Assert.IsFalse(ok, "49 张卡组应被判违规");
            Assert.IsTrue(error != null && error.Contains("50"),
                $"错误文案应说明需 ≥ 50 张，实际：{error}");
        }

        public void TestDeckValidate_恰50张通过()
        {
            var validator = new Script.Net.Godot.GameDeckValidator();
            var deck = BuildPlainDeck(copies: 50);

            bool ok = validator.Validate(Script.IO.DeckListLoader.ToJsonText(deck), null,
                out _, out _, out int count, out string error);

            Assert.IsTrue(ok, $"恰 50 张应通过张数校验（≥50），实际错误：{error}");
            Assert.AreEqual(50, count, "张数应正确统计为 50");
        }

        // ==================== 辅助 ====================

        /// <summary>
        /// 纯玩家卡卡组（用项目内真实存在的模板路径，供校验器做存在性检查）。
        /// **必须显式挂一个"无构筑限制"的秘社**：`DeckListLoader` 在秘社为空时会兜底补默认秘社
        /// （`MSJC001_weimushouwang`，带"需 ≥25 张黄色牌"限制），那样张数用例会被构筑限制干扰。
        /// </summary>
        private static DeckList BuildPlainDeck(int copies)
        {
            var deck = new DeckList { DeckName = "测试卡组" };
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = ProbeTemplatePath, Copies = copies });
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = ProbeNoRestrictionCouncilPath, Copies = 1 });
            return deck;
        }

        private static DeckList BuildDeckWithCouncil(int handSize)
        {
            var deck = new DeckList { DeckName = "秘社卡组" };
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = ProbeTemplatePath, Copies = 30 });
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = WriteProbeCouncil(handSize), Copies = 1 });
            return deck;
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 2026 };
            game.InitializeAll();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        /// <summary>
        /// 造一张 **C 档（响应型）** 0 费事务卡并入某玩家手牌：`PlayTiming=Response` → 取消打出阶段门禁，
        /// 只要"有优先权 + 从手牌打出 + 忠诚/费用足够"即可打出入堆叠。
        /// 这样测试只聚焦"优先权交换"这一个自变量，不被 A/B 档的堆叠空/行动步骤门禁干扰。
        /// </summary>
        private static CardInstance MakePlayableCard(GameInitializer game, int playerId, string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针事务_" + uid, Cost = 0, Defense = -1,
                CategoryFlag = CardCategory.Sorcery,
            };
            tmpl.EffectDataList.Add(new Script.Data.Effects.EffectData
            {
                EffectId = uid + "_e",
                Timing = Script.Data.Effects.EffectTiming.OnSorceryCast,
                PlayTiming = Script.Data.Effects.PlayTiming.Response,   // C 档：取消阶段门禁
                Cost = 0,
                Forced = false,
                Duration = "瞬时",
            });
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToHand(inst, playerId);
            return inst;
        }

        private static void ProvideMana(GameInitializer game, int playerId)
        {
            var tmpl = new PlayerCardData
            {
                UID = $"TS_p5_mana_{playerId}", CardName = "探针资产", Cost = 0, Defense = -1,
                CategoryFlag = CardCategory.Attachment,
            };
            var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToAsset(asset, playerId);
        }

        // ---- 临时真实资源（测试用，写到 user:// 便于 ResourceLoader 加载做"存在性检查"）----

        private static string _probeTemplatePath;
        private static string ProbeTemplatePath => _probeTemplatePath ??= WriteProbeTemplate();

        private static string _probeNoRestrictionCouncilPath;
        /// <summary>无任何构筑限制的探针秘社路径（供"只看张数"的用例隔离秘社限制干扰）。</summary>
        private static string ProbeNoRestrictionCouncilPath
            => _probeNoRestrictionCouncilPath ??= WriteProbeNoRestrictionCouncil();

        private static string WriteProbeNoRestrictionCouncil()
        {
            var council = new CouncilCardData
            {
                UID = "TS_p5_council_norestriction", CardName = "无限制探针秘社", StartingHandSize = 6,
            };
            string path = "user://TS_p5_council_norestriction.tres";
            ResourceSaver.Save(council, path);
            return path;
        }

        private static string WriteProbeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "TS_p5_probe_tmpl", CardName = "探针卡", Cost = 1, Defense = 1,
                CategoryFlag = CardCategory.Sorcery,
            };
            string path = "user://TS_p5_probe_tmpl.tres";
            ResourceSaver.Save(tmpl, path);
            return path;
        }

        private static string WriteProbeCouncil(int handSize)
        {
            var council = new CouncilCardData
            {
                UID = $"TS_p5_council_{handSize}", CardName = "测试秘社", StartingHandSize = handSize,
            };
            string path = $"user://TS_p5_council_{handSize}.tres";
            ResourceSaver.Save(council, path);
            return path;
        }
    }
}
