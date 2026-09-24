using System.Collections.Generic;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【K14·目标种类扩展（墓地 / 手牌 / 资产）】候选池与合法性判定的**单一实现**（用户 2026-09 口径）。
    ///
    /// 规则口径（已确认）：
    ///  · **允许指定对手的手牌 / 墓地 / 资产**（卡面用"你墓地"/"对手墓地"等限定 → 由 `TargetSpec.Restrictions` 的
    ///    `Friendly`/`Enemy` 表达，本类据 `sourceController` 求值）；
    ///  · **结算时若目标的位置版本号变动则丢失目标** —— 复用既有 D16 版本号逻辑
    ///    （`ZoneManager.FireMoved` 对**除"争夺区→争夺区"外的一切区域切换**都 `BumpVersion`，
    ///     含"进入/离开资产区"；`TurnManager.IsTargetLost` 比对入栈版本号），本类不新增任何丢失判据。
    ///
    /// 覆盖面：`TargetSpec.Kind` 为 `Graveyard` / `Hand` / `Asset` 的目标（这三类是"区域牌堆目标"，
    /// 不属于争夺区，因此候选池与选择窗口都不走"场上点选"，而走**选卡窗**，见 `TurnManager.EnsureTargetThenPush`）。
    ///
    /// 候选合法性 = ① 大类（按实例所在区域映射）② `TargetSpec.Filter` 文本筛选
    /// （语法复用 `DeckSearchFilter`：`类别:` / `子类别:` / `费用<=N` / `关键字:` / `组织:` / `卡名:`，未知字段 fail-closed）
    /// ③ `TargetSpec.Restrictions`（`Friendly`/`Enemy`/`StaticSubCategory`… 与打出处共用 `TargetValidity.CheckRestrictions`）。
    /// </summary>
    public static class TargetZonePool
    {
        /// <summary>该目标大类是否属于"非争夺区目标"（墓地 / 手牌 / 资产 / 附属 / 秘社 / 封印）——决定候选池与选择窗口的分支。
        /// 【2026-09 用户口径】候选池**允许扩大**到 附属区 + 秘社群（此前取目标候选只扫争夺区，
        /// 导致"横置目标角色**或附属**"这类卡（JC013 等）永远取不到附属目标）。
        /// 这些区域里的卡都没有场上 CardUI（附属/秘社虽在场上但通过宿主/秘社容器渲染，无法用"点场上的卡"统一命中），
        /// 故统一走**选卡窗**（单机 `PromptPickCard`／联机 `Kind.SelectZoneCard`）。</summary>
        public static bool IsZonePileKind(TargetKind kind)
            => kind == TargetKind.Graveyard || kind == TargetKind.Hand || kind == TargetKind.Asset
            || kind == TargetKind.Attachment || kind == TargetKind.Council || kind == TargetKind.Seal;

        /// <summary>该 TargetSpec 是否按"非争夺区目标"处理（`Kind` 或 `KindAlt` 任一命中即可；
        /// 【P3·复合目标限制】`SpecAlt` 那条命中也算）。</summary>
        public static bool IsZonePileSpec(TargetSpec spec)
            => spec != null && (IsZonePileKind(spec.Kind) || IsZonePileKind(spec.KindAlt)
                                || (spec.SpecAlt != null && IsZonePileSpec(spec.SpecAlt)));

        private static CardZone? ZoneOf(TargetKind kind)
            => kind == TargetKind.Graveyard ? CardZone.Graveyard
             : kind == TargetKind.Hand ? CardZone.Hand
             : kind == TargetKind.Asset ? CardZone.AssetZone
             : kind == TargetKind.Attachment ? CardZone.AttachedZone
             : kind == TargetKind.Council ? CardZone.CouncilZone
             : kind == TargetKind.Seal ? CardZone.SealedZone
             : (CardZone?)null;   // null = "本池不负责（争夺区/地区/玩家等）"

        /// <summary>该目标大类在提示文案里的说法（供选择窗口）。</summary>
        public static string DescribeZone(TargetKind kind)
            => kind == TargetKind.Graveyard ? "墓地"
             : kind == TargetKind.Hand ? "手牌"
             : kind == TargetKind.Asset ? "资产区"
             : kind == TargetKind.Attachment ? "附属区"
             : kind == TargetKind.Council ? "秘社"
             : kind == TargetKind.Seal ? "封印区"
             : "区域";

        /// <summary>
        /// 按 TargetSpec 枚举候选实例：只枚举**非争夺区**（墓地/手牌/资产/附属/秘社/封印；争夺区由既有候选池负责）。
        /// `KindAlt` 非 None 时两个大类的候选合并（OR 语义）。
        /// `localAreaUid` 非空且 `spec.IsLocalZone` 时，只保留 `ZoneDetailId == localAreaUid` 的候选（"本地区"语义；
        /// 秘社无地区归属，会被排除）。
        /// 顺序：先 `Kind` 的各玩家桶（P1→P2），再 `KindAlt` 的桶——确定性顺序，便于双端同解与测试断言。
        /// </summary>
        public static List<CardInstance> Collect(TargetSpec spec, int sourceController, ZoneManager zm,
            PlayerManager pm = null, string localAreaUid = null)
        {
            var result = new List<CardInstance>();
            if (spec == null || zm == null || !IsZonePileSpec(spec)) return result;
            CollectOne(spec, sourceController, zm, pm, localAreaUid, result);
            // 【P3·复合目标限制】别名那条（OR）：并集去重，本体候选在前（确定性顺序，便于双端同解与测试断言）。
            if (spec.SpecAlt != null && IsZonePileSpec(spec.SpecAlt))
                CollectOne(spec.SpecAlt, sourceController, zm, pm, localAreaUid, result);
            return result;
        }

        private static void CollectOne(TargetSpec spec, int sourceController, ZoneManager zm,
            PlayerManager pm, string localAreaUid, List<CardInstance> result)
        {
            var kinds = new List<TargetKind> { spec.Kind };
            if (spec.KindAlt != TargetKind.None && spec.KindAlt != spec.Kind) kinds.Add(spec.KindAlt);

            foreach (var kind in kinds)
            {
                var zone = ZoneOf(kind);
                if (zone == null) continue;   // 该大类由别的候选池负责
                for (int p = 1; p <= 2; p++)
                {
                    foreach (var c in zm.GetZoneCards(zone.Value, p))
                    {
                        if (c == null || c.IsHidden) continue;   // 隐藏占位（对手手牌）不可作为目标
                        if (result.Contains(c)) continue;        // 两个大类可能命中同一张（如附属同时也是资产？防御性去重）
                        if (spec.IsLocalZone && !string.IsNullOrEmpty(localAreaUid)
                            && !string.Equals(c.ZoneDetailId ?? "", localAreaUid, System.StringComparison.Ordinal)) continue;
                        if (!MatchesOne(spec, c, sourceController, pm)) continue;
                        result.Add(c);
                    }
                }
            }
        }

        /// <summary>单个候选是否合法（大类（含 `KindAlt` OR）+ 文本筛选 + Restrictions；【P3】`SpecAlt` 整条 OR）。</summary>
        public static bool Matches(TargetSpec spec, CardInstance c, int sourceController, PlayerManager pm = null)
            => MatchesOne(spec, c, sourceController, pm)
               || (spec?.SpecAlt != null && MatchesOne(spec.SpecAlt, c, sourceController, pm));

        private static bool MatchesOne(TargetSpec spec, CardInstance c, int sourceController, PlayerManager pm = null)
        {
            if (spec == null || c == null) return false;
            // ① 大类：按实例"当前所在区域"映射（墓地/手牌/资产/附属/秘社/封印优先，见 CardManager.MapInstanceToTargetKind）
            //    【K14】`KindAlt` 非 None 时与 `Kind` 构成 OR（"目标附属或资产"）
            var kind = CardManager.MapInstanceToTargetKind(c);
            bool kindOk = kind == spec.Kind
                || (spec.KindAlt != TargetKind.None && kind == spec.KindAlt)
                || (spec.IncludeCovert && spec.Kind == TargetKind.Character && kind == TargetKind.Covert);
            if (!kindOk) return false;
            // ② 文本筛选（复用检索筛选语法；空 = 不限）
            if (!string.IsNullOrWhiteSpace(spec.Filter) && !DeckSearchFilter.Matches(spec.Filter, c)) return false;
            // ③ 精筛（Friendly/Enemy/子类别/费用…）——与打出/激活阶段共用同一实现
            return TargetValidity.CheckRestrictions(spec.Restrictions, spec.Param, c, sourceController, pm);
        }

        /// <summary>把候选实例转成 UID 列表（丢给既有选择窗口/决策请求用）。</summary>
        public static List<string> CollectUids(TargetSpec spec, int sourceController, ZoneManager zm,
            PlayerManager pm = null, string localAreaUid = null)
            => Collect(spec, sourceController, zm, pm, localAreaUid).ConvertAll(c => c.UID);
    }
}
