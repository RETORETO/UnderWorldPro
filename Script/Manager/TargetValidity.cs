using Godot;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 目标丢失校验链（目标系统·丢失规则，2026-08-07）。
    /// 统一收口"结算前目标是否仍合法"的判断，玩家卡(TargetRule)与效果系统(TargetSpec)
    /// 经 ITargetDesc 共用此链，无需关心数据来源。
    ///
    /// 规则覆盖（取自需求）：
    ///  ① 角色离场/翻面 → 丢失
    ///  ② 暗藏者离场/翻面 → 丢失
    ///  ③ 角色或暗藏者目标，翻面仍丢（正面→背面 COVERT / 背面→正面 UNCOVER 任一方向均判丢失）
    ///  ④ 附属离开原附属区 → 丢失
    ///  ⑤ 封印离开原封印区 → 丢失
    ///  ⑥ 本地区限定，来源移动 → 丢失（仅 desc.IsLocalZone 为 true 的卡/效果才判）
    ///  ⑦ 目标获屏障 → 非法被反击（实现上等同丢失）
    ///  ⑧ 以墓地内卡为目标，目标离墓地 → 丢失
    ///  ⑨ 以手牌卡为目标，目标离手牌 → 丢失
    /// 以上规则可经新增 checker 扩展，调用方不感知。
    ///
    /// 注意：本类只判定"是否仍合法"，不负责"丢失后怎么处置"（进墓地/不结算/部分跳过），
    /// 那是调用方（TurnManager.ResolveEntryTargetIsLost / EffectEngine.Instance.ValidateTargetAtResolve）按 ResolveKind 决定的事。
    ///
    /// 关于"结算前属性变化"（控制者变化/子类别获得失去）：本类不再做方向性过程记忆判定，
    /// 而由 D16 版本号方案在 TurnManager 统一覆盖（见 IsTargetLost 比对 TargetVersionId；BumpVersion 由
    /// 控制者变更/子类别变更/移动/翻面等触发）。本类仅保留规则⑥本地区与规则⑦屏障两类静态/位置判定。
    /// </summary>
    public static class TargetValidity
    {
        /// <summary>注入的 PlayerManager 引用（由 GameInitializer 在装配时赋值）。
        /// 用于"条件式限制"（如 AttachedToCharacterIfNoMind）在取目标阶段评估"资产区是否具【心】"等打出时状态。
        ///
        /// 【批次 2·多对局隔离】本字段是**进程级兜底**：一个服务端进程同时跑多场对局时，它只会指向"最后构造的那一局"。
        /// 因此所有生产调用点都应显式传入本局的 PlayerManager（见 `CheckRestrictions`/`Check` 的 `pm` 形参，
        /// 由 `CardManager.PlayerManagerRef` / `TurnManager.PlayerManagerRef` 提供）；本字段仅服务于未注入的旧路径与单测。</summary>
        public static PlayerManager PlayerManagerRef;

        /// <summary>
        /// 入栈时【来源】快照（结算复检的比较基准）。由 TurnManager.CaptureTargetSnapshot 在目标锁定入栈时记录。
        ///
        /// 设计（2026-08-11 定稿）：只记录"来源侧"的两项基准，不再记录目标侧属性快照——
        /// 目标自身的属性变化（控制者/子类别/朝向/离场）已由 D16 版本号方案统一覆盖，
        /// 这里只需保留那些"版本号刷不到"的比较基准：
        ///  · SourceZoneDetailId —— 规则⑥"本地区"：以【效果发动时所在地区】为唯一基准；
        ///  · SourceControllerId —— Friendly/Enemy：以【效果发动时来源控制者】为基准，
        ///    这样来源自己事后被夺控也不改变入栈时确定的敌我关系。
        /// 原 ControllerId / SubCategories（目标侧快照）已随方向性记忆判定一并删除。
        /// </summary>
        public readonly struct TargetSnapshot
        {
            /// <summary>入栈时【来源(自身)地区】快照，规则⑥"本地区"的唯一比较基准。null=未记录。</summary>
            public readonly string SourceZoneDetailId;
            /// <summary>入栈时【来源控制者】快照，Friendly/Enemy 的比较基准。-1=未记录。</summary>
            public readonly int SourceControllerId;
            public TargetSnapshot(string sourceZoneDetailId, int sourceControllerId = -1)
            {
                SourceZoneDetailId = sourceZoneDetailId;
                SourceControllerId = sourceControllerId;
            }
        }

        /// <summary>
        /// 校验结算前目标是否仍合法。
        /// </summary>
        /// <param name="source">效果/卡牌来源（用于本地区/控制者判定）。</param>
        /// <param name="target">当前目标实例。</param>
        /// <param name="desc">目标描述（TargetRule 或 TargetSpec）。</param>
        /// <param name="snapshot">目标入栈时快照；用于"结算前变化"类（控制者/子类别）判定。为 null 则跳过此类判定（兜底场景）。</param>
        /// <param name="pm">本对局的 PlayerManager（**多对局隔离**：由调用方传入；null 时退回进程级 `PlayerManagerRef` 兜底）。</param>
        /// <returns>(仍合法?, 丢失原因)。</returns>
        public static (bool valid, TargetLossReason reason) Check(
            CardInstance source, CardInstance target, ITargetDesc desc, TargetSnapshot? snapshot = null,
            PlayerManager pm = null)
        {
            var primary = CheckOne(source, target, desc, snapshot, pm);
            if (primary.valid) return primary;
            // 【P3·复合目标限制】`SpecAlt`＝第二条**整条**目标声明：本体整条不通过时，改用别名那条整条复检
            // （卡文"目标……或……"且两侧条件不同，如 JC043「结附于秘社**或**本地区」）。
            if (desc is TargetSpec specAltHolder && specAltHolder.SpecAlt != null)
            {
                var alt = CheckOne(source, target, specAltHolder.SpecAlt, snapshot, pm);
                if (alt.valid) return alt;
            }
            return primary;
        }

        private static (bool valid, TargetLossReason reason) CheckOne(
            CardInstance source, CardInstance target, ITargetDesc desc, TargetSnapshot? snapshot = null,
            PlayerManager pm = null)
        {
            // 0. 实例不存在 / 已移出游戏
            if (target == null || target.CurrentZone == CardZone.Exiled)
                return (false, TargetLossReason.Gone);

            // 1. 大类对应的合法区域（含翻面朝向判定）：离场或翻面都在此合一处理
            //    【K14·或种类】`KindAlt` 非 None 时与 `Kind` 构成 OR（卡文"目标附属或资产"，JC107）：
            //    主大类不通过就试第二个大类，两者都不通过才判丢失。
            var zoneCheck = CheckZoneAndFace(target, desc.Kind, desc.IncludeCovert);
            if (!zoneCheck.valid && desc.KindAlt != TargetKind.None)
                zoneCheck = CheckZoneAndFace(target, desc.KindAlt, desc.IncludeCovert);
            if (!zoneCheck.valid)
                return (false, zoneCheck.reason);

            // 2. 本地区限定（规则⑥）：以【发动地快照】为唯一基准（2026-08-11 定稿，2026-08-11 修正）。
            //    语义：效果发动时来源所在的那个地区是唯一基准；结算时只判断【目标】是否仍在该地区，
            //    不再判断【来源(发动角色)】的地区——效果发动后即与发动角色分离，发动角色去哪（如回手牌）
            //    不影响本地区判定的结果。
            //    不再用 source.ZoneDetailId vs target.ZoneDetailId 实时比对，也不再区分"目标是否为来源自身"——
            //    因为争夺区→争夺区移动已不刷版本号，必须由本判定独立兜住"目标跑出发动地"的情况。
            if (desc.IsLocalZone)
            {
                string snapZone = snapshot?.SourceZoneDetailId;
                if (!string.IsNullOrEmpty(snapZone))
                {
                    // 目标必须仍在发动地区（目标跑出发动地 → 不再是"本地区"目标）。
                    // 仅判目标，发动角色自身离开发动地不在此判（来源地区不再参与判定）。
                    if (target.ZoneDetailId != snapZone)
                        return (false, TargetLossReason.OutOfLocalZone);
                }
            }

            // 3. 合法性丧失（规则⑦·屏障）：**已在 CheckRestrictions 开头统一判定**（取目标阶段与结算复检共用），
            //    此处不再重复实现，保留本注释说明历史 TODO 已落地（见 IsBarrierBlocking）。

            // 4. 结算复检 Restrictions（方案 B·单一真相源）：Friendly/Enemy/子类别/卡名/费用等
            //    与区域无关的属性条件，取目标阶段(CardManager.MatchesRule)与结算阶段共用 CheckRestrictions。
            //    其中 Friendly/Enemy 以入栈时【来源控制者快照】为基准（见 TargetSnapshot.SourceControllerId），
            //    来源自身事后被夺控不改变入栈时确定的敌我关系。
            int sourceController = snapshot?.SourceControllerId ?? -1;
            if (sourceController < 0) sourceController = source?.ControllerId ?? -1;
            // 结算复检前剔除"仅取目标阶段"的条件式限制（如 AttachedToCharacterIfNoMind）——
            // 其"【心】"判定只在打出（选目标）时做，结算时不复检（用户规则：结算时【心】是否仍在不影响）。
            // 其余 Restrictions（Friendly/Enemy/子类别/费用等）照常复检。
            var resolveRestrictions = ExcludePlayTimeOnlyRestrictions(desc.Restrictions);
            if (!CheckRestrictions(resolveRestrictions, desc.Param, target, sourceController, pm))
                return (false, TargetLossReason.Illegal);

            return (true, TargetLossReason.None);
        }

        /// <summary>
        /// 【P3·屏障】该目标是否"因屏障而不能被该来源指定为目标"。
        /// 规则文本：拥有屏障的卡**不能被敌方卡牌或能力指定为目标**（己方来源不受限制）。
        /// 屏障来源两类：① `CardInstance.HasGuard`（由 `Gain{"屏障"}` / `SetGuard` 设置，如 JC017）；
        /// ② 运行期授予的关键字 `屏障`（`KeywordEffects.Barrier()`，如 JC073 的"获得屏障"），以及卡面固有声明。
        /// <paramref name="sourceController"/> &lt;= 0（来源控制者未知）时**不拦**，避免把"无法判定敌我"误判为非法。
        /// </summary>
        public static bool IsBarrierBlocking(CardInstance target, int sourceController)
        {
            if (target == null) return false;
            if (sourceController <= 0) return false;                     // 来源控制者未知 → 不判
            if (target.ControllerId == sourceController) return false;    // 己方来源不受屏障限制
            bool guarded = target.HasGuard
                || target.HasKeyword("屏障")
                || (target.PlayerTemplate?.HasInherentKeyword("屏障") ?? false);
            return guarded;
        }

        /// <summary>剔除"仅取目标阶段"类限制（结算复检跳过），返回剩余限制列表。</summary>
        private static System.Collections.Generic.List<TargetRestriction> ExcludePlayTimeOnlyRestrictions(
            System.Collections.Generic.IEnumerable<TargetRestriction> restrictions)
        {
            var list = new System.Collections.Generic.List<TargetRestriction>();
            if (restrictions == null) return list;
            foreach (var r in restrictions)
            {
                // 条件式限制（【心】判定）仅在取目标阶段评估，结算复检跳过
                if (r == TargetRestriction.AttachedToCharacterIfNoMind) continue;
                list.Add(r);
            }
            return list;
        }

        /// <summary>
        /// 目标【精筛条件】统一判定（方案 B 单一真相源，2026-08-11）。
        /// 取目标阶段（CardManager.MatchesRule）与结算复检阶段（TargetValidity.Check）共用本方法，
        /// 保证"能选中的目标"与"结算时仍合法的目标"用同一份逻辑；新增限制只需在此加一个 case。
        ///
        /// 【不含大类粗筛】：大类判定由各调用方按自身语义负责——
        ///  · MatchesRule 用 MapInstanceToTargetKind（互斥映射，适合拖拽期快速筛候选）；
        ///  · Check 用 CheckZoneAndFace（按区域+朝向，含 IncludeCovert 放宽，适合结算期精确判定）。
        /// 若在此重复做 MapInstanceToTargetKind 粗筛，会导致"可对暗藏者生效(IncludeCovert=true)"的效果
        /// 在结算时被误判丢失（暗藏者映射为 Covert ≠ Character），故本方法刻意不碰大类。
        ///
        /// 【LocalZone 不在此判】：本地区的比较基准在两个阶段不同（取目标=来源当前地区、结算=发动地快照），
        /// 故本方法遇 LocalZone 直接跳过，由调用方各自处理（Check 内规则⑥即为结算侧实现）。
        /// </summary>
        /// <param name="restrictions">限制集合（AND 关系，全部满足才通过）。</param>
        /// <param name="param">限制参数（部分限制共用，按 '|' 分段，见各 case 注释）。</param>
        /// <param name="t">被判定的目标实例。</param>
        /// <param name="sourceController">敌我判定基准控制者（取目标=来源当前控制者；结算=入栈来源控制者快照）。</param>
        /// <param name="pm">本对局的 PlayerManager（**多对局隔离**：由调用方传入；null 时退回进程级 `PlayerManagerRef` 兜底）。</param>
        public static bool CheckRestrictions(
            System.Collections.Generic.IEnumerable<TargetRestriction> restrictions,
            string param, CardInstance t, int sourceController, PlayerManager pm = null)
        {
            if (t == null) return false;
            // 【P3·屏障】规则文本："不能被敌方卡牌或能力指定为目标"。
            // 收口在此处 = 取目标阶段（CardManager.MatchesRule/MatchesSpec）与结算复检（Check）共用同一实现，
            // 因此"先被指定为目标、之后才获得屏障"的情形在结算复检时自然判为目标不合法（用户补充规则）。
            // 判定基准：源控制者已知（>=1）且与目标控制者不同 → 视作"敌方来源"；源控制者未知(-1)时不拦（避免误伤）。
            if (IsBarrierBlocking(t, sourceController))
            {
                GD.Print($"⛔ [屏障] 目标 {t.PlayerTemplate?.CardName} 不能被敌方（P{sourceController}）指定为目标");
                return false;
            }
            if (restrictions == null) return true;
            param = param ?? string.Empty;
            foreach (var res in restrictions)
            {
                switch (res)
                {
                    case TargetRestriction.None:
                        break;
                    case TargetRestriction.Friendly:
                        if (t.ControllerId != sourceController) return false;
                        break;
                    // 【P3·复合目标限制】目标须是**结附于秘社**的附属（JC043 的一条；另一条用 `LocalZone`）。
                    case TargetRestriction.HostIsCouncil:
                        if (t.AttachedTo == null || CardManager.MapInstanceToTargetKind(t.AttachedTo) != TargetKind.Council)
                        {
                            GD.Print($"⛔ [HostIsCouncil] 目标 {t.PlayerTemplate?.CardName} 未结附于秘社 → 不合法");
                            return false;
                        }
                        break;
                    // 【P3·复合目标限制】目标须是**直接结附于地区**的附属（无宿主；JC043 的另一条
                    // 与 `LocalZone` 组合＝"结附于本地区的附属"，与"结附于该地区角色的附属"区分开）。
                    case TargetRestriction.AttachedToArea:
                        if (t.AttachedTo != null)
                        {
                            GD.Print($"⛔ [AttachedToArea] 目标 {t.PlayerTemplate?.CardName} 结附于卡（非地区）→ 不合法");
                            return false;
                        }
                        break;
                    case TargetRestriction.Enemy:
                        if (t.ControllerId == sourceController) return false;
                        break;
                    case TargetRestriction.CurrentSubCategory:
                        if (!t.HasSubCategory(param)) return false;
                        break;
                    case TargetRestriction.StaticSubCategory:
                        if (!t.HasStaticSubCategory(param)) return false;
                        break;
                    case TargetRestriction.CardName:
                        if (t.PlayerTemplate?.CardName != param
                            && t.CouncilTemplate?.CardName != param) return false;
                        break;
                    case TargetRestriction.SubCardName:
                        if (t.PlayerTemplate?.SubCardName != param) return false;
                        break;
                    case TargetRestriction.NotStaticSubCategory:
                        // "非某类别"：目标静态 SubCategories 含 Param 即不通过（如"非人类"）。
                        // 与 CostAtLeast 共用 param 时，按 '|' 分段：第0段为本限制的参数（如"人类"）。
                        if (t.HasStaticSubCategory(param.Split('|')[0])) return false;
                        break;
                    case TargetRestriction.CostAtLeast:
                        // 目标静态印刷费用门槛：低于 Param（整数文本）即不通过（读 PlayerTemplate.Cost 静态数据）。
                        // 与 NotStaticSubCategory 共用 param 时，按 '|' 分段：第1段为本限制的参数（如"3"）。
                        string costParam = param.Contains("|") ? param.Split('|')[1] : param;
                        if (!int.TryParse(costParam, out int minCost) || t.PlayerTemplate?.Cost < minCost) return false;
                        break;
                    case TargetRestriction.LocalZone:                        // 本地区：比较基准随阶段不同，交由调用方判定（结算侧见 Check 规则⑥），此处跳过。
                        break;
                    // ---- 【P1】新增精筛条件 ----
                    case TargetRestriction.CostAtMost:
                    {
                        // 目标静态印刷费用上限：高于 Param（整数文本）即不通过。
                        // Param 约定：单独写门槛（"2"）；与其它限制共用 Param 时按 '|' 分段取第 2 段（第 0/1 段留给
                        // NotStaticSubCategory / CostAtLeast），避免与既有约定冲突。
                        string capParam = param.Contains("|")
                            ? (param.Split('|').Length > 2 ? param.Split('|')[2] : param.Split('|')[^1])
                            : param;
                        if (!int.TryParse(capParam, out int maxCost) || t.PlayerTemplate?.Cost > maxCost) return false;
                        break;
                    }
                    case TargetRestriction.Tapped:
                        if (!t.IsTapped) return false;
                        break;
                    case TargetRestriction.HasMagicDomainIcon:
                        // 实例领域优先、回退模板；印刷"-1"（无领域）视为没有图标。
                        if (!t.HasAnyMagicDomainIcon) return false;
                        break;
                    case TargetRestriction.Organization:
                    {
                        string org = t.PlayerTemplate?.Organization ?? t.CouncilTemplate?.Organization ?? "";
                        if (!string.Equals(org, param, System.StringComparison.Ordinal)) return false;
                        break;
                    }
                    case TargetRestriction.HasCounter:
                    {
                        // 【P3·K21】目标须带指定**卡上标志**（JC069「具有锁定标志的角色」）：
                        // 判定 `Counters[Param] ≥ 1`；标志名未给出（Param 空）→ 不通过（fail-closed，防拼错静默放行）。
                        if (string.IsNullOrWhiteSpace(param))
                        {
                            Godot.GD.Print("[CheckRestrictions] HasCounter 缺少 Param（标志名）→ 不通过");
                            return false;
                        }
                        t.Counters.TryGetValue(param.Trim(), out int counterCount);
                        if (counterCount < 1) return false;
                        break;
                    }
                    case TargetRestriction.HasKeyword:
                        // 双层语义（与 CardInstance.HasPublicKeyword 一致）：运行时授予 ∪ 模板固有声明。
                        if (!t.HasKeyword(param) && !(t.PlayerTemplate?.HasInherentKeyword(param) ?? false)) return false;
                        break;
                    case TargetRestriction.AttachedToCharacterIfNoMind:
                        // 条件式：仅取目标(打出)阶段评估。资产区【无】【心】(心灵领域忠诚≥1) 时，
                        // 目标须结附于角色（AttachedTo 为角色）；有【心】时放行任意附属。
                        // 结算复检阶段此限制【跳过】（见 Check 的过滤），故这里不区分阶段、直接按实时【心】判定——
                        // 因本方法只在取目标阶段被调用（结算侧 Check 会先剔除本限制再进 CheckRestrictions）。
                        {
                            bool hasMind = HasMindDomain(sourceController, pm);
                            if (hasMind) break;  // 有【心】→ 放行
                            // 无【心】→ 目标必须"结附于角色"：宿主存在、且为玩家卡（PlayerTemplate 非空）、且类别含 Character。
                            // 秘社宿主 PlayerTemplate 为 null（用 CouncilTemplate），地区附属 AttachedTo 为 null，均应拦截。
                            var host = t.AttachedTo;
                            bool attachedToCharacter = host != null
                                && host.PlayerTemplate != null
                                && (host.PlayerTemplate.CategoryFlag & CardCategory.Character) != 0;
                            if (!attachedToCharacter)
                                return false;
                        }
                        break;
                    default:
                        Godot.GD.Print($"[CheckRestrictions] 未实现的 TargetRestriction={res}，暂视为不通过");
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 按大类判定目标是否仍在合法区域、朝向是否正确（翻面=丢失）。
        /// 覆盖规则 ①②③④⑤⑧⑨。
        /// </summary>
        private static (bool valid, TargetLossReason reason) CheckZoneAndFace(
            CardInstance target, TargetKind kind, bool includeCovert = false)
        {
            switch (kind)
            {
                    case TargetKind.Character:
                    // 角色须正面在争夺区（AreaZone 且未翻面成暗藏）；翻面(正面→背面)即丢失
                    // includeCovert=true 时放宽：角色"或"暗藏者均合法（仅校验区域，不校验朝向）。
                    if (target.CurrentZone != CardZone.AreaZone)
                        return (false, TargetLossReason.LeftZone);
                    if (!includeCovert && target.IsCovert)
                        return (false, TargetLossReason.Covert);
                    return (true, TargetLossReason.None);

                case TargetKind.Covert:
                    // 暗藏者须背面在争夺区（AreaZone 且 IsCovert）；解除暗藏(背面→正面)即丢失
                    if (target.CurrentZone != CardZone.AreaZone || !target.IsCovert)
                        return (false, TargetLossReason.Uncover);
                    return (true, TargetLossReason.None);

                case TargetKind.Attachment:
                    // 附属须仍在附属区
                    if (target.CurrentZone != CardZone.AttachedZone)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Seal:
                    // 封印须仍在封印区
                    if (target.CurrentZone != CardZone.SealedZone)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Asset:
                    if (target.CurrentZone != CardZone.AssetZone)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Council:
                    if (target.CurrentZone != CardZone.CouncilZone)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.StackCard:
                    if (target.CurrentZone != CardZone.Stack)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Graveyard:
                    // 规则⑧：以墓地内卡为目标
                    if (target.CurrentZone != CardZone.Graveyard)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Hand:
                    // 规则⑨：以手牌卡为目标
                    if (target.CurrentZone != CardZone.Hand)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Area:
                    // 地区槽位：地区牌一般不翻面/获屏障，按存在性；若未来需细化再扩展
                    if (target.CurrentZone != CardZone.AreaZone)
                        return (false, TargetLossReason.LeftZone);
                    return (true, TargetLossReason.None);

                case TargetKind.Player:
                case TargetKind.None:
                default:
                    // 玩家/无目标：不判区域丢失
                    return (true, TargetLossReason.None);
            }
        }

        /// <summary>判定某控制者资产区是否具【心】（心灵领域忠诚 ≥ 1）。取目标阶段的条件式限制评估用。
        /// pm = 本对局的 PlayerManager（多对局隔离）；null 时退回进程级 `PlayerManagerRef`（旧路径/单测兜底）。</summary>
        private static bool HasMindDomain(int controllerId, PlayerManager pm = null)
        {
            pm ??= PlayerManagerRef;
            if (pm == null || controllerId < 0)
            {
                // 【P0·缺依赖要吵出来】条件式限制的求值必须拿到"本局" PlayerManager；两端任一装配漏注入都会
                // 得到与服务端相反的结果（历史上客户端就是因此把"有【心】"判成"无【心】"）。故这里显式告警，
                // 不再静默按"不满足"处理而无人察觉。
                Godot.GD.PushWarning("[TargetValidity] 条件式限制需要本局 PlayerManager，但调用方未传参且未注入 " +
                    "TargetValidity.PlayerManagerRef → 本次按【不满足】判定（请检查装配：生产路径应由 " +
                    "CardManager.PlayerManagerRef / 客户端 ClientMatchView 注入）");
                return false;
            }
            var loyalty = pm.GetDomainLoyalty(controllerId);
            return loyalty.TryGetValue(MagicDomain.Mind, out int v) && v >= 1;
        }
    }
}
