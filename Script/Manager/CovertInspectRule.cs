using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【JC057 监控员②·持续可见性授权】＋【UI 悬浮预览】的**单一真相源**。
    ///
    /// 卡文（JC057 监控员）：「持续：**只要监控员未横置，你可以随时检视本地区的任一暗藏者**。」
    ///
    /// 这是一条**不经过效果结算的查看通道**（"随时"＝任何时刻都能看，弹窗式 `PromptPickCard` 那种
    /// 一次性检视表达不了），因此判定必须是一个**静态纯函数**：读卡面印刷规则 ＋ 实例态，
    /// **不依赖 `EffectEngine` 实例** ⇒ 单机 / 联机客户端 / 服务端同解，也不会随快照投影丢失。
    ///
    /// 可见性口径（用户 2026-09 口径：**所有可见卡都弹**悬浮预览）：
    ///  · **自己的卡**（控制者或拥有者＝查看者）：一律可见 —— 包括**自己的手牌**与**自己的暗藏者**；
    ///  · **场上正面卡 / 附属 / 秘社 / 资产 / 墓地 / 地区**：可见（本就是公开信息）；
    ///  · **对手的手牌**：不可见（隐藏信息）；
    ///  · **对手的暗藏者**：不可见 —— **除非**查看者有一张**未横置**的、**位于该暗藏者所在地区**的
    ///    JC057 类卡（`StaticKind.AllowInspectLocalCovert` ＋ 前提 `SelfUntapped`）。
    ///
    /// 说明：`StaticKind.AllowInspectLocalCovert` 的"未横置"前提由持续效果注册表承担（会随横置/重置实时重判），
    /// 但本类的判定**另按卡面印刷规则自行复检一次** `SelfUntapped`——这样"引擎侧是否已注册"不影响结论，
    /// 判定在两端与任意时刻都成立（与 JC081 的 `GetAttachSubCategoryWhitelist` 同款思路）。
    /// </summary>
    public static class CovertInspectRule
    {
        /// <summary>
        /// 查看者能否看到该卡的**卡面**（＝UI 是否弹出"整张卡图"的悬浮预览）。
        /// </summary>
        /// <param name="card">被查看的卡实例（可为空 → 不可见）。</param>
        /// <param name="viewerPlayerId">查看者玩家号（≤0 → 不可见，fail-closed）。</param>
        /// <param name="cm">卡管理器（枚举场上卡；缺失时只按"自己的卡/对手手牌/对手暗藏"判定）。</param>
        public static bool CanViewerSeeCardFace(CardInstance card, int viewerPlayerId, CardManager cm = null)
        {
            if (card == null || viewerPlayerId <= 0) return false;
            if (card.IsHidden) return false;                     // 联机客户端的隐藏占位：没有模板/卡面

            // 1) 自己的卡：手牌、暗藏者、场上卡全部可见（用户口径："自己的暗藏者也可以弹出悬浮UI"）
            if (card.ControllerId == viewerPlayerId || card.OwnerId == viewerPlayerId) return true;

            // 2) 对手的手牌：隐藏信息，永不预览
            if (card.CurrentZone == CardZone.Hand) return false;

            // 3) 对手的暗藏者：默认不可见 → 除非被"随时检视"授权（JC057）
            if (card.IsCovert)
                return IsInspectAuthorized(card, viewerPlayerId, cm);

            // 4) 其余（场上正面卡 / 附属 / 秘社 / 资产 / 墓地 / 地区 …）：公开信息，可预览
            return true;
        }

        /// <summary>
        /// 【JC057】查看者是否对这张**暗藏者**拥有"随时检视"授权：
        /// 查看者控制着某张**未横置**的、**与该暗藏者位于同一地区**的
        /// "随时检视本地区任一暗藏者"类卡（`StaticKind.AllowInspectLocalCovert`，前提 `SelfUntapped`）。
        /// 无区域信息（不在争夺区）→ 不授权（fail-closed）。
        /// </summary>
        public static bool IsInspectAuthorized(CardInstance covert, int viewerPlayerId, CardManager cm = null)
        {
            if (covert == null || cm == null || viewerPlayerId <= 0) return false;
            string area = covert.ZoneDetailId;
            if (string.IsNullOrEmpty(area)) return false;

            foreach (var src in cm.GetAreaCards())
            {
                if (src == null || src.ControllerId != viewerPlayerId) continue;
                if (src.IsTapped) continue;                       // 前提：来源**未横置**
                if (src.ZoneDetailId != area) continue;            // 前提：**本地区**（同一地区）
                if (HasPrintedLocalCovertInspect(src)) return true;
            }
            // 附属类来源（理论上 JC057 是角色；此处兜底覆盖"结附于本地区角色"的同款声明）
            var zm = cm.ZoneManagerRef;
            if (zm != null)
            {
                foreach (var src in zm.GetZoneCards(CardZone.AttachedZone, 0))
                {
                    if (src == null || src.ControllerId != viewerPlayerId) continue;
                    if (src.IsTapped) continue;
                    if (src.ZoneDetailId != area) continue;
                    if (HasPrintedLocalCovertInspect(src)) return true;
                }
            }
            return false;
        }

        /// <summary>该卡是否**印刷**了"随时检视本地区任一暗藏者"（`StaticKind.AllowInspectLocalCovert`）
        /// 且其**未横置**前提成立（`PrereqKind.SelfUntapped`；未声明前提的按"始终成立"处理）。</summary>
        public static bool HasPrintedLocalCovertInspect(CardInstance card)
        {
            if (card == null) return false;
            // 【用户口径·2026-09】背面朝上的卡（暗藏者）**不提供**持续/静止规则 ⇒ 本卡的"随时检视"授权失效
            // （与 `RegisterPersistent` 的暗藏门禁同口径；本判定读印刷规则，故必须在此单独判一次）。
            if (card.IsCovert) return false;
            foreach (var e in card.EffectiveEffects)
            {
                if (e?.StaticEffects == null) continue;
                bool hasFlag = false;
                foreach (var s in e.StaticEffects)
                    if (s != null && s.Kind == StaticKind.AllowInspectLocalCovert) { hasFlag = true; break; }
                if (!hasFlag) continue;
                // 未横置前提：声明了就必须成立（横置 → 失去授权）；未声明前提 → 视为始终成立
                if (e.PrereqSpec != null && e.PrereqSpec.Kind == PrereqKind.SelfUntapped && card.IsTapped) continue;
                return true;
            }
            return false;
        }
    }
}
