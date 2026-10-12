using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Net;

namespace Script.Manager
{
    /// <summary>
    /// 【P2·合法性投影】服务端为某个查看者预先算好"操作合法性"（可发动 / 合法目标），随快照下发。
    ///
    /// 目的（消灭两套判定）：客户端原先自己调 `CardManager.IsLegalPlayTarget` / `ActivateEligibility.CanActivate`
    /// 做提前判断。这些函数本身是**同一份实现**，但客户端要把服务端全部输入投影齐（本局 PlayerManager、
    /// 静止标记、运行时授予、候选范围…）才能得到同解，漏一项就漂移（JC005 有【心】被挡、JC001 装备结附被放行…）。
    /// 改为服务端算好候选集合下发后：
    ///   · 客户端只做"渲染 + 成员判断"，不再需要自己拼输入；
    ///   · 候选范围由服务端决定（客户端不再有自己的枚举范围，消除"只扫争夺区"这类结构性缺口）；
    ///   · 规则实现只剩服务端一份，新增限制/状态天然同步。
    ///
    /// 纪律：本类**只调用**生产规则函数（IsLegalPlayTarget / CanActivate / IsLegalActivateTarget），
    /// 不复制任何判定逻辑；客户端在无投影时退回调用同一批函数（见 ArenaOl 的包装方法）。
    /// </summary>
    public static class LegalityProjection
    {
        /// <summary>为 viewerPlayerId 生成合法性投影；viewerPlayerId &lt;= 0 或依赖缺失时返回 null（客户端将退回本地判定）。</summary>
        public static GameViewSnap Build(CardManager cm, PlayerManager pm, TurnManager tm, ZoneManager zm,
            int viewerPlayerId)
        {
            if (cm == null || pm == null || tm == null || zm == null || viewerPlayerId <= 0) return null;

            var view = new GameViewSnap { ViewerPlayerId = viewerPlayerId };

            // 候选池：争夺区 + 附属区 + 秘社 + **资产区** + **墓地**。
            // 与单机 `Arena.BeginSorceryTargetSelection` / 联机事务卡选目标窗口的候选范围一致，
            // 由服务端统一决定，客户端不再各自枚举。
            // 【K14】补 资产区/墓地：供"消灭目标附属或资产"（JC107）等以资产/墓地为目标的卡选到目标。
            var candidates = new List<CardInstance>();
            candidates.AddRange(cm.GetAreaCards() ?? new List<CardInstance>());
            candidates.AddRange(zm.GetZoneCards(CardZone.AttachedZone, 0));
            candidates.AddRange(zm.GetZoneCards(CardZone.CouncilZone, 0));
            candidates.AddRange(zm.GetZoneCards(CardZone.AssetZone, 1));
            candidates.AddRange(zm.GetZoneCards(CardZone.AssetZone, 2));
            candidates.AddRange(zm.GetZoneCards(CardZone.Graveyard, 1));
            candidates.AddRange(zm.GetZoneCards(CardZone.Graveyard, 2));

            // ---------- 1) 打出的合法卡目标（事务/附属/角色的"卡目标"）----------
            // 【K27】"墓地行动"（`PlayableFromGraveyard`）：墓地中声明该标志的卡**同为可打出源**
            // （"就如同其在你的手上一样"）→ 也为其枚举合法卡目标，供客户端拖拽/选择。
            var playSources = new List<CardInstance>();
            playSources.AddRange(zm.GetZoneCards(CardZone.Hand, viewerPlayerId));
            foreach (var gy in zm.GetZoneCards(CardZone.Graveyard, viewerPlayerId))
                if (gy?.PlayerTemplate != null && gy.PlayerTemplate.PlayableFromGraveyard) playSources.Add(gy);

            foreach (var hand in playSources)
            {
                if (hand?.PlayerTemplate == null) continue;
                var entry = new PlayTargetSnap { SourceUid = hand.UID };
                foreach (var t in candidates)
                {
                    if (t == null || t == hand) continue;
                    if (t.IsDying) continue;   // 【触发语义】正在死亡的卡不再是合法目标（与 GetLocalZoneTargets 同判据）
                    if (cm.IsLegalPlayTarget(hand, t)) entry.TargetUids.Add(t.UID);
                }
                view.PlayTargets.Add(entry);
            }

            // ---------- 2) 可发动 + 3) 激活类效果的合法目标 ----------
            foreach (var card in EnumerateViewerFieldCards(cm, zm, viewerPlayerId))
            {
                // 可发动：与客户端高亮/拖拽起手同一门禁；ignoreAtomicLock 见方法注释
                // （快照可能在服务端原子段内生成，照实返回 false 会让客户端整块失去高亮；
                //   时序门禁由服务端在 ActivateAbility 意图入口强制）。
                bool activatable = ActivateEligibility.CanActivate(card, tm, pm, cm, ignoreAtomicLock: true);
                if (activatable) view.ActivatableUids.Add(card.UID);

                // 【2026-09 修复·JC019 宿主无法拖拽】"能否用拖拽表达"由服务端算一次并投影：
                // 客户端拿不到"运行期授予"表（服务端 EffectEngine 才有），
                // 故必须把结论（含 EffectId）下发，否则客户端的 `GetAllActivateAbilities` 只有模板项
                // ⇒ JC019 授予宿主的能力在客户端**不可见**（拖拽起手直接 return false）。
                bool canDrag = ActivateEligibility.CanDragActivate(card, out var dragAbility);
                foreach (var e in EnumerateActivationEffects(card))
                {
                    if (e == null) continue;
                    // 【2026-09 修复·JC053 用户口径】**只投影"当前确实可发动"的能力**：声明的结算条件
                    // （`EffectCondition.Predicate`）当前不满足的能力**不下发** ⇒ 客户端据"投影有该卡条目、
                    // 但缺本能力"把它**置灰**（A103），实现用户要求"一项可选、一项置灰"。
                    // （服务端有引擎实例，可在此真实求值；客户端没有，故结论必须由投影给出。）
                    if (!ActivateEligibility.ConditionAllowsActivation(e, card))
                    {
                        Godot.GD.Print($"[LegalityProjection] 不投影能力（前置不满足）：{card.PlayerTemplate?.CardName} {e.EffectId}");
                        continue;
                    }
                    view.ActivationAbilities.Add(new ActivationAbilitySnap
                    {
                        SourceUid = card.UID,
                        EffectId = e.EffectId,
                        Draggable = canDrag && ReferenceEquals(dragAbility, e),
                        Label = string.IsNullOrEmpty(e.FaqNote) ? e.EffectId : e.FaqNote,
                        NeedsSelection = e.Target?.NeedsSelection == true,
                        TargetKindValue = (int)(e.Target?.Kind ?? TargetKind.None),
                        PlayerTarget = PlayerTargetPool.IsPlayerSpec(e.Target),
                    });
                    if (e.Target == null || !e.Target.NeedsSelection) continue;
                    var entry = new AbilityTargetSnap { SourceUid = card.UID, EffectId = e.EffectId };
                    foreach (var t in candidates)
                    {
                        // 【2026-10 用户口径·JC108】声明"目标含自身"（`Target.CanTargetSelf`）的能力，
                        // **来源卡本身也是合法目标** ⇒ 投影必须一并下发，否则客户端拿不到"自己"这个候选
                        // （`IsLegalActivateTarget` 本就按 CanTargetSelf 放行，此处跳过自身会与之分叉）。
                        if (t == null) continue;
                        if (t == card && e.Target?.CanTargetSelf != true) continue;
                        if (ActivateEligibility.IsLegalActivateTarget(card, e, t, cm, pm))
                            entry.TargetUids.Add(t.UID);
                    }
                    if (entry.TargetUids.Count > 0) view.AbilityTargets.Add(entry);
                }
            }

            return view;
        }

        /// <summary>查看者控制的场上卡（争夺区 + 附属区 + 秘社），与客户端高亮遍历范围一致。</summary>
        private static IEnumerable<CardInstance> EnumerateViewerFieldCards(CardManager cm, ZoneManager zm, int viewerPlayerId)
        {
            foreach (var c in cm.GetAreaCards() ?? new List<CardInstance>())
                if (c != null && c.ControllerId == viewerPlayerId) yield return c;
            foreach (var c in zm.GetZoneCards(CardZone.AttachedZone, 0))
                if (c != null && c.ControllerId == viewerPlayerId) yield return c;
            foreach (var c in zm.GetZoneCards(CardZone.CouncilZone, 0))
                if (c != null && c.ControllerId == viewerPlayerId) yield return c;
        }

        /// <summary>一张卡的激活类效果（角色/附属 ActivationOptions 与秘社 ActivationOptions 统一枚举）。
        /// 【2026-09 修复·JC019】玩家卡侧改用 `ActivateEligibility.GetAllActivateAbilities`
        /// （**模板 ∪ 运行期授予**）——此前只读模板 `EffectDataList` ⇒ JC019 授予宿主的激活能力
        /// **完全不在投影里**（实测：宿主有 1 条授予能力，投影 `AbilityTargets` 为 0 条）。</summary>
        private static IEnumerable<EffectData> EnumerateActivationEffects(CardInstance card)
        {
            if (card == null) yield break;
            if (card.PlayerTemplate is PlayerCardData)
            {
                foreach (var e in ActivateEligibility.GetAllActivateAbilities(card))
                    if (e != null) yield return e;
                yield break;
            }
            if (card.CouncilTemplate is CouncilCardData council && council.ActivationOptions != null)
            {
                foreach (var opt in council.ActivationOptions)
                {
                    if (opt?.EffectDataList == null) continue;
                    foreach (var e in opt.EffectDataList)
                        if (e != null && e.Timing == EffectTiming.CouncilActivated) yield return e;
                }
            }
        }
    }
}
