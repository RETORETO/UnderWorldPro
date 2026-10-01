using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Script.Game.Entities;
using Script.Net;

namespace Script.Manager
{
    /// <summary>
    /// 【P4·预测X】规则实现（用户 2026-09 规则文本）：
    /// **检视你牌库顶的 X 张牌，将其中任意数量的牌以任意顺序置于你的牌库顶，将其余的牌以任意顺序置于你的牌库底。**
    ///
    /// 复用口径（用户指定）：
    ///  · 分配窗口**复用调查对抗分配窗口**（`InvestigationWindow`：把牌拖到"牌库顶/牌库底"两区）；
    ///  · 数据层**复用调查对抗的取/放通道**（`ZoneManager.TakeTopForInvestigation` /
    ///    `ReturnAfterInvestigation`：取出的牌暂时脱离牌库列表、`CurrentZone` 保持 Deck）；
    ///  · 联机**复用同一组字段**：`ChoiceRequest.Kind.Prediction` + `OptionUids` + `OptionCardRefs`
    ///    （决策等待期间服务端不推快照，展示所需卡实例必须随请求下发）+ `Amount`；
    ///    回包语义与调查对抗一致：**`SelectedUids` = 置于牌库顶的卡（有序），未列出的按原序置于牌库底**。
    ///
    /// 三条路径（语义一致）：
    ///  · **单机**：`IGameUIHooks.PromptPrediction`（View 打开调查同款分配窗口）；
    ///  · **联机**：`IInputGateway` 下发 `Prediction` 请求（服务端权威：回包里的卡必须属于本次取出的 X 张）；
    ///  · **无 UI / 无网关**（headless/测试）：确定性兜底＝**原序全部留在牌库顶**（牌库不发生任何变化）。
    ///
    /// 消费点：`EffectEngine.ExecuteVerb` 的 `ActionVerb.Predict`（Args[0] = X，缺省 1）。
    /// </summary>
    public static class PredictionRule
    {
        /// <summary>主入口：让 source 的控制者执行"预测X"。</summary>
        public static void Resolve(EffectEngine ee, CardInstance source, int count)
        {
            if (ee == null || source == null) { GD.Print("[预测] ⚠ 跳过：effectEngine/source 为空"); return; }
            int predictor = source.ControllerId;
            if (predictor <= 0) { GD.Print($"[预测] ⚠ 跳过：来源控制者无效（{predictor}）"); return; }

            var zm = ee.ZoneManagerRef;
            if (zm == null) { GD.Print("[预测] ⚠ 跳过：ZoneManager 未注入"); return; }
            if (count <= 0) count = 1;

            // 数据层取牌：与调查对抗完全同一条通道（取出后 CurrentZone 仍为 Deck，不在任何区域桶）
            var cards = zm.TakeTopForInvestigation(predictor, count);
            if (cards == null || cards.Count == 0)
            {
                GD.Print($"[预测] 玩家{predictor} 牌库为空 → 无可检视的牌");
                return;
            }
            GD.Print($"[预测] 玩家{predictor} 检视牌库顶 {cards.Count} 张（预测{count}）：{DescribeCards(cards)}");

            // ① 联机：一次性下发分配请求（异步续算）
            if (ee.InputGateway != null)
            {
                _ = ResolveViaGatewayAsync(ee, zm, predictor, cards);
                return;
            }

            // ② 单机：复用调查对抗分配窗口
            if (ee.UIHooks != null)
            {
                ee.UIHooks.PromptPrediction(predictor, cards.Count, cards,
                    (toTop, toBottom) => Apply(zm, predictor, cards, toTop, toBottom));
                return;
            }

            // ③ 无 UI / 无网关：确定性兜底＝原序全部置顶（牌库不变，仅"看过"）
            GD.Print($"[预测] 无 UI/网关：确定性兜底 → 原序全部留在牌库顶（牌库不变）");
            Apply(zm, predictor, cards, cards, new List<CardInstance>());
        }

        /// <summary>
        /// 归一化并落地"置顶/置底"：以本次取出的 cards 为**全集**，
        /// `toTop` 按给定顺序排在最前、`toBottom` 次之，**任何漏掉的卡按原序补进牌库底**
        /// （防止 UI 漏放/回包缺卡导致牌库凭空少牌）。
        /// </summary>
        private static void Apply(ZoneManager zm, int predictor, List<CardInstance> cards,
            List<CardInstance> toTop, List<CardInstance> toBottom)
        {
            var top = new List<CardInstance>();
            var seen = new HashSet<CardInstance>();
            if (toTop != null)
                foreach (var c in toTop)
                    if (c != null && cards.Contains(c) && seen.Add(c)) top.Add(c);
            var bottom = new List<CardInstance>();
            if (toBottom != null)
                foreach (var c in toBottom)
                    if (c != null && cards.Contains(c) && seen.Add(c)) bottom.Add(c);
            foreach (var c in cards)
                if (seen.Add(c)) bottom.Add(c);

            zm.ReturnAfterInvestigation(predictor, top, bottom);
            GD.Print($"[预测] 玩家{predictor} 分配完成：牌库顶 {top.Count} 张（{DescribeCards(top)}），牌库底 {bottom.Count} 张（{DescribeCards(bottom)}）");
        }

        /// <summary>联机：下发 `Prediction` 请求（候选=本次取出的 X 张，含展示引用），回包后权威复检并落地。</summary>
        private static async Task ResolveViaGatewayAsync(EffectEngine ee, ZoneManager zm, int predictor,
            List<CardInstance> cards)
        {
            var req = new ChoiceRequest
            {
                PlayerId = predictor,
                RequestKind = ChoiceRequest.Kind.Prediction,
                Amount = cards.Count,
                OptionUids = cards.ConvertAll(c => c.UID),
                // 决策等待期间服务端不推快照 → 展示所需的卡实例必须随请求下发（与调查对抗同款字段）
                OptionCardRefs = cards.ConvertAll(c => ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId)),
                Prompt = $"预测{cards.Count}：把任意数量的牌置于牌库顶（其余置于牌库底）",
                ForceChoice = true,
            };

            ChoiceResult result = null;
            try
            {
                result = await ee.InputGateway.RequestChoice(predictor, req);
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[预测] ⚠ 联机分配请求异常：{ex.GetType().Name}: {ex.Message}（按确定性兜底落地）");
            }

            // 服务端权威复检：回包里的 UID 必须属于本次取出的 X 张（非法一律忽略）；
            // 未列出的卡按原序置于牌库底（与调查对抗同一口径）。
            // 特例：**没有有效决策**（超时/取消/网关异常）→ 视为"没看过"，原序全部留在牌库顶（牌库不变）；
            // 与"玩家主动把全部牌放到底"（Cancelled=false 且 SelectedUids 为空）区分开。
            if (result == null || result.Cancelled)
            {
                GD.Print($"[预测] 玩家{predictor} 无有效决策（取消/超时）→ 原序全部留在牌库顶（牌库不变）");
                Apply(zm, predictor, cards, cards, new List<CardInstance>());
                return;
            }

            var toTop = new List<CardInstance>();
            if (result.SelectedUids != null)
            {
                foreach (var uid in result.SelectedUids)
                {
                    var c = cards.Find(x => x.UID == uid);
                    if (c == null)
                    {
                        GD.PrintErr($"[预测] ⛔ 忽略非法/过期的牌 {uid}（不属于本次取出的 {cards.Count} 张）");
                        continue;
                    }
                    if (!toTop.Contains(c)) toTop.Add(c);
                }
            }
            var toBottom = new List<CardInstance>();
            foreach (var c in cards) if (!toTop.Contains(c)) toBottom.Add(c);

            Apply(zm, predictor, cards, toTop, toBottom);
            // 【A55·联机 UI 刷新】异步落地 ⇒ 主动通知下发快照（牌库顶/底顺序变化客户端需立刻看到）
            ee?.TurnManagerRef?.NotifyResolutionSettled();
        }

        private static string DescribeCards(List<CardInstance> cards)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cards.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(cards[i]?.PlayerTemplate?.CardName ?? cards[i]?.UID ?? "?");
            }
            return sb.ToString();
        }
    }
}
