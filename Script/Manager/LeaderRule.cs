using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Net;

namespace Script.Manager
{
    /// <summary>
    /// 【P4·词条规则】领袖（Leader）——用户 2026-09 给出的规则文本：
    ///  · 同一控制者在场上**至多 1 个**拥有【领袖】的角色；
    ///  · 一个领袖进场后若超出上限，**立刻**由该控制者选择其中一个（**含刚进场的那个**）**牺牲**：
    ///    该牺牲是**规则性结算**——**不入堆叠**、不可被响应（堆叠上已有的东西照常结算）；
    ///  · **进场触发照常生效**：进场触发在进场瞬间就已收集并等待结算，规则性牺牲不撤销它
    ///    （牺牲若消灭了刚进场的领袖，其进场触发仍会结算，只是目标可能已丢失）；
    ///  · 牺牲是"死亡"的一种（`CardManager.DeathCause.Sacrifice`），会照常广播死亡触发。
    ///
    /// 消费点：`ZoneManager.MoveToArea` 的进场触点（非场地区 → 争夺区）之后，经
    /// `ZoneManager.OnCardEnteredArea` 委托调用本类（装配见 `GameInitializer`）。
    /// 只在"进场"时检查：争夺区→争夺区的移动不重新检查（该卡在场期间未新增领袖）。
    ///
    /// 决策路径（与项目既有口径一致，三条路径语义相同）：
    ///  · **单机**：复用 `IGameUIHooks.PromptPickCard`（allowCancel=false，候选=该控制者场上全部领袖）；
    ///  · **联机**：经 `IInputGateway` 下发 `ChoiceRequest.Kind.SelectTarget`（候选=全部领袖，强制选择），
    ///    回包后续算（服务端权威：越权/超时/取消一律按确定性兜底落地）；
    ///  · **无 UI / 无网关**（headless/测试）：确定性兜底——优先牺牲**最新进场**的那个，循环至只剩 1 个，
    ///    保证"至多 1 个领袖"这条不变量始终成立。
    /// </summary>
    public static class LeaderRule
    {
        /// <summary>词条名（关键字值）。</summary>
        public const string Keyword = "领袖";

        /// <summary>是否为领袖：运行期授予（持续效果注册后）∪ 卡面固有声明（`HasInherentKeyword`）。</summary>
        public static bool IsLeader(CardInstance card)
            => card != null
               && (card.HasKeyword(Keyword) || (card.PlayerTemplate?.HasInherentKeyword(Keyword) ?? false));

        /// <summary>某控制者**场上**（争夺区）的全部领袖，按"场上顺序"（先进场在前）返回。</summary>
        public static List<CardInstance> LeadersOnField(CardManager cm, int controllerId)
        {
            var list = new List<CardInstance>();
            var manager = cm ?? CardManager.Instance;
            if (manager == null || controllerId <= 0) return list;
            foreach (var c in manager.GetAllInstances())
            {
                if (c == null || c.IsDying) continue;
                if (c.CurrentZone != CardZone.AreaZone) continue;
                if (c.ControllerId != controllerId) continue;
                if (!IsLeader(c)) continue;
                list.Add(c);
            }
            return list;
        }

        /// <summary>
        /// 【进场消费点】一张卡进入争夺区后调用：若它是领袖且该控制者场上领袖数超限，
        /// 则立刻让该控制者选一个牺牲（不入堆叠），直到只剩 1 个。
        /// </summary>
        public static void EnforceOnEntry(CardInstance entered, CardManager cm, EffectEngine ee)
        {
            if (entered == null) return;
            if (entered.CurrentZone != CardZone.AreaZone) return;
            if (entered.ControllerId <= 0) return;
            if (!IsLeader(entered)) return;
            EnforceInvariant(cm, ee, entered.ControllerId, entered);
        }

        /// <summary>
        /// 强制执行"至多 1 个领袖"的不变量：每次循环让控制者选一个领袖牺牲，直到场上只剩 1 个。
        /// `newest` = 触发本次检查的那张卡（兜底优先级最高、也是联机决策请求的来源卡）。
        /// </summary>
        private static void EnforceInvariant(CardManager cm, EffectEngine ee, int controllerId, CardInstance newest)
        {
            var manager = cm ?? CardManager.Instance;
            if (manager == null) return;

            // 防呆上限：正常最多循环（领袖数-1）次；上限保护避免任何异常导致死循环。
            for (int guard = 0; guard < 8; guard++)
            {
                var leaders = LeadersOnField(manager, controllerId);
                if (leaders.Count <= 1) return;

                GD.Print($"[领袖] 玩家{controllerId} 场上领袖数 {leaders.Count} > 1（{Describe(leaders)}）→ 立即由该控制者选择一个牺牲（不入堆叠）");

                // ① 联机：经网关向该控制者下发选择请求（异步续算，服务端权威）
                if (ee?.InputGateway != null)
                {
                    _ = AskViaGatewayAsync(manager, ee, controllerId, leaders, newest);
                    return;
                }

                // ② 单机：复用通用选卡窗口（不可取消——必须选一个，规则如此）
                if (ee?.UIHooks != null)
                {
                    ee.UIHooks.PromptPickCard(controllerId,
                        $"领袖：同控制者场上至多 1 个领袖，请选择一个牺牲",
                        leaders, allowCancel: false,
                        picked =>
                        {
                            Sacrifice(manager, picked ?? leaders[leaders.Count - 1]);
                            // 回调内再判一次：仍超限（理论上只在异常输入下发生）时继续询问
                            EnforceInvariant(manager, ee, controllerId, newest);
                        });
                    return;
                }

                // ③ 无 UI / 无网关：确定性兜底（优先牺牲最新进场的那个）
                var fallback = ResolveDeterministicPick(leaders, newest);
                Sacrifice(manager, fallback);
            }
            GD.PrintErr($"[领袖] ⚠ 玩家{controllerId} 的领袖数量仍未收敛（防呆上限触发），已停止继续牺牲");
        }

        /// <summary>确定性兜底选择：优先本次进场的那张（仍在候选内时），否则取"最后进场"的候选。</summary>
        private static CardInstance ResolveDeterministicPick(List<CardInstance> leaders, CardInstance newest)
        {
            if (newest != null && leaders.Contains(newest)) return newest;
            return leaders[leaders.Count - 1];
        }

        /// <summary>联机路径：下发 SelectTarget 决策请求（候选=该控制者场上全部领袖，强制选择），回包后落地牺牲。</summary>
        private static async Task AskViaGatewayAsync(CardManager cm, EffectEngine ee, int controllerId,
            List<CardInstance> leaders, CardInstance newest)
        {
            ChoiceResult result = null;
            try
            {
                result = await ee.InputGateway.RequestChoice(controllerId, new ChoiceRequest
                {
                    PlayerId = controllerId,
                    RequestKind = ChoiceRequest.Kind.SelectTarget,
                    SourceUid = newest?.UID,
                    OptionUids = leaders.ConvertAll(c => c.UID),
                    Prompt = "领袖：同控制者场上至多 1 个领袖，请选择一个牺牲",
                    ForceChoice = true,
                });
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[领袖] ⚠ 联机决策请求异常：{ex.GetType().Name}: {ex.Message}（按确定性兜底落地）");
            }

            // 服务端权威复检：回包必须落在已下发的候选内；取消/超时/越权 → 确定性兜底。
            CardInstance picked = null;
            if (!string.IsNullOrEmpty(result?.SelectedUid))
            {
                var candidate = cm.GetInstance(result.SelectedUid);
                if (candidate != null && leaders.Contains(candidate)) picked = candidate;
                else GD.Print($"[领袖] ⚠ 忽略非法/过期的牺牲选择 {result.SelectedUid}（按确定性兜底落地）");
            }
            if (picked == null) picked = ResolveDeterministicPick(leaders, newest);

            Sacrifice(cm, picked);
            EnforceInvariant(cm, ee, controllerId, newest);
        }

        /// <summary>落地"牺牲"：走死亡唯一入口（DeathCause.Sacrifice），照常广播死亡触发。</summary>
        private static void Sacrifice(CardManager cm, CardInstance card)
        {
            if (card == null) return;
            GD.Print($"[领袖] 规则牺牲：{card.PlayerTemplate?.CardName}（玩家{card.ControllerId}）—— 同控制者场上至多 1 个领袖");
            cm.ResolveDeath(card, CardManager.DeathCause.Sacrifice);
        }

        private static string Describe(List<CardInstance> leaders)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < leaders.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(leaders[i].PlayerTemplate?.CardName ?? leaders[i].UID);
            }
            return sb.ToString();
        }
    }
}
