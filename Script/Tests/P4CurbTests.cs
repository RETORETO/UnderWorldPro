using System.Collections.Generic;
using System.Threading.Tasks;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【P4·遏制X】回归（用户 2026-09 规则文本）：
    ///  · 底层减法唯一出口 `AreaManager.RemovePowerMark`（返回实际移除数、按现有量钳制、不影响对方）；
    ///  · **X ≥ 被遏制者标记总数 → 自动全清（不弹窗）**；
    ///  · **X &lt; 总数 → 被遏制者按地区分配**，**每地区上限＝该地区该玩家的标记数**；
    ///  · **被遏制者默认＝施术者的对手**（`自己` 为可选方位）；
    ///  · 三条决策路径：单机 `PromptCurbMarks` 逐次询问 / 联机 `ChoiceRequest.Kind.CurbMarks`（服务端权威复检）/
    ///    无 UI·网关时确定性兜底（按地区序号贪心）。
    /// </summary>
    public class P4CurbTests : TestRunner
    {
        /// <summary>记录请求并按脚本回答的网关（CurbMarks = 按脚本回分配；其余按默认）。</summary>
        private sealed class CurbGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public List<string> AnswerUids = new List<string>();
            public List<int> AnswerAmounts = new List<int>();
            public bool CancelCurb;

            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    var r0 = new ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r0.SelectedIndices.Add(i);
                    return Task.FromResult(r0);
                }
                if (req.RequestKind == ChoiceRequest.Kind.CurbMarks)
                {
                    if (CancelCurb) return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = true });
                    var r = new ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    r.SelectedUids.AddRange(AnswerUids);
                    r.SelectedAmounts.AddRange(AnswerAmounts);
                    return Task.FromResult(r);
                }
                return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = false });
            }

            public bool HasCurbRequest => Requests.Exists(r => r.RequestKind == ChoiceRequest.Kind.CurbMarks);
            public ChoiceRequest CurbRequest => Requests.Find(r => r.RequestKind == ChoiceRequest.Kind.CurbMarks);
        }

        /// <summary>单机 UI 桩：记录遏制询问，并按脚本选择地区（默认选列表最后一个）。</summary>
        private sealed class CurbPickerStub : IGameUIHooks
        {
            public readonly List<(int remaining, int optionCount, List<CurbMarkOption> options)> Prompts
                = new List<(int, int, List<CurbMarkOption>)>();
            public System.Func<List<CurbMarkOption>, string> Pick;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked) { }

            public void PromptCurbMarks(int curbedPlayerId, int remaining, List<CurbMarkOption> options,
                System.Action<string> onPickedAreaUid)
            {
                Prompts.Add((remaining, options.Count, new List<CurbMarkOption>(options)));
                if (Pick != null) { onPickedAreaUid?.Invoke(Pick(options)); return; }
                onPickedAreaUid?.Invoke(options[options.Count - 1].AreaUid);
            }

            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed)
                => onConfirmed?.Invoke(cards, new List<CardInstance>());

            /// <summary>【K15】本套件不涉及多目标：视为玩家取消。</summary>
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed)
                => onConfirmed?.Invoke(null);

            /// <summary>【K16】本套件不涉及模式分支：视为取消。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
        }

        // ==================== 装配辅助 ====================

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 53 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance Source(GameInitializer game, int controllerId = 1)
            => game.CardManager.CreateCardInstance(Probe($"T_curb_src_{controllerId}"), controllerId);

        /// <summary>遏制效果（事务口径：无目标、条件恒真）。</summary>
        private static EffectData CurbEffect(string id, string x, string who = null)
        {
            var args = new Array<string> { x };
            if (who != null) args.Add(who);
            return new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Curb, Args = args } },
            };
        }

        // ==================== ① RemovePowerMark：底层减法 ====================

        public void TestP4_遏制_RemovePowerMark返回实际移除数并钳制()
        {
            var game = NewGame(2);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 3);
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(areas[0], 2), "前置：玩家2 在地区1 有 3 个标记");

            Assert.AreEqual(1, game.AreaManager.RemovePowerMark(areas[0], 2, 1), "移除 1 个 → 返回实际移除 1");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[0], 2), "剩余 2 个");

            Assert.AreEqual(2, game.AreaManager.RemovePowerMark(areas[0], 2, 9), "请求超量 → 按现有量钳制（返回 2）");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "已全清");
            Assert.AreEqual(0, game.AreaManager.RemovePowerMark(areas[0], 2, 1), "无标记可移除 → 返回 0（不会变负）");

            Assert.AreEqual(0, game.AreaManager.RemovePowerMark("no_such_area", 2, 1), "未知地区 → 0");
            Assert.AreEqual(0, game.AreaManager.RemovePowerMark(areas[0], 2, 0), "数量 0 → 0");
            Assert.AreEqual(0, game.AreaManager.RemovePowerMark(areas[0], 0, 1), "非法玩家 → 0");
        }

        /// <summary>遏制是**纯移除**：只减被遏制者自己的标记，不影响对方（与 `AddPowerMark` 的净差抵扣语义不同）。</summary>
        public void TestP4_遏制_只移除被遏制者的标记()
        {
            var game = NewGame(2);
            var areas = Areas(game);

            // 两个地区分开放置，避免 AddPowerMark 的"新增量先抵扣对方"逻辑互相抵消
            game.AreaManager.AddPowerMark(areas[0], 1, 2);   // 玩家1：地区1
            game.AreaManager.AddPowerMark(areas[1], 2, 3);   // 玩家2：地区2

            game.AreaManager.RemovePowerMark(areas[1], 2, 2);

            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[0], 1), "玩家1 的标记不受影响");
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[1], 2), "玩家2 被移除了 2 个");
        }

        // ==================== ② X ≥ 总数 → 自动全清（不弹窗） ====================

        public void TestP4_遏制_X不小于总数时自动全清不弹窗()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 1);
            Assert.AreEqual(3, CurbRule.TotalMarks(game.AreaManager, 2), "前置：玩家2 共 3 个标记");

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_all", "3"), Source(game), null);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "X=总数 → 全部清除（地区1）");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[1], 2), "X=总数 → 全部清除（地区2）");
            Assert.IsFalse(gateway.HasCurbRequest, "X ≥ 总数 → 自动全清，**不弹窗/不下发决策请求**");
        }

        public void TestP4_遏制_X大于总数也自动全清()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);
            game.AreaManager.AddPowerMark(areas[0], 2, 2);

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_over", "7"), Source(game), null);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "X 超过总数 → 仍全清（不会溢出）");
            Assert.IsFalse(gateway.HasCurbRequest, "X ≥ 总数 → 不询问");
        }

        // ==================== ③ X < 总数 → 被遏制者按地区分配 ====================

        public void TestP4_遏制_X小于总数时联机按回包分配()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 2);
            Assert.AreEqual(4, CurbRule.TotalMarks(game.AreaManager, 2), "前置：玩家2 共 4 个标记");

            // 被遏制者（玩家2）选择：从地区2 移除 2 个
            gateway.AnswerUids = new List<string> { areas[1] };
            gateway.AnswerAmounts = new List<int> { 2 };

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_split", "2"), Source(game, 1), null);

            Assert.IsTrue(gateway.HasCurbRequest, "X < 总数 → 应下发遏制分配决策请求");
            var req = gateway.CurbRequest;
            Assert.AreEqual(2, req.PlayerId, "被遏制者默认＝施术者的对手（玩家2）");
            Assert.AreEqual(2, req.Amount, "请求携带 X");
            Assert.AreEqual(2, req.OptionUids.Count, "候选=该玩家有标记的全部地区");
            Assert.AreEqual(2, req.OptionMaxAmounts[0], "每地区上限＝该地区标记数（地区1）");
            Assert.AreEqual(2, req.OptionMaxAmounts[1], "每地区上限＝该地区标记数（地区2）");

            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[0], 2), "未被选中的地区不受影响");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[1], 2), "按回包从地区2 移除 2 个");
        }

        /// <summary>服务端权威复检：回包超过每地区上限 → 按上限钳制；回包总量超过 X → 按 X 钳制。</summary>
        public void TestP4_遏制_回包超上限与超总量被服务端钳制()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 3);   // 总数 5，遏制 2

            // 非法回包：地区1 要 9 个（上限 2）、地区2 要 9 个（上限 3）→ 总量也不得超过 X=2
            gateway.AnswerUids = new List<string> { areas[0], areas[1] };
            gateway.AnswerAmounts = new List<int> { 9, 9 };

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_clamp", "2"), Source(game, 1), null);

            int removed = (2 - game.AreaManager.GetPowerMark(areas[0], 2)) + (3 - game.AreaManager.GetPowerMark(areas[1], 2));
            Assert.AreEqual(2, removed, "服务端复检后总移除量必须正好等于 X（超量被钳制）");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "地区1 按其上限 2 钳制后清空");
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(areas[1], 2), "已用满 X，地区2 不再被移除");
        }

        /// <summary>回包非法地区 UID 被忽略；不足部分按确定性兜底补齐（流程不卡死）。</summary>
        public void TestP4_遏制_回包非法地区被忽略并兜底补齐()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 2);

            gateway.AnswerUids = new List<string> { "no_such_area" };
            gateway.AnswerAmounts = new List<int> { 2 };

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_bad", "2"), Source(game, 1), null);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "非法回包被忽略 → 确定性兜底按地区序号移除");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[1], 2), "兜底不会多移除（总量仍为 X）");
        }

        public void TestP4_遏制_回包取消按确定性兜底()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway { CancelCurb = true };
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 1);
            game.AreaManager.AddPowerMark(areas[1], 2, 3);

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_cancel", "2"), Source(game, 1), null);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "取消 → 兜底先按地区序号填满（地区1 的 1 个）");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[1], 2), "剩余 1 个从地区2 移除");
        }

        // ==================== ④ 单机逐次询问 ====================

        public void TestP4_遏制_单机逐次询问且受每地区上限约束()
        {
            var game = NewGame(2);
            var picker = new CurbPickerStub();
            EffectEngine.Instance.UIHooks = picker;
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 1);
            game.AreaManager.AddPowerMark(areas[1], 2, 2);   // 总数 3，遏制 2

            // 每次都选"列表最后一个"（即标记最多的地区2）
            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_ui", "2"), Source(game, 1), null);

            Assert.AreEqual(2, picker.Prompts.Count, "逐次询问：X=2 → 询问 2 次");
            Assert.AreEqual(2, picker.Prompts[0].remaining, "首次询问 remaining=X");
            Assert.AreEqual(1, picker.Prompts[1].remaining, "第二次询问 remaining=1");
            foreach (var p in picker.Prompts)
                foreach (var o in p.options)
                    Assert.IsTrue(o.Removable > 0, "候选只应包含仍可移除的地区（可移除数 > 0）");

            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[0], 2), "地区1 未被选中，标记不变");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[1], 2), "两次都选地区2（其上限 2）→ 移除 2 个");
            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>单机：每地区上限＝该地区标记数（同一地区被选满后即从候选消失）。</summary>
        public void TestP4_遏制_单机同地区达上限后从候选消失()
        {
            var game = NewGame(2);
            var picker = new CurbPickerStub();
            EffectEngine.Instance.UIHooks = picker;
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 2, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 2);   // 总数 4，遏制 3
            int firstAreaChoices = 0;
            picker.Pick = options =>
            {
                // 前两次选地区1（其上限 2），之后地区1 应从候选消失
                if (options[0].AreaUid == areas[0] && firstAreaChoices < 2) { firstAreaChoices++; return areas[0]; }
                return options[options.Count - 1].AreaUid;
            };

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_ui3", "3"), Source(game, 1), null);

            Assert.AreEqual(3, picker.Prompts.Count, "逐次询问 3 次（X=3）");
            Assert.AreEqual(1, picker.Prompts[1].options.Find(o => o.AreaUid == areas[0]).Removable,
                "第一次移除后，地区1 的可移除数由 2 → 1");
            Assert.IsFalse(picker.Prompts[2].options.Exists(o => o.AreaUid == areas[0]),
                "地区1 选满（上限 2）后应从候选中消失");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(areas[0], 2), "地区1 按上限移除 2 个");
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[1], 2), "地区2 移除 1 个");
            EffectEngine.Instance.UIHooks = null;
        }

        // ==================== ⑤ 被遏制者方位 ====================

        public void TestP4_遏制_默认被遏制者是施术者对手()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 1, 2);   // 施术者（玩家1）
            game.AreaManager.AddPowerMark(areas[1], 2, 2);   // 对手（玩家2）
            gateway.AnswerUids = new List<string> { areas[1] };
            gateway.AnswerAmounts = new List<int> { 1 };

            // 施术者=玩家1，被遏制者缺省=对手（玩家2）；X=1 < 对手总数 2 → 走分配询问
            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_default", "1"), Source(game, 1), null);

            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[0], 1), "施术者自己的标记绝不被移除");
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[1], 2), "被遏制者（对手）的标记被移除 1 个");
            Assert.IsNotNull(gateway.CurbRequest, "X < 总数 → 应下发遏制分配请求");
            Assert.AreEqual(2, gateway.CurbRequest.PlayerId, "遏制请求发给对手（玩家2）");
        }

        public void TestP4_遏制_可指定遏制自己()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);

            game.AreaManager.AddPowerMark(areas[0], 1, 2);
            game.AreaManager.AddPowerMark(areas[1], 2, 2);
            gateway.AnswerUids = new List<string> { areas[0] };
            gateway.AnswerAmounts = new List<int> { 1 };

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_self", "1", "自己"), Source(game, 1), null);

            Assert.IsNotNull(gateway.CurbRequest, "X < 总数 → 应下发遏制分配请求");
            Assert.AreEqual(1, gateway.CurbRequest.PlayerId, "『自己』= 施术者（玩家1）");
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(areas[0], 1), "施术者自己的标记被移除 1 个");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[1], 2), "对手标记不变");
        }

        /// <summary>被遏制者没有任何标记 → 无操作、不发请求（X ≥ 0 全清也算空操作）。</summary>
        public void TestP4_遏制_被遏制者无标记时无操作()
        {
            var game = NewGame(2);
            var gateway = new CurbGateway();
            game.SetInputGateway(gateway);
            var areas = Areas(game);
            game.AreaManager.AddPowerMark(areas[0], 1, 2);

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_none", "1"), Source(game, 1), null);

            Assert.IsFalse(gateway.HasCurbRequest, "被遏制者（玩家2）无标记 → 不询问");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(areas[0], 1), "施术者标记不变");
        }

        // ==================== ⑥ 动词接线 ====================

        /// <summary>`ActionVerb.Curb` 应真正接到 `CurbRule`（Args=[X, 被遏制者] 解析正确）。</summary>
        public void TestP4_遏制_动词接线生效()
        {
            var game = NewGame(2);
            var areas = Areas(game);
            game.AreaManager.AddPowerMark(areas[0], 2, 4);

            EffectEngine.Instance.ResolveEffect(CurbEffect("T_curb_verb", "1"), Source(game, 1), null);

            Assert.AreEqual(3, game.AreaManager.GetPowerMark(areas[0], 2), "遏制1 → 无 UI 时确定性兜底移除 1 个");
        }
    }
}
