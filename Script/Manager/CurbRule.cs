using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Net;

namespace Script.Manager
{
    /// <summary>
    /// 【P4·遏制X】规则实现（用户 2026-09 规则文本）：
    ///  · **遏制X** = 由**被遏制者**（默认＝施术者的对手）选择并移除他**自己所拥有**的 X 个势力标志；
    ///  · **X ≥ 该玩家势力标志总数** → **自动全清**（不弹窗、不询问）；
    ///  · **X &lt; 总数** → 由**被遏制者按地区分配**这 X 次移除，**每地区上限 = 该地区该玩家的标记数**。
    ///
    /// 三条决策路径（语义一致）：
    ///  · **单机**：`IGameUIHooks.PromptCurbMarks` 逐次询问（每次移除 1 个），Controller 在回调内落地；
    ///  · **联机**：`IInputGateway` 下发 `ChoiceRequest.Kind.CurbMarks`（一次性分配：OptionUids=地区、
    ///    OptionMaxAmounts=每地区上限、Amount=X），服务端**权威复检**（逐地区按上限钳制、总量不超过 X）；
    ///  · **无 UI / 无网关**（headless/测试）：确定性兜底——按地区序号**贪心填满**，逐地区取到上限为止。
    ///
    /// 消费点：`EffectEngine.ExecuteVerb` 的 `ActionVerb.Curb`（Args：[X（缺省1）, 被遏制者（缺省"对手"）]）。
    /// </summary>
    public static class CurbRule
    {
        /// <summary>被遏制者方位解析：`对手`（缺省，= source 控制者的对手）/ `自己`（= source 控制者）。</summary>
        public static int ResolveCurbedPlayer(CardInstance source, string who)
        {
            int self = source?.ControllerId ?? -1;
            if (self <= 0) return -1;
            return who == "自己" ? self : (self == 1 ? 2 : 1);
        }

        /// <summary>收集"可移除选项"：场上所有地区中，该玩家标记数 &gt; 0 的（按地区序号升序）。</summary>
        public static List<CurbMarkOption> CollectOptions(AreaManager am, int playerId)
        {
            var list = new List<CurbMarkOption>();
            if (am == null || playerId <= 0) return list;
            var uids = am.GetOrderedAreaUids();
            if (uids == null) return list;
            foreach (var uid in uids)
            {
                int n = am.GetPowerMark(uid, playerId);
                if (n <= 0) continue;
                list.Add(new CurbMarkOption { AreaUid = uid, SlotIndex = am.GetSlotIndex(uid), Removable = n });
            }
            return list;
        }

        /// <summary>该玩家在全部地区的势力标志总数（遏制 X 的比较基准）。</summary>
        public static int TotalMarks(AreaManager am, int playerId)
        {
            int total = 0;
            foreach (var o in CollectOptions(am, playerId)) total += o.Removable;
            return total;
        }

        /// <summary>确定性分配（无 UI/网关；或联机回包缺失时兜底）：按地区序号贪心填满 amount。</summary>
        public static List<(string areaUid, int amount)> DeterministicPlan(List<CurbMarkOption> options, int amount)
        {
            var plan = new List<(string, int)>();
            int left = amount;
            if (options == null) return plan;
            foreach (var o in options)
            {
                if (left <= 0) break;
                int take = System.Math.Min(left, o.Removable);
                if (take <= 0) continue;
                plan.Add((o.AreaUid, take));
                left -= take;
            }
            return plan;
        }

        /// <summary>按分配执行移除（逐地区钳制到该地区可移除数），返回实际移除总数。</summary>
        public static int Apply(AreaManager am, int playerId, IEnumerable<(string areaUid, int amount)> plan)
        {
            int removed = 0;
            if (am == null || plan == null) return 0;
            foreach (var (areaUid, amount) in plan)
            {
                if (amount <= 0) continue;
                removed += am.RemovePowerMark(areaUid, playerId, amount);
            }
            return removed;
        }

        /// <summary>
        /// 主入口（`ActionVerb.Curb` 消费）：
        /// 解析 X 与被遏制者 → X ≥ 总数 时自动全清；否则按决策路径询问被遏制者并落地。
        /// </summary>
        public static void Resolve(EffectEngine ee, CardInstance source, int amount, string who)
        {
            var am = ee?.TurnManagerRef?.AreaManagerRef;
            int curbed = ResolveCurbedPlayer(source, who);
            if (am == null || curbed <= 0)
            {
                GD.Print($"[遏制] ⚠ 跳过：AreaManager={(am == null ? "null" : "ok")}, 被遏制者={curbed}");
                return;
            }
            if (amount <= 0) amount = 1;

            var options = CollectOptions(am, curbed);
            int total = 0;
            foreach (var o in options) total += o.Removable;

            if (total <= 0)
            {
                GD.Print($"[遏制] 玩家{curbed} 没有任何势力标志可移除（X={amount}）→ 无需结算");
                return;
            }

            // ① X ≥ 总数 → 自动全清（不弹窗、不询问）
            if (amount >= total)
            {
                int removedAll = Apply(am, curbed, DeterministicPlan(options, total));
                GD.Print($"[遏制] X={amount} ≥ 玩家{curbed} 的势力标志总数 {total} → 自动全清（不询问），共移除 {removedAll} 个");
                return;
            }

            GD.Print($"[遏制] X={amount} < 玩家{curbed} 的势力标志总数 {total} → 由被遏制者按地区分配（每地区上限=该地区标记数）");

            // ② 联机：一次性下发分配请求（异步续算；服务端权威复检）
            if (ee.InputGateway != null)
            {
                _ = ResolveViaGatewayAsync(ee, am, curbed, amount, options);
                return;
            }

            // ③ 单机：逐次询问（每次 1 个），直到 X 分配完
            if (ee.UIHooks != null)
            {
                PromptSequential(ee, am, curbed, amount);
                return;
            }

            // ④ 无 UI / 无网关：确定性兜底
            int removedFallback = Apply(am, curbed, DeterministicPlan(options, amount));
            GD.Print($"[遏制] 无 UI/网关：确定性兜底（按地区序号贪心）移除 {removedFallback} 个（目标 {amount}）");
        }

        /// <summary>单机逐次询问：每次让被遏制者选一个地区移除 1 个，直到 X 分配完。</summary>
        private static void PromptSequential(EffectEngine ee, AreaManager am, int curbed, int remaining)
        {
            if (remaining <= 0) return;
            var options = CollectOptions(am, curbed);
            if (options.Count == 0)
            {
                GD.Print($"[遏制] 候选地区已耗尽（还需 {remaining} 个）→ 结束分配");
                return;
            }
            ee.UIHooks.PromptCurbMarks(curbed, remaining, options, pickedUid =>
            {
                // 玩家放弃 / 回包为空 → 确定性兜底补齐（不卡流程）
                string uid = string.IsNullOrEmpty(pickedUid) || options.Find(o => o.AreaUid == pickedUid) == null
                    ? options[0].AreaUid
                    : pickedUid;
                int removed = am.RemovePowerMark(uid, curbed, 1);
                GD.Print($"[遏制] 被遏制者玩家{curbed} 选择地区 {uid} 移除 {removed} 个（剩余待分配 {remaining - removed}）");
                PromptSequential(ee, am, curbed, remaining - System.Math.Max(removed, 0));
            });
        }

        /// <summary>联机：下发一次性分配请求（OptionUids=地区、OptionMaxAmounts=每地区上限、Amount=X），回包后权威复检并落地。</summary>
        private static async Task ResolveViaGatewayAsync(EffectEngine ee, AreaManager am, int curbed, int amount,
            List<CurbMarkOption> options)
        {
            var req = new ChoiceRequest
            {
                PlayerId = curbed,
                RequestKind = ChoiceRequest.Kind.CurbMarks,
                Amount = amount,
                OptionUids = options.ConvertAll(o => o.AreaUid),
                OptionMaxAmounts = options.ConvertAll(o => o.Removable),
                OptionLabels = options.ConvertAll(o => $"地区{o.SlotIndex}"),
                Prompt = $"遏制{amount}：请按地区分配要移除的势力标志（每地区上限＝该地区标记数）",
                ForceChoice = true,
            };

            ChoiceResult result = null;
            // 【2026-09·原子操作期间的玩家选择】等待被遏制者回包期间持原子锁（服务端无 View）
            await ee.HoldDecisionAsync(async () =>
            {
                try
                {
                    result = await ee.InputGateway.RequestChoice(curbed, req);
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[遏制] ⚠ 联机分配请求异常：{ex.GetType().Name}: {ex.Message}（按确定性兜底落地）");
                }
            });

            // 服务端权威复检：逐地区按上限钳制，总量不超过 X；非法/缺失一律按剩余量确定性补齐。
            var plan = new List<(string, int)>();
            int assigned = 0;
            var seen = new HashSet<string>();
            if (result != null && !result.Cancelled && result.SelectedUids != null)
            {
                for (int i = 0; i < result.SelectedUids.Count; i++)
                {
                    string uid = result.SelectedUids[i];
                    int amt = i < result.SelectedAmounts.Count ? result.SelectedAmounts[i] : 0;
                    if (string.IsNullOrEmpty(uid) || amt <= 0) continue;
                    var opt = options.Find(o => o.AreaUid == uid);
                    if (opt == null)
                    {
                        GD.PrintErr($"[遏制] ⛔ 忽略非法地区分配 {uid}（不在下发的候选内）");
                        continue;
                    }
                    if (!seen.Add(uid))
                    {
                        GD.PrintErr($"[遏制] ⛔ 忽略重复地区分配 {uid}");
                        continue;
                    }
                    int take = System.Math.Min(amt, System.Math.Min(opt.Removable, amount - assigned));
                    if (take <= 0) continue;
                    plan.Add((uid, take));
                    assigned += take;
                }
            }

            // 剩余量兜底补齐（玩家取消/超时/少分配都不卡流程）：按地区序号填充各自剩余容量
            if (assigned < amount)
            {
                foreach (var o in options)
                {
                    if (assigned >= amount) break;
                    int already = 0;
                    foreach (var p in plan) if (p.Item1 == o.AreaUid) already += p.Item2;
                    int room = o.Removable - already;
                    if (room <= 0) continue;
                    int take = System.Math.Min(room, amount - assigned);
                    plan.Add((o.AreaUid, take));
                    assigned += take;
                }
            }

            int removed = Apply(am, curbed, plan);
            GD.Print($"[遏制] 联机分配落地：被遏制者玩家{curbed} 共移除 {removed} 个（目标 {amount}，{(result == null || result.Cancelled ? "回包缺失/取消 → 含兜底" : "按回包")}）");
            // 【A55·联机 UI 刷新】异步落地 ⇒ 主动通知下发快照（否则客户端要等下一条上行才更新）
            ee?.TurnManagerRef?.NotifyResolutionSettled();
        }
    }
}
