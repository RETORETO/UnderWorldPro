using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Net;

namespace Script.Manager
{
    /// <summary>
    /// 【P3·K25】「道途」唯一性（JC034 心兽之道「你只能操控至多一个道途」）。
    /// **用户 2026-09 口径**：与【领袖】**同一套处理**——场上存在多个道途时，**由该玩家选择一个牺牲**。
    ///
    /// 规则形状（与 `LeaderRule` 逐条对齐，便于维护者对照）：
    ///  · **至多 1 张**：同一控制者在**场上**（`AttachedZone`＝结附秘社/角色，或 `AreaZone`）的「道途」卡至多 1 张；
    ///  · 超出上限 → **立刻**由该控制者选择其中一个（**含刚进场的那张**）**牺牲**：
    ///    该牺牲是**规则性结算**——**不入堆叠**、不可被响应（堆叠上已有条目照常结算）；
    ///  · **进场触发照常生效**（规则性牺牲不撤销已收集的进场触发）；
    ///  · 牺牲是"死亡"的一种（`CardManager.DeathCause.Sacrifice`），**会广播死亡触发**（与 A1 一致）。
    ///  · **规则只在"印了该规则的卡在场"时生效**（读 `StaticKind.UniquePathAtMostOne` 标记）——
    ///    未印该规则的「道途」卡之间不互相限制。
    ///
    /// 决策路径（三条路径语义相同，与 `LeaderRule` 完全一致）：
    ///  · **单机**：`IGameUIHooks.PromptPickCard`（`allowCancel=false`，候选＝该控制者场上全部道途）；
    ///  · **联机**：`IInputGateway` 下发 `ChoiceRequest.Kind.SelectTarget`（强制选择），回包后服务端权威复检；
    ///  · **无 UI / 无网关**（headless/测试）：确定性兜底——优先牺牲**最新进场**的那张，循环至只剩 1 张。
    ///
    /// 消费点：`ZoneManager.OnPostMove`（任何区域变更之后；装配见 `GameInitializer`）。
    /// 只用"是否在场上 + 子类别是否为道途"过滤，故进场/移动/回场都会被覆盖。
    /// </summary>
    public static class PathRule
    {
        /// <summary>规则涉及的子类别名（卡库写法：`SubCategories = ["道途", …]`）。</summary>
        public const string SubCategory = "道途";

        /// <summary>是否「道途」：按"当前实际子类别"判定（模板固有 ∪ 运行期授予，与既有子类别口径一致）。</summary>
        public static bool IsPath(CardInstance card) => card != null && card.HasSubCategory(SubCategory);

        /// <summary>是否在**场上**（道途＝附属，通常结附于秘社 → `AttachedZone`；兼容 `AreaZone`）。</summary>
        private static bool IsOnField(CardInstance card)
            => card != null && (card.CurrentZone == CardZone.AttachedZone || card.CurrentZone == CardZone.AreaZone);

        /// <summary>某控制者**场上正面**的全部道途，按确定性顺序（`GetAllInstances` 顺序＝实例创建顺序）返回。
        /// 【2026-09 A60/A61】背面朝上的卡不提供其效果 ⇒ 暗藏道途**不占**"至多 1 个道途"名额（与领袖同口径）。</summary>
        public static List<CardInstance> PathsOnField(CardManager cm, int controllerId)
        {
            var list = new List<CardInstance>();
            var manager = cm ?? CardManager.Instance;
            if (manager == null || controllerId <= 0) return list;
            foreach (var c in manager.GetAllInstances())
            {
                if (c == null || c.IsDying) continue;
                if (c.ControllerId != controllerId) continue;
                if (!IsOnField(c)) continue;
                if (c.IsCovert) continue;
                if (!IsPath(c)) continue;
                list.Add(c);
            }
            return list;
        }

        /// <summary>该控制者场上的道途中，是否有卡声明了"至多一个道途"这条规则。</summary>
        private static bool RuleDeclared(List<CardInstance> paths, EffectEngine ee)
        {
            foreach (var p in paths)
                if (ee?.HasStaticFlag(p, StaticKind.UniquePathAtMostOne) == true) return true;
            return false;
        }

        /// <summary>
        /// 【移动消费点】一张卡进入/变更区域后调用：若它是「道途」且该控制者场上道途数超限（且有卡声明本规则），
        /// 则立刻让该控制者选一个牺牲（不入堆叠），直到只剩 1 张。
        /// </summary>
        public static void EnforceOnEntry(CardInstance moved, CardManager cm, EffectEngine ee)
        {
            if (moved == null) return;
            if (!IsOnField(moved)) return;
            if (moved.ControllerId <= 0) return;
            if (!IsPath(moved)) return;
            EnforceInvariant(cm, ee, moved.ControllerId, moved);
        }

        /// <summary>强制执行"至多 1 张道途"的不变量：每次循环让控制者选一张道途牺牲，直到只剩 1 张。</summary>
        private static void EnforceInvariant(CardManager cm, EffectEngine ee, int controllerId, CardInstance newest)
        {
            var manager = cm ?? CardManager.Instance;
            if (manager == null) return;

            // 防呆上限：正常最多循环（道途数-1）次。
            for (int guard = 0; guard < 8; guard++)
            {
                var paths = PathsOnField(manager, controllerId);
                if (paths.Count <= 1) return;
                if (!RuleDeclared(paths, ee)) return;   // 场上没有任何卡印该规则 → 不施加限制

                GD.Print($"[道途] 玩家{controllerId} 场上道途数 {paths.Count} > 1（{Describe(paths)}）→ 立刻由该控制者选择一个牺牲（不入堆叠）");

                // ① 联机：经网关向该控制者下发选择请求（异步续算，服务端权威）
                if (ee?.InputGateway != null)
                {
                    _ = AskViaGatewayAsync(manager, ee, controllerId, paths, newest);
                    return;
                }

                // ② 单机：复用通用选卡窗口（不可取消——必须选一个，规则如此）
                if (ee?.UIHooks != null)
                {
                    ee.UIHooks.PromptPickCard(controllerId,
                        "道途：同一玩家至多操控 1 个道途，请选择一个牺牲",
                        paths, allowCancel: false,
                        picked =>
                        {
                            Sacrifice(manager, picked ?? paths[paths.Count - 1]);
                            EnforceInvariant(manager, ee, controllerId, newest);
                        });
                    return;
                }

                // ③ 无 UI / 无网关：确定性兜底（优先牺牲最新进场的那个）
                Sacrifice(manager, ResolveDeterministicPick(paths, newest));
            }
            GD.PrintErr($"[道途] ⚠ 玩家{controllerId} 的道途数量仍未收敛（防呆上限触发），已停止继续牺牲");
        }

        /// <summary>确定性兜底选择：优先本次进场的那张（仍在候选内时），否则取"最后进场"的候选。</summary>
        private static CardInstance ResolveDeterministicPick(List<CardInstance> paths, CardInstance newest)
        {
            if (newest != null && paths.Contains(newest)) return newest;
            return paths[paths.Count - 1];
        }

        /// <summary>联机路径：下发 SelectTarget 决策请求（候选＝该控制者场上全部道途，强制选择）。</summary>
        private static async Task AskViaGatewayAsync(CardManager cm, EffectEngine ee, int controllerId,
            List<CardInstance> paths, CardInstance newest)
        {
            ChoiceResult result = null;
            // 【2026-09·原子操作期间的玩家选择】等待玩家回包期间持原子锁（与 LeaderRule 同款）
            await ee.HoldDecisionAsync(async () =>
            {
                try
                {
                    result = await ee.InputGateway.RequestChoice(controllerId, new ChoiceRequest
                    {
                        PlayerId = controllerId,
                        RequestKind = ChoiceRequest.Kind.SelectTarget,
                        SourceUid = newest?.UID,
                        OptionUids = paths.ConvertAll(c => c.UID),
                        Prompt = "道途：同一玩家至多操控 1 个道途，请选择一个牺牲",
                        ForceChoice = true,
                    });
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[道途] ⚠ 联机决策请求异常：{ex.GetType().Name}: {ex.Message}（按确定性兜底落地）");
                }
            });

            // 服务端权威复检：回包必须落在已下发的候选内；取消/超时/越权 → 确定性兜底。
            CardInstance picked = null;
            if (!string.IsNullOrEmpty(result?.SelectedUid))
            {
                var candidate = cm.GetInstance(result.SelectedUid);
                if (candidate != null && paths.Contains(candidate)) picked = candidate;
                else GD.Print($"[道途] ⚠ 忽略非法/过期的牺牲选择 {result.SelectedUid}（按确定性兜底落地）");
            }
            if (picked == null) picked = ResolveDeterministicPick(paths, newest);

            Sacrifice(cm, picked);
            EnforceInvariant(cm, ee, controllerId, newest);
            // 【A55·联机 UI 刷新】异步落地 ⇒ 主动通知下发快照（同 LeaderRule）
            ee?.TurnManagerRef?.NotifyResolutionSettled();
        }

        /// <summary>落地"牺牲"：走死亡唯一入口（`DeathCause.Sacrifice`），照常广播死亡触发。</summary>
        private static void Sacrifice(CardManager cm, CardInstance card)
        {
            if (card == null) return;
            GD.Print($"[道途] 规则牺牲：{card.PlayerTemplate?.CardName}（玩家{card.ControllerId}）—— 同一玩家至多操控 1 个道途");
            cm.ResolveDeath(card, CardManager.DeathCause.Sacrifice);
        }

        private static string Describe(List<CardInstance> paths)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(paths[i].PlayerTemplate?.CardName ?? paths[i].UID);
            }
            return sb.ToString();
        }
    }
}
