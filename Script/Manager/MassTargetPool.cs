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

            string area = !string.IsNullOrEmpty(areaUid) ? areaUid : (source.ZoneDetailId ?? "");
            if (string.IsNullOrEmpty(area)) return result;

            int srcCtrl = source.ControllerId;
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
            var kind = CardManager.MapInstanceToTargetKind(c);
            bool kindOk = kind == spec.Kind || (spec.KindAlt != TargetKind.None && kind == spec.KindAlt)
                || (spec.IncludeCovert && spec.Kind == TargetKind.Character && kind == TargetKind.Covert);
            if (!kindOk) return false;
            if (!string.IsNullOrWhiteSpace(spec.Filter) && !DeckSearchFilter.Matches(spec.Filter, c)) return false;
            return TargetValidity.CheckRestrictions(spec.Restrictions, spec.Param, c, sourceController, pm);
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
