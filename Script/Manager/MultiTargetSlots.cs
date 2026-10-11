using System.Collections.Generic;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【K15·多目标】一个槽位的选择结果（玩家在取目标阶段为某个 `TargetSlot` 选中的对象）。
    /// 地区槽存**地区 UID**，卡槽存**卡 UID**（两套命名空间不冲突，用 <see cref="IsArea"/> 区分）。
    /// </summary>
    public struct TargetChoice
    {
        /// <summary>true＝该选择是"目标地区"（<see cref="Uid"/> 是地区 UID）。</summary>
        public bool IsArea;
        /// <summary>
        /// 【K24】true＝该选择是"目标玩家"（<see cref="PlayerId"/> 是玩家号；<see cref="Uid"/> 为 `"P{id}"` 便于入栈对齐）。
        /// </summary>
        public bool IsPlayer;
        /// <summary>地区 UID / 卡 UID / 玩家号文本（`P{id}`）；空串＝该槽留空（未选）。</summary>
        public string Uid;
        /// <summary>【K24】玩家号（`IsPlayer` 时有效；否则 -1）。</summary>
        public int PlayerId;

        public TargetChoice(bool isArea, string uid) : this(isArea, false, uid, -1) { }

        public TargetChoice(bool isArea, bool isPlayer, string uid, int playerId)
        {
            IsArea = isArea;
            IsPlayer = isPlayer;
            Uid = uid ?? "";
            PlayerId = playerId;
        }

        /// <summary>空选择（该槽留空）。</summary>
        public static TargetChoice Empty => new TargetChoice(false, "");
        public static TargetChoice Area(string areaUid) => new TargetChoice(true, areaUid);
        public static TargetChoice Card(string cardUid) => new TargetChoice(false, cardUid);

        /// <summary>【K24】玩家选择：`Uid` 固定写成 `P{playerId}`，使入栈负载"按槽对齐"时与其他槽同构。</summary>
        public static TargetChoice Player(int playerId) => new TargetChoice(false, true, PlayerUid(playerId), playerId);

        /// <summary>玩家号的文本形式（入栈负载/联机载荷用）。</summary>
        public static string PlayerUid(int playerId) => "P" + playerId;

        /// <summary>从文本形式解析玩家号（非法 → -1）。</summary>
        public static int ParsePlayerUid(string uid)
        {
            if (string.IsNullOrEmpty(uid) || uid.Length < 2 || (uid[0] != 'P' && uid[0] != 'p')) return -1;
            return int.TryParse(uid.Substring(1), out int id) ? id : -1;
        }

        public bool IsEmpty => string.IsNullOrEmpty(Uid);
    }

    /// <summary>某个槽在"当前已选"下的候选（地区槽给地区 UID，卡槽给卡实例，玩家槽给玩家号）。</summary>
    public sealed class SlotCandidates
    {
        public bool IsArea;
        /// <summary>【K24】该槽是"目标玩家"槽（候选见 <see cref="PlayerIds"/>）。</summary>
        public bool IsPlayer;
        public string Label = "";
        /// <summary>【2026-10·JC092】该槽的**动作提示**（`TargetSlot.Hint`；空＝UI 用中性兜底文案）。</summary>
        public string Hint = "";
        public bool Optional = true;
        public List<string> AreaUids = new List<string>();
        public List<CardInstance> Cards = new List<CardInstance>();
        /// <summary>【K24】候选玩家号（升序）。</summary>
        public List<int> PlayerIds = new List<int>();

        /// <summary>
        /// 【2026-09 用户口径·A86】该槽的候选**在场上看得见**吗（决定取目标的交互形态）：
        ///  · 地区槽 → 看得见（地区槽位有高亮通道）；
        ///  · 卡槽 → 非"区域牌堆"候选（争夺区角色/暗藏者等），或声明了 `IsFieldVisibleSpec`
        ///    （附属/封印/秘社等**有场上卡 UI** 的区）。
        /// 消费点＝`ShouldPickOnField`（单机 `Arena.PromptMultiTargets` / 联机 `ArenaOl`）。
        /// </summary>
        public bool FieldVisible;

        public int Count => IsArea ? AreaUids.Count : (IsPlayer ? PlayerIds.Count : Cards.Count);
        public bool IsEmpty => Count == 0;
    }

    /// <summary>
    /// 【K15·多目标槽位】"逐槽取目标"的**候选枚举 + 合法性 + 槽间依赖**单一实现（用户 2026-09 口径）。
    ///
    /// 口径（已与用户确认）：
    ///  · 卡文"选择目标本方角色**和/或**目标敌方角色"＝**两个可留空的槽**（本方角色 / 敌方角色），
    ///    **各自可留空**，但整条效果**至少要有 `EffectData.MinChosenSlots` 个槽被填**（JC021＝1）；
    ///  · **目标地区由玩家选择**；FAQ 禁止原地移动 → **已经在该目的地的角色不出现在候选里**
    ///    （由 `TargetSlot.ExcludeInChosenArea` 表达，取目标顺序上地区槽先于这些卡槽）；
    ///  · 全部槽选完后进入**确认面板**，确认通过才把效果入堆叠（单机 `IGameUIHooks.PromptMultiTargets`／联机 `ChoiceRequest.Kind.ConfirmTargets`）。
    ///
    /// 本类只做"候选与判定"，不落地任何选择窗口、不改对局状态（View/Controller 各司其职）。
    /// </summary>
    public static class MultiTargetSlots
    {
        /// <summary>该效果是否走"多目标槽位"模式。</summary>
        public static bool IsMultiTarget(EffectData data)
            => data?.TargetSlots != null && data.TargetSlots.Count > 0;

        /// <summary>取第 i 个槽（越界返回 null）。</summary>
        public static TargetSlot SlotAt(EffectData data, int index)
        {
            if (!IsMultiTarget(data) || index < 0 || index >= data.TargetSlots.Count) return null;
            return data.TargetSlots[index];
        }

        /// <summary>该槽是否是"目标地区"槽（`Spec.Kind == Area`）。</summary>
        public static bool IsAreaSlot(TargetSlot slot) => slot?.Spec != null && slot.Spec.Kind == TargetKind.Area;

        /// <summary>【K24】该槽是否是"目标玩家"槽（`Spec.Kind == Player`）。</summary>
        public static bool IsPlayerSlot(TargetSlot slot) => PlayerTargetPool.IsPlayerSpec(slot?.Spec);

        // ==================== 候选枚举 ====================

        /// <summary>按槽取候选（地区槽 / 卡槽统一入口；供选择窗口逐槽刷新）。</summary>
        public static SlotCandidates CollectSlot(EffectData data, int slotIndex, int sourceController,
            IList<TargetChoice> chosen, ZoneManager zm, CardManager cm = null, PlayerManager pm = null,
            CardInstance source = null, AreaManager areaManager = null)
        {
            var slot = SlotAt(data, slotIndex);
            var result = new SlotCandidates
            {
                IsArea = IsAreaSlot(slot),
                IsPlayer = IsPlayerSlot(slot),
                Label = SlotLabel(data, slotIndex),
                Hint = slot?.Hint ?? "",
                Optional = slot?.Optional ?? true,
            };
            if (slot?.Spec == null) return result;
            if (result.IsArea)
            {
                result.AreaUids = CollectAreaUids(areaManager);
                result.FieldVisible = true;   // 地区槽位有"高亮 + 点击"通道
                return result;
            }
            if (result.IsPlayer)
            {
                // 【K24】玩家槽：候选＝满足"任意/本方/对手"限制的玩家（已在本效果其它槽选中的玩家不再出现）。
                foreach (int pid in PlayerTargetPool.Collect(slot.Spec, sourceController, pm))
                    if (!ChosenContainsPlayer(chosen, pid, slotIndex)) result.PlayerIds.Add(pid);
                return result;   // 玩家不是场上节点（无卡 UI）→ FieldVisible 保持 false（走按钮窗）
            }
            result.FieldVisible = !TargetZonePool.IsZonePileSpec(slot.Spec)
                                  || TargetZonePool.IsFieldVisibleSpec(slot.Spec);
            result.Cards = CollectCards(data, slotIndex, sourceController, chosen, zm, cm, pm, source, areaManager);
            return result;
        }

        /// <summary>
        /// 【2026-09 用户口径·A86】该槽是否应当走"**场上高亮 + 点击**"取目标，而**不是**弹出按钮列表窗：
        ///  · 地区槽 / 场上看得见的卡槽（`SlotCandidates.FieldVisible`）→ true；
        ///  · **候选为空时一律 false**——必须保留按钮列表窗的"留空 / 取消（不打出）"出口，
        ///    否则玩家会卡在一个点不动的提示窗上（无候选 ⇒ 无选择 ⇒ 必须能退出）。
        ///  · 玩家槽恒 false（玩家不是场上卡节点，只能列成按钮）。
        /// 用户实测口径（JC062）："选择查看的暗藏者应该选择**场上的高亮暗藏者**，而不是弹出选择框"。
        /// </summary>
        public static bool ShouldPickOnField(SlotCandidates cand)
        {
            if (cand == null || cand.IsPlayer) return false;
            if (cand.Count == 0) return false;
            return cand.IsArea ? cand.AreaUids.Count > 0 : cand.FieldVisible;
        }

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021】多目标槽位的**取目标窗形状**：
        ///  · `FieldMultiPick` ＝"**一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消**"：
        ///    所有槽都是"地区槽"或"**场上看得见的卡槽**"（争夺区角色/暗藏者…），且**至少一个卡槽**；
        ///    · JC062：两个"目标暗藏者"卡槽（可留空、至少 0 个）⇒ 只选卡、至多 2 张；
        ///    · JC021：一个"目标地区"槽（必选）＋"本方角色"/"敌方角色"卡槽（各自可留空、至少 1 个角色）
        ///      ⇒ 选卡（**每个玩家场上至多 1 个**）＋选地区，且"没有地区或没有角色时不能确认"；
        ///  · `PerSlot` ＝ 其余形状（含**区域牌堆**槽：墓地/手牌/牌库…这些没有场上 UI）→ 既有逐槽流程。
        /// </summary>
        public enum MultiSlotShape
        {
            /// <summary>既有逐槽流程（逐槽高亮/列表窗 → 确认面板）。</summary>
            PerSlot,
            /// <summary>一个窗多选（场上高亮 + 左键选择/右键取消 + 确认/取消）。</summary>
            FieldMultiPick,
        }

        /// <summary>见 <see cref="MultiSlotShape"/>：判定该效果该用哪种取目标窗（单一实现，View 只调用不重算）。</summary>
        public static MultiSlotShape ClassifyShape(EffectData data)
        {
            var slots = data?.TargetSlots;
            if (slots == null || slots.Count == 0) return MultiSlotShape.PerSlot;
            int areaSlots = 0, cardSlots = 0;
            foreach (var s in slots)
            {
                var spec = s?.Spec;
                if (spec == null || spec.Kind == TargetKind.None) return MultiSlotShape.PerSlot;
                if (IsPlayerSlot(s)) return MultiSlotShape.PerSlot;
                if (IsAreaSlot(s)) { areaSlots++; continue; }
                // 卡槽：必须"在场上看得见"（区域牌堆如墓地/手牌/牌库只能列按钮）
                if (TargetZonePool.IsZonePileSpec(spec) && !TargetZonePool.IsFieldVisibleSpec(spec))
                    return MultiSlotShape.PerSlot;
                cardSlots++;
            }
            if (cardSlots >= 1 && areaSlots <= 1) return MultiSlotShape.FieldMultiPick;
            return MultiSlotShape.PerSlot;
        }

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021】**"一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消"取目标的计划**：
        /// 形状判定（`ClassifyShape == FieldMultiPick`）后，由本类统一给出分组文案、上限/下限，
        /// 以及"**已选卡＋已选地区 → 逐槽 `TargetChoice`**"的映射与"哪些还能点"的重算。
        ///
        /// **单机 UI（`Arena.TryPromptFieldMultiPick`）与服务端联机（`TurnManager` 聚合请求）共用本实现**，
        /// 保证两端"每个玩家场上的角色只能选一个""地区必选""不能原地移动"等口径完全一致（单一真相源）。
        /// 规则要点：
        ///  · 每张被选中的卡归入**第一个"空着且接受它"的卡槽**（JC021 的"本方角色/敌方角色"两槽都带本方/敌方限制，
        ///    因此天然"每个玩家场上的角色至多 1 个"；JC062 两个同大类槽则等价于"至多 2 张、不可重复"）；
        ///  · **禁止原地移动**：已经选了地区之后，位于该地区的角色不再是候选（`ExcludeInChosenArea`），
        ///    已选中的会被自动剔除。
        /// </summary>
        public sealed class FieldPickPlan
        {
            public EffectData Effect;
            public CardInstance Source;
            public int SourceController;
            public ZoneManager ZoneManager;
            public CardManager CardManager;
            public PlayerManager PlayerManager;
            public AreaManager AreaManager;

            /// <summary>卡槽下标（有序；顺序即"归槽"的优先顺序）。</summary>
            public readonly List<int> CardSlotIndexes = new List<int>();
            /// <summary>地区槽下标（-1＝无地区槽）。</summary>
            public int AreaSlotIndex = -1;

            /// <summary>卡槽组标题（JC062＝"目标暗藏者"；JC021＝"目标角色"）。</summary>
            public string CardLabel = "目标角色";
            /// <summary>地区槽组标题（如"目标地区"）；无地区槽时为空。</summary>
            public string AreaLabel;
            /// <summary>至多可选几张卡（＝卡槽数）。</summary>
            public int MaxCards => CardSlotIndexes.Count;
            /// <summary>至多可选几个地区（0/1）。</summary>
            public int MaxAreas => AreaSlotIndex >= 0 ? 1 : 0;
            /// <summary>至少需要选几张卡（`EffectData.MinChosenSlots`；JC062＝0、JC021＝1）。</summary>
            public int MinCards;
            /// <summary>地区槽是否必选。</summary>
            public bool AreaRequired;
            /// <summary>是否"每个玩家场上的角色只能选择一个"（卡槽自带阵营限制时成立，如 JC021）。</summary>
            public bool SameControllerOnce;

            private FieldPickPlan() { }

            /// <summary>构建计划；返回 null ＝ 该效果不是 `FieldMultiPick` 形状（调用方走既有逐槽流程）。</summary>
            public static FieldPickPlan Build(EffectData data, CardInstance source, int sourceController,
                ZoneManager zm, CardManager cm, PlayerManager pm, AreaManager am)
            {
                if (data?.TargetSlots == null || data.TargetSlots.Count == 0) return null;
                if (ClassifyShape(data) != MultiSlotShape.FieldMultiPick) return null;

                var plan = new FieldPickPlan
                {
                    Effect = data,
                    Source = source,
                    SourceController = sourceController,
                    ZoneManager = zm,
                    CardManager = cm,
                    PlayerManager = pm,
                    AreaManager = am,
                    MinCards = System.Math.Max(0, data.MinChosenSlots),
                };
                for (int i = 0; i < data.TargetSlots.Count; i++)
                {
                    var s = data.TargetSlots[i];
                    if (IsAreaSlot(s))
                    {
                        plan.AreaSlotIndex = i;
                        plan.AreaLabel = CleanSlotLabel(s.Label, "目标地区");
                        plan.AreaRequired = !s.Optional;
                        continue;
                    }
                    plan.CardSlotIndexes.Add(i);
                }
                if (plan.CardSlotIndexes.Count == 0) return null;
                // 卡槽组标题：同大类且**都不带阵营限制**时用首槽标签（JC062"目标暗藏者"）；
                // 带阵营限制（JC021"本方角色/敌方角色"）时用统称"目标角色"（用户窗面样例即如此）。
                bool restricted = false;
                foreach (var si in plan.CardSlotIndexes)
                    if ((data.TargetSlots[si].Spec?.Restrictions?.Count ?? 0) > 0) restricted = true;
                plan.SameControllerOnce = restricted && plan.CardSlotIndexes.Count > 1;
                plan.CardLabel = restricted ? "目标角色" : CleanSlotLabel(data.TargetSlots[plan.CardSlotIndexes[0]].Label, "目标角色");
                return plan;
            }

            /// <summary>槽位标签去掉"（第 N 个，可留空）"这类括注（用户窗面样例写作"目标暗藏者"/"目标地区"）。</summary>
            private static string CleanSlotLabel(string label, string fallback)
            {
                if (string.IsNullOrEmpty(label)) return fallback;
                int idx = label.IndexOf('（');
                string cleaned = idx > 0 ? label.Substring(0, idx) : label;
                return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
            }

            private List<TargetChoice> EmptyChoices()
            {
                var list = new List<TargetChoice>();
                for (int i = 0; i < Effect.TargetSlots.Count; i++) list.Add(TargetChoice.Empty);
                return list;
            }

            /// <summary>
            /// 把"已选卡（有序）＋已选地区"折算成逐槽 `TargetChoice`。
            /// `areaFirst`：是否先落地区（影响卡槽候选的"禁止原地移动"过滤；默认 true＝与逐槽流程一致）。
            /// </summary>
            public List<TargetChoice> MapPicks(List<string> pickedCards, List<string> pickedAreas,
                bool areaFirst = true)
            {
                var choices = EmptyChoices();
                if (AreaSlotIndex >= 0 && pickedAreas != null && pickedAreas.Count > 0)
                    choices[AreaSlotIndex] = TargetChoice.Area(pickedAreas[0]);
                if (pickedCards == null) return choices;
                var filled = new System.Collections.Generic.HashSet<int>();
                foreach (var uid in pickedCards)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    int found = -1;
                    foreach (var si in CardSlotIndexes)
                    {
                        if (filled.Contains(si)) continue;
                        var cand = CollectSlot(Effect, si, SourceController, choices, ZoneManager,
                            CardManager, PlayerManager, Source, AreaManager);
                        if (cand == null) continue;
                        bool accepts = false;
                        foreach (var c in cand.Cards) if (c != null && c.UID == uid) { accepts = true; break; }
                        if (!accepts) continue;
                        found = si;
                        break;
                    }
                    if (found < 0) continue;      // 找不到归宿（地区变化后被剔除等）→ 跳过（服务端仍会 Validate）
                    filled.Add(found);
                    choices[found] = TargetChoice.Card(uid);
                }
                return choices;
            }

            /// <summary>"哪些卡还能被左键点选"：**已选中且仍然合法**的卡 ∪ **尚未填满的卡槽**能接受的卡之并集。
            ///
            /// 【2026-09 修复·用户实测 JC021"多次点选/取消后再点确定，效果没有加入堆叠"】**必须把"已选中且仍然合法"
            /// 的卡也算进候选**：协调器用"不在候选里 ⇒ 已失效 ⇒ 剔除"来清理选择，而"尚未填满的槽能接受的卡"
            /// 天然**不包含**刚被选中的那些卡 ⇒ 旧实现会在每次刷新时把玩家刚选的卡删掉（选择永远攒不起来 ⇒
            /// 确认门槛过不去 ⇒ 效果入不了堆叠）。剔除（如"选了地区后该地区内的角色"）由 `MapPicks` 的
            /// 归槽结果体现：归不进任何槽的卡不会出现在这里的并集里。
            /// </summary>
            public List<string> OpenCardCandidates(List<string> pickedCards, List<string> pickedAreas)
            {
                var res = new List<string>();
                var choices = MapPicks(pickedCards, pickedAreas);
                // ① 已选中且仍然合法（能归入某个槽）的卡 ⇒ 留在候选里（＝"仍然被选中"）
                foreach (var ch in choices)
                    if (!ch.IsEmpty && !ch.IsArea && !ch.IsPlayer && !res.Contains(ch.Uid)) res.Add(ch.Uid);
                // ② 尚未填满的槽还能接受哪些卡
                var filled = new System.Collections.Generic.HashSet<int>();
                for (int i = 0; i < choices.Count; i++) if (!choices[i].IsEmpty) filled.Add(i);
                foreach (var si in CardSlotIndexes)
                {
                    if (filled.Contains(si)) continue;
                    var cand = CollectSlot(Effect, si, SourceController, choices, ZoneManager,
                        CardManager, PlayerManager, Source, AreaManager);
                    if (cand?.Cards == null) continue;
                    foreach (var c in cand.Cards)
                        if (c != null && !res.Contains(c.UID)) res.Add(c.UID);
                }
                return res;
            }

            /// <summary>"哪些地区还能点"：地区槽未填时全部地区候选。</summary>
            public List<string> OpenAreaCandidates(List<string> pickedCards, List<string> pickedAreas)
            {
                var res = new List<string>();
                if (AreaSlotIndex < 0) return res;
                var choices = MapPicks(pickedCards, pickedAreas);
                if (!choices[AreaSlotIndex].IsEmpty) return res;   // 已选地区（上限 1）⇒ 不再重复高亮
                var cand = CollectSlot(Effect, AreaSlotIndex, SourceController, choices, ZoneManager,
                    CardManager, PlayerManager, Source, AreaManager);
                if (cand?.AreaUids != null) res.AddRange(cand.AreaUids);
                return res;
            }

            /// <summary>该卡能否被左键加选（＝"存在一个空着且接受它的卡槽"；JC021 因此每方至多 1 张）。</summary>
            public bool CanAddCard(List<string> pickedCards, List<string> pickedAreas, string uid)
            {
                if (string.IsNullOrEmpty(uid)) return false;
                foreach (var u in pickedCards ?? new List<string>()) if (u == uid) return false;
                return OpenCardCandidates(pickedCards, pickedAreas).Contains(uid);
            }

            /// <summary>"确认"是否可用：选卡数落在 [MinCards, MaxCards] 且（地区必选时）已选地区。</summary>
            public bool CanConfirm(List<string> pickedCards, List<string> pickedAreas)
            {
                int cards = pickedCards?.Count ?? 0;
                int areas = pickedAreas?.Count ?? 0;
                return cards >= MinCards && cards <= MaxCards && areas <= MaxAreas
                       && (!AreaRequired || areas >= 1);
            }

            /// <summary>窗面标题（与用户样例逐字一致：
            /// 有地区槽 → `请选择目标角色（0/2）和目标地区（0/1）`；无地区槽 → `请选择目标暗藏者：已选择（0/2）`）。</summary>
            public string Title(List<string> pickedCards, List<string> pickedAreas)
            {
                int cards = pickedCards?.Count ?? 0;
                int areas = pickedAreas?.Count ?? 0;
                return AreaSlotIndex >= 0
                    ? $"请选择{CardLabel}（{cards}/{MaxCards}）和{AreaLabel}（{areas}/{MaxAreas}）"
                    : $"请选择{CardLabel}：已选择（{cards}/{MaxCards}）";
            }
        }

        /// <summary>
        /// 【K16·模式分支】从卡模板里找出"声明了模式"的效果（打出版本用；`timing` 传 null 表示任意）。
        /// </summary>
        /// <summary>
        /// 【JC082·"选择一项"的**打出**】把卡上的 `TargetRules`（各类别各一项）**合成为一份模式声明**，
        /// 供 `TurnManager.BeginModeCardPlay` / `PlayCard` / 联机 `PlayerActionService.TryBeginModeCardPlayOnline`
        /// / `ArenaOl` 走与 JC063 完全相同的"先选一项 → 再选该项目的目标"流程。
        /// 未声明 `PlayerCardData.PlayChoice` → 返回 null（其余卡路径不变）。
        /// </summary>
        public static EffectData PlayChoiceEffect(Script.Data.Cards.PlayerCardData tmpl)
        {
            if (tmpl?.PlayChoice != true || tmpl.TargetRules == null) return null;
            var effect = new EffectData
            {
                EffectId = (tmpl.UID ?? "") + "_playchoice",
                Timing = EffectTiming.OnSorceryCast,   // 仅作"打出时模式"的占位时机（不入效果结算）
                Duration = "瞬时",
            };
            foreach (var rule in tmpl.TargetRules)
            {
                if (rule == null || rule.Kind == TargetKind.None) continue;
                effect.Modes.Add(new EffectMode
                {
                    Label = DescribePlayChoice(rule),
                    Target = new TargetSpec
                    {
                        Kind = rule.Kind,
                        Restrictions = rule.Restrictions,
                        Param = rule.Param,
                        NeedsSelection = true,
                    },
                });
            }
            return effect.Modes.Count > 0 ? effect : null;
        }

        /// <summary>把一条打出目标规则转成"选择一项"的选项标签（JC082：结附于目标角色／结附于本方秘社）。</summary>
        private static string DescribePlayChoice(TargetRule rule)
        {
            string what = rule.Kind switch
            {
                TargetKind.Character => "目标角色",
                TargetKind.Covert => "目标暗藏者",
                TargetKind.Area => "目标地区",
                TargetKind.Attachment => "目标附属",
                TargetKind.Asset => "目标资产",
                TargetKind.Council => "本方秘社",
                TargetKind.Player => "目标玩家",
                _ => rule.Kind.ToString(),
            };
            return $"结附于{what}";
        }

        public static EffectData FindModeEffect(Script.Data.Cards.PlayerCardData tmpl, EffectTiming? timing = null)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
            {
                if (e?.HasModes != true) continue;
                if (timing == null || e.Timing == timing.Value) return e;
            }
            return null;
        }

        /// <summary>
        /// 从卡模板里找出"声明了多目标槽位"的效果（打出/结算路径据此进入多目标模式）。
        /// 优先取 `Timing` 匹配者（如事务卡取 `OnSorceryCast`）；`timing` 传 null 表示任意。
        /// </summary>
        public static EffectData FindMultiTargetEffect(Script.Data.Cards.PlayerCardData tmpl,
            EffectTiming? timing = null)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
            {
                if (!IsMultiTarget(e)) continue;
                if (timing == null || e.Timing == timing.Value) return e;
            }
            return null;
        }

        /// <summary>地区槽的候选：当前存在的所有地区（确定性顺序＝地区序号）。</summary>
        public static List<string> CollectAreaUids(AreaManager areaManager)
        {
            var result = new List<string>();
            var uids = areaManager?.GetOrderedAreaUids();
            if (uids == null) return result;
            foreach (var u in uids)
                if (!string.IsNullOrEmpty(u)) result.Add(u);
            return result;
        }

        /// <summary>
        /// 卡槽的候选：
        ///  · 大类属于"区域牌堆"（墓地/手牌/资产/附属区/秘社/封印区）→ 复用 `TargetZonePool.Collect`（含 `KindAlt` OR）；
        ///  · 否则（角色/暗藏者等场上卡）→ 扫各争夺区（`CardZone.AreaZone`），按 `MassTargetPool.Matches` 同一判定筛选；
        ///  · `TargetSlot.ExcludeInChosenArea` 为真且**地区槽已选** → 排除位于该地区的卡（FAQ 禁止原地移动）；
        ///  · 已在本效果其它槽选中的卡不再出现（同一张卡不能占两个槽）。
        /// </summary>
        public static List<CardInstance> CollectCards(EffectData data, int slotIndex, int sourceController,
            IList<TargetChoice> chosen, ZoneManager zm, CardManager cm = null, PlayerManager pm = null,
            CardInstance source = null, AreaManager areaManager = null)
        {
            var result = new List<CardInstance>();
            var slot = SlotAt(data, slotIndex);
            var spec = slot?.Spec;
            if (spec == null || zm == null || IsAreaSlot(slot)) return result;

            string chosenArea = ChosenAreaUid(data, chosen);

            if (TargetZonePool.IsZonePileSpec(spec))
            {
                foreach (var c in TargetZonePool.Collect(spec, sourceController, zm, pm))
                    if (PassesSlotExtras(slot, slotIndex, c, chosenArea, chosen, source)) result.Add(c);
                return result;
            }

            var areas = CollectAreaUids(areaManager);
            if (areas.Count == 0)
            {
                // 兜底：AreaManager 未注入时用来源所在地区（集成测试/最小装配下也能取到候选）
                string fallback = source?.ZoneDetailId;
                if (!string.IsNullOrEmpty(fallback)) areas.Add(fallback);
            }
            foreach (var areaUid in areas)
                foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, areaUid))
                {
                    if (c == null || c.IsDying) continue;
                    if (!PassesSlotExtras(slot, slotIndex, c, chosenArea, chosen, source)) continue;
                    if (!MassTargetPool.Matches(spec, c, sourceController, pm)) continue;
                    if (result.Contains(c)) continue;
                    result.Add(c);
                }
            return result;
        }

        /// <summary>槽的"附加条件"：本槽自身约束（排除已选/排除来源自身）+ 地区互斥。
        /// `slotIndex` 用于把"本槽自己的选择"排除在"同一张卡不能占两个槽"的判定之外。</summary>
        private static bool PassesSlotExtras(TargetSlot slot, int slotIndex, CardInstance c, string chosenArea,
            IList<TargetChoice> chosen, CardInstance source)
        {
            if (c == null) return false;
            var spec = slot.Spec;
            if (c == source && !spec.CanTargetSelf) return false;
            // 同一张卡不能同时占两个槽（本槽已有的选择不算冲突）
            if (ChosenContainsCard(chosen, c.UID, slotIndex)) return false;
            // FAQ 禁止原地移动：已选目的地内的卡不进候选
            if (slot.ExcludeInChosenArea && !string.IsNullOrEmpty(chosenArea)
                && string.Equals(c.ZoneDetailId ?? "", chosenArea, System.StringComparison.Ordinal)) return false;
            return true;
        }

        // ==================== 合法性与组合校验 ====================

        /// <summary>该选择在当前槽里是否合法（打出时与结算复检共用同一判定）。</summary>
        public static bool IsLegal(EffectData data, int slotIndex, TargetChoice choice, int sourceController,
            IList<TargetChoice> chosen, ZoneManager zm, CardManager cm, PlayerManager pm,
            CardInstance source, AreaManager areaManager, out string reason)
        {
            reason = null;
            var slot = SlotAt(data, slotIndex);
            if (slot?.Spec == null) { reason = "槽位声明缺失"; return false; }
            if (choice.IsEmpty) { reason = null; return true; }   // 留空由"组合校验"裁决（必选槽/最少数量）

            if (IsAreaSlot(slot))
            {
                if (!choice.IsArea) { reason = "该槽需要选择地区"; return false; }
                if (areaManager == null || areaManager.GetSlotIndex(choice.Uid) == 0)
                { reason = $"目标地区 {choice.Uid} 不存在"; return false; }
                return true;
            }
            // 【K24】玩家槽：按玩家号判定（与取候选同一实现 `PlayerTargetPool.Matches`）
            if (IsPlayerSlot(slot))
            {
                if (!choice.IsPlayer) { reason = "该槽需要选择玩家"; return false; }
                if (ChosenContainsPlayer(chosen, choice.PlayerId, slotIndex))
                { reason = $"玩家{choice.PlayerId} 已在本效果另一个槽中选中"; return false; }
                if (!PlayerTargetPool.Matches(slot.Spec, choice.PlayerId, sourceController, pm, out var pReason))
                { reason = pReason ?? $"玩家{choice.PlayerId} 不是合法目标"; return false; }
                return true;
            }
            if (choice.IsArea) { reason = "该槽需要选择卡，而不是地区"; return false; }
            if (choice.IsPlayer) { reason = "该槽需要选择卡，而不是玩家"; return false; }

            var card = cm?.GetInstance(choice.Uid);
            if (card == null) { reason = $"目标卡 {choice.Uid} 不存在"; return false; }
            // 与候选池同源判定（含地区互斥 / 同卡不可占两槽 / 自身排除）
            string chosenArea = ChosenAreaUid(data, chosen);
            if (!PassesSlotExtras(slot, slotIndex, card, chosenArea, chosen, source))
            {
                if (slot.ExcludeInChosenArea && string.Equals(card.ZoneDetailId ?? "", chosenArea, System.StringComparison.Ordinal))
                    reason = $"目标 {DisplayName(card)} 已经在目标地区内（不能原地移动）";
                else
                    reason = $"目标 {DisplayName(card)} 已在本效果另一个槽中选中";
                return false;
            }
            bool ok = TargetZonePool.IsZonePileSpec(slot.Spec)
                ? TargetZonePool.Matches(slot.Spec, card, sourceController, pm)
                : MassTargetPool.Matches(slot.Spec, card, sourceController, pm);
            if (!ok) reason = $"目标 {DisplayName(card)} 不满足该槽的条件";
            return ok;
        }

        /// <summary>整组选择是否合法（返回 null＝合法；否则中文原因）。</summary>
        public static string Validate(EffectData data, IList<TargetChoice> chosen, int sourceController,
            ZoneManager zm, CardManager cm = null, PlayerManager pm = null, CardInstance source = null,
            AreaManager areaManager = null)
        {
            if (!IsMultiTarget(data)) return null;
            int n = data.TargetSlots.Count;
            for (int i = 0; i < n; i++)
            {
                var slot = data.TargetSlots[i];
                var choice = ChoiceAt(chosen, i);
                bool empty = choice.IsEmpty;
                if (empty && slot != null && !slot.Optional)
                    return $"『{SlotLabel(data, i)}』是必选目标，必须先选择";
                if (!empty && !IsLegal(data, i, choice, sourceController, chosen, zm, cm, pm, source, areaManager, out var reason))
                    return reason;
            }
            int picked = CountChosenTargets(data, chosen);
            if (picked < data.MinChosenSlots)
                return $"至少需要选择 {data.MinChosenSlots} 个目标（当前 {picked} 个）";
            return null;
        }

        /// <summary>已填槽数（含地区槽）。</summary>
        public static int CountChosen(EffectData data, IList<TargetChoice> chosen)
        {
            if (!IsMultiTarget(data)) return 0;
            int picked = 0;
            for (int i = 0; i < data.TargetSlots.Count; i++)
                if (!ChoiceAt(chosen, i).IsEmpty) picked++;
            return picked;
        }

        /// <summary>
        /// 已选中的**目标**数量＝已填的"卡目标槽"数量（**不含地区槽**）。
        /// `EffectData.MinChosenSlots` 与它比较（用户口径："至少选 1 个目标"说的是角色目标，
        /// 目的地地区是另一回事）。
        /// </summary>
        public static int CountChosenTargets(EffectData data, IList<TargetChoice> chosen)
        {
            if (!IsMultiTarget(data)) return 0;
            int picked = 0;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (IsAreaSlot(data.TargetSlots[i])) continue;
                if (!ChoiceAt(chosen, i).IsEmpty) picked++;
            }
            return picked;
        }

        /// <summary>已选中的"目标地区"UID（取第一个地区槽的值；无则空串）。</summary>
        public static string ChosenAreaUid(EffectData data, IList<TargetChoice> chosen)
        {
            if (!IsMultiTarget(data)) return "";
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (!IsAreaSlot(data.TargetSlots[i])) continue;
                var c = ChoiceAt(chosen, i);
                if (!c.IsEmpty) return c.Uid;
            }
            return "";
        }

        /// <summary>已选中的"卡目标"UID（按槽序；**跳过留空的槽**，不含地区槽与玩家槽）。</summary>
        public static List<string> ChosenCardUids(EffectData data, IList<TargetChoice> chosen)
        {
            var result = new List<string>();
            if (!IsMultiTarget(data)) return result;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (IsAreaSlot(data.TargetSlots[i]) || IsPlayerSlot(data.TargetSlots[i])) continue;
                var c = ChoiceAt(chosen, i);
                if (!c.IsEmpty) result.Add(c.Uid);
            }
            return result;
        }

        /// <summary>【K24】已选中的"目标玩家"号（按槽序；跳过留空槽）。</summary>
        public static List<int> ChosenPlayerIds(EffectData data, IList<TargetChoice> chosen)
        {
            var result = new List<int>();
            if (!IsMultiTarget(data)) return result;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (!IsPlayerSlot(data.TargetSlots[i])) continue;
                var c = ChoiceAt(chosen, i);
                if (c.IsPlayer) result.Add(c.PlayerId);
            }
            return result;
        }

        /// <summary>
        /// 已选中的卡目标 UID，**按卡槽对齐**（长度＝卡槽数；留空的槽为空串）。
        /// 入堆栈时用这个（`StackEntry.TargetCardUids`）：留空位置必须保住，
        /// 否则结算时无法还原"哪个目标属于哪个槽"（会出现"敌方角色被塞进本方角色槽"的误判）。
        /// </summary>
        public static List<string> SlotAlignedCardUids(EffectData data, IList<TargetChoice> chosen)
        {
            var result = new List<string>();
            if (!IsMultiTarget(data)) return result;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (IsAreaSlot(data.TargetSlots[i]) || IsPlayerSlot(data.TargetSlots[i])) continue;
                result.Add(ChoiceAt(chosen, i).Uid ?? "");
            }
            return result;
        }

        /// <summary>
        /// 【K24】已选中的"目标玩家"号，**按玩家槽对齐**（长度＝玩家槽数；留空的槽写 -1）。
        /// 入堆栈时用这个（`StackEntry.TargetPlayerIds`），与 `SlotAlignedCardUids` 同一纪律。
        /// </summary>
        public static List<int> SlotAlignedPlayerIds(EffectData data, IList<TargetChoice> chosen)
        {
            var result = new List<int>();
            if (!IsMultiTarget(data)) return result;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (!IsPlayerSlot(data.TargetSlots[i])) continue;
                var c = ChoiceAt(chosen, i);
                result.Add(c.IsPlayer ? c.PlayerId : -1);
            }
            return result;
        }

        // ==================== 辅助 ====================

        /// <summary>
        /// 由"入堆栈的负载"反推逐槽选择（结算与联机复检用）：
        /// 地区槽 ← `areaUid`；卡槽 ← `cardUids` **按槽序**依次填充；【K24】玩家槽 ← `playerIds` 按槽序填充。
        /// 槽数与已存目标数不一致时按较短者处理（多余的一侧忽略）。
        /// </summary>
        public static List<TargetChoice> Reconstruct(EffectData data, string areaUid, IList<string> cardUids,
            IList<int> playerIds = null)
        {
            var result = new List<TargetChoice>();
            if (!IsMultiTarget(data)) return result;
            int ci = 0, pi = 0;
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                if (IsAreaSlot(data.TargetSlots[i]))
                {
                    result.Add(string.IsNullOrEmpty(areaUid) ? TargetChoice.Empty : TargetChoice.Area(areaUid));
                }
                else if (IsPlayerSlot(data.TargetSlots[i]))
                {
                    int pid = (playerIds != null && pi < playerIds.Count) ? playerIds[pi] : -1;
                    result.Add(pid > 0 ? TargetChoice.Player(pid) : TargetChoice.Empty);
                    pi++;
                }
                else
                {
                    string uid = (cardUids != null && ci < cardUids.Count) ? cardUids[ci] : null;
                    result.Add(string.IsNullOrEmpty(uid) ? TargetChoice.Empty : TargetChoice.Card(uid));
                    ci++;
                }
            }
            return result;
        }

        private static TargetChoice ChoiceAt(IList<TargetChoice> chosen, int index)
            => (chosen != null && index >= 0 && index < chosen.Count) ? chosen[index] : TargetChoice.Empty;

        private static bool ChosenContainsCard(IList<TargetChoice> chosen, string cardUid, int ignoreIndex = -1)
        {
            if (chosen == null || string.IsNullOrEmpty(cardUid)) return false;
            for (int i = 0; i < chosen.Count; i++)
            {
                if (i == ignoreIndex) continue;      // 本槽自己的选择不算"占用另一个槽"
                var c = chosen[i];
                if (!c.IsArea && !c.IsPlayer && string.Equals(c.Uid, cardUid, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>【K24】同一玩家不能同时占两个玩家槽（本槽自己的选择不算冲突）。</summary>
        private static bool ChosenContainsPlayer(IList<TargetChoice> chosen, int playerId, int ignoreIndex = -1)
        {
            if (chosen == null || playerId <= 0) return false;
            for (int i = 0; i < chosen.Count; i++)
            {
                if (i == ignoreIndex) continue;
                if (chosen[i].IsPlayer && chosen[i].PlayerId == playerId) return true;
            }
            return false;
        }

        /// <summary>槽位提示文本（声明了 `Label` 用它，否则按大类给出）。</summary>
        public static string SlotLabel(EffectData data, int index)
        {
            var slot = SlotAt(data, index);
            if (slot == null) return $"槽{index + 1}";
            if (!string.IsNullOrEmpty(slot.Label)) return slot.Label;
            if (IsAreaSlot(slot)) return "目标地区";
            if (IsPlayerSlot(slot)) return PlayerTargetPool.DescribeSpec(slot.Spec);
            return TargetZonePool.IsZonePileSpec(slot.Spec)
                ? TargetZonePool.DescribeZone(slot.Spec.Kind)
                : $"{slot.Spec.Kind}目标";
        }

        private static string DisplayName(CardInstance c)
            => c?.PlayerTemplate?.CardName ?? c?.UID ?? "?";

        /// <summary>人类可读的已选目标描述（日志/确认面板用）。</summary>
        public static string Describe(EffectData data, IList<TargetChoice> chosen, CardManager cm = null,
            PlayerManager pm = null)
        {
            if (!IsMultiTarget(data)) return "（非多目标效果）";
            var parts = new List<string>();
            for (int i = 0; i < data.TargetSlots.Count; i++)
            {
                var c = ChoiceAt(chosen, i);
                string label = SlotLabel(data, i);
                if (c.IsEmpty) { parts.Add($"{label}=（空）"); continue; }
                string name = c.IsArea ? c.Uid
                    : (c.IsPlayer ? PlayerTargetPool.Describe(pm, c.PlayerId) : DisplayName(cm?.GetInstance(c.Uid)));
                parts.Add($"{label}={name}");
            }
            return string.Join("；", parts);
        }
    }
}
