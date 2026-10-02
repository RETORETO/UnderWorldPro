using System.Collections.Generic;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【K2·群体/批量动作】"对某地区内符合条件的所有卡生效"的**候选集合单一实现**（用户 2026-09 口径）。
    ///
    /// 语义（本轮确认）：
    ///  · `TargetSpec.MassMode = MassMode.AllInArea` 时本效果**不取单个目标**，而是作用于一个集合；
    ///  · **地区** = 本次结算的"目标地区"（`targetAreaId`：打出时选的地区 / 触发事件所在地，如进场地区、死亡地区…）；
    ///    为空时回落到**来源卡所在地区**（"本地区"类效果，如 JC052/JC011/JC056）；
    ///  · **集合** = 该地区内满足 ① `Kind`（`Character`/`Covert`/`Attachment`…，`IncludeCovert=true` 时角色类同时接受暗藏者）
    ///    ② `Filter` 文本筛选（语法复用 `DeckSearchFilter`，未知字段 fail-closed）③ `Restrictions`（`Friendly`/`Enemy`…）
    ///    的卡；**排除正在死亡（IsDying）的卡**；除非 `CanTargetSelf=true`，否则排除来源卡自身；
    ///  · **执行方式**：同一动作序列对集合内每张卡各执行一次，**在一次结算内全部完成**。
    ///
    /// ⚠️ **不实现"批量期间挂起触发"**（用户明确）：群体效果一般包在**一条效果/一段原子锁操作**内，
    /// 原子锁（`TurnManager.EnterAtomic/IsAtomicLocked`）在堆叠结算期间成立，因此期间不会有其它玩家行动；
    /// 各目标死亡产生的死亡触发按既有触发管线收集，在本次结算走完后由触发窗口/堆叠处理（多条由玩家排序）。
    /// </summary>
    public static class MassTargetPool
    {
        /// <summary>本效果是否按"群体动作"处理。</summary>
        public static bool IsMass(EffectData data) => data?.Target != null && data.Target.MassMode == MassMode.AllInArea;

        /// <summary>
        /// 取群体集合（确定性顺序 = 该地区区域列表顺序，便于双端同解与测试断言）。
        /// `areaUid` 为空时用 `source.ZoneDetailId`（来源所在地区）。
        /// </summary>
        public static List<CardInstance> Collect(EffectData data, CardInstance source, string areaUid,
            ZoneManager zm, CardManager cm = null, PlayerManager pm = null)
        {
            var result = new List<CardInstance>();
            var spec = data?.Target;
            if (spec == null || spec.MassMode != MassMode.AllInArea || zm == null || source == null) return result;

           
            int srcCtrl = source.ControllerId;

            // 【秘社批次·MSJC013】`TargetSpec.AllAreas` ⇒ **跨全部争夺区**取集合（卡文"消灭所有其上有…的
            // 角色和附属"不限定地区）。注意：`GetZoneCards(CardZone.AreaZone, 0, area)` 传空 area 才是"全场"，
            // 故这里显式分支，不要沿用"空 area 回落来源地区"的旧语义（那会漏掉别的地区）。
            if (spec.AllAreas)
            {
                foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, null))
                {
                    if (c == null || c.IsDying) continue;
                    if (c == source && !spec.CanTargetSelf) continue;
                    if (!Matches(spec, c, srcCtrl, pm)) continue;
                    result.Add(c);
                }
                return result;
            }

            string area = !string.IsNullOrEmpty(areaUid) ? areaUid : (source.ZoneDetailId ?? "");
            if (string.IsNullOrEmpty(area)) return result;

            foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, area))
            {
                if (c == null || c.IsDying) continue;
                if (c == source && !spec.CanTargetSelf) continue;      // 卡文"其他角色" → CanTargetSelf=false
                if (!Matches(spec, c, srcCtrl, pm)) continue;
                result.Add(c);
            }
            return result;
        }

        /// <summary>集合成员是否合法：大类（含暗藏者放宽、`KindAlt` OR、【P3】`SpecAlt` 整条 OR）+ 文本筛选 + 精筛。</summary>
        public static bool Matches(TargetSpec spec, CardInstance c, int sourceController, PlayerManager pm = null)
            => MatchesOne(spec, c, sourceController, pm)
               || (spec?.SpecAlt != null && MatchesOne(spec.SpecAlt, c, sourceController, pm));

        private static bool MatchesOne(TargetSpec spec, CardInstance c, int sourceController, PlayerManager pm = null)
        {
            if (spec == null || c == null) return false;
            // 【秘社批次】`MinCounters`（"其上有 N 个或更多某标志"）：在任何大类判定之前先卡这一条。
            if (!MeetsMinCounters(spec.MinCounters, c)) return false;
            var kind = CardManager.MapInstanceToTargetKind(c);
            // `Kind = None` ⇒ 不限大类（卡文只按"其上有标志"筛，不区分角色/附属，如 MSJC013②）。
            bool kindOk = spec.Kind == TargetKind.None
                || kind == spec.Kind || (spec.KindAlt != TargetKind.None && kind == spec.KindAlt)
                || (spec.IncludeCovert && spec.Kind == TargetKind.Character && kind == TargetKind.Covert);
            if (!kindOk) return false;
            if (!string.IsNullOrWhiteSpace(spec.Filter) && !DeckSearchFilter.Matches(spec.Filter, c)) return false;
            return TargetValidity.CheckRestrictions(spec.Restrictions, spec.Param, c, sourceController, pm);
        }

        /// <summary>
        /// 【秘社批次】解析并判定 `TargetSpec.MinCounters`（写法 `&lt;标志名&gt;:&lt;N&gt;`，如 `"时间标志:3"`）。
        /// 空串 ⇒ 恒 true（不限）。写法非法 ⇒ **fail-closed 返回 false 并打日志**（防拼错静默放行整场清场）。
        /// </summary>
        public static bool MeetsMinCounters(string spec, CardInstance c)
        {
            if (string.IsNullOrWhiteSpace(spec)) return true;
            if (c == null) return false;
            int colon = spec.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(spec.Substring(colon + 1).Trim(), out int need) || need <= 0)
            {
                Godot.GD.Print($"[MassTargetPool] ⚠ MinCounters 写法非法「{spec}」（应为『标志名:N』）→ 视为不通过（fail-closed）");
                return false;
            }
            string marker = spec.Substring(0, colon).Trim();
            int have = 0;
            if (c.Counters != null)
            {
                c.Counters.TryGetValue(marker, out have);
                // 卡库对同一概念有"锁定标记"/"锁定标志"两种写法（见 `TargetRestriction.HasCounter` 同款口径）——
                // 本门槛同样两种都认，避免同一个标志因叫法不同而漏判。
                if (have == 0 && marker == "锁定标志") c.Counters.TryGetValue("锁定标记", out have);
                else if (have == 0 && marker == "锁定标记") c.Counters.TryGetValue("锁定标志", out have);
            }
            bool ok = have >= need;
            Godot.GD.Print($"[MassTargetPool] 卡上标志门槛 {c.PlayerTemplate?.CardName ?? c.UID}：{marker}={have} vs 需≥{need} → {(ok ? "通过" : "不通过")}");
            return ok;
        }

        /// <summary>人类可读的集合描述（日志用）。</summary>
        public static string Describe(List<CardInstance> cards)
        {
            if (cards == null || cards.Count == 0) return "（空）";
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
