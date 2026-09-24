using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【K1·动态数值 X】取值表达式的**唯一求值实现**（用户 2026-09 口径）。
    ///
    /// 卡文里的"X"由 `EffectData.ValueExpr` 声明，动作参数里用占位符 `$X`（见 `EffectAction.Args`）。
    /// 已登记的表达式（**未登记的写法一律 fail-closed**：视为求值失败 → 该效果不发动，并告警，避免"拼错字段名静默算成 0/全量"）：
    ///
    /// | 写法 | 含义 | 例 |
    /// |---|---|---|
    /// | `事件数量` | **本次触发事件携带的数量**（`TriggerContext.ExtraInt`，如"本次弃牌的总张数"） | JC040「遏制X，X＝此次弃牌的数量」 |
    /// | `计数:区域:区域名:类别:阵营` | 某区域内的卡数（区域：`墓地/手牌/资产/争夺区/附属区/秘社/封印区`；类别：`角色/附属/法术/任意`；阵营：`自己/对手/双方`） | JC097「袭击X，X＝你墓地中角色牌的数量」 |
    /// | `计数:卡上标志:标志名:对象` | 某卡上的标志数（对象：`自身/目标`） | JC045「其上每有一个时间标志…」类 |
    /// | `计数:地区标志:阵营:地区` | 某地区的势力标志数（阵营：`自己/对手`；地区：`本地区/目标地区`） | JC048 类 |
    /// | `玩家选择` | **玩家输入的数值**——需要"数值输入 UI"，**当前未实现** → 按求值失败处理（不发动） | JC055/JC083 |
    ///
    /// **X=0 的语义（用户 2026-09 明确）**：`X` 求值为 **0 时该效果不算发动**（由调用方按 `DynamicValueState.Blocked` 处理；
    /// 玩家主动选择的 X 也不允许选 0，见 `玩家选择` 分支的说明）。
    /// </summary>
    public static class EffectValueResolver
    {
        /// <summary>动作参数里的占位符（会被替换为求值结果）。</summary>
        public const string Placeholder = "$X";

        /// <summary>取值上下文（求值所需的对局信息）。</summary>
        public struct ValueContext
        {
            public CardInstance Source;        // 效果来源卡（"自己"＝其控制者，"自身"＝它）
            public CardInstance Target;        // 本次结算的目标卡（"目标"）
            public string TargetAreaId;        // 本次结算的目标地区（"目标地区"）
            public int EventAmount;            // 本次事件携带的数量（-1 = 无）
            public ZoneManager ZoneManager;
            public CardManager CardManager;
            public PlayerManager PlayerManager;
            public AreaManager AreaManager;
        }

        /// <summary>求值结果。</summary>
        public enum ValueEvalResult
        {
            NoExpr,   // 该效果没有声明动态值（不参与）
            Ok,       // 求值成功
            Fail,     // 表达式未登记 / 依赖未实现 → 调用方按"不发动"处理（fail-closed）
        }

        /// <summary>求值。`value` 仅在返回 `Ok` 时有效。</summary>
        public static ValueEvalResult Evaluate(string expr, in ValueContext ctx, out int value, out string reason)
        {
            value = 0;
            reason = null;
            if (string.IsNullOrWhiteSpace(expr)) return ValueEvalResult.NoExpr;

            string e = expr.Trim();
            if (e == "事件数量")
            {
                if (ctx.EventAmount < 0) { reason = "本次事件未携带数量（ExtraInt 缺失）"; return ValueEvalResult.Fail; }
                value = ctx.EventAmount;
                return ValueEvalResult.Ok;
            }
            if (e == "玩家选择")
            {
                reason = "『玩家选择』需要数值输入 UI（尚未实现）";
                return ValueEvalResult.Fail;
            }
            if (e.StartsWith("计数:", System.StringComparison.Ordinal))
                return EvaluateCount(e, in ctx, out value, out reason);

            reason = $"未登记的取值表达式「{expr}」（请检查 ValueExpr 写法）";
            return ValueEvalResult.Fail;
        }

        private static ValueEvalResult EvaluateCount(string e, in ValueContext ctx, out int value, out string reason)
        {
            value = 0;
            reason = null;
            var parts = e.Split(':');
            // 计数:区域:墓地:角色:自己
            if (parts.Length >= 5 && parts[1] == "区域")
                return CountZone(parts[2], parts[3], parts[4], in ctx, out value, out reason);
            // 计数:卡上标志:时间标志:自身
            if (parts.Length >= 4 && parts[1] == "卡上标志")
                return CountCardCounter(parts[2], parts[3], in ctx, out value, out reason);
            // 计数:地区标志:自己:本地区
            if (parts.Length >= 4 && parts[1] == "地区标志")
                return CountAreaMark(parts[2], parts[3], in ctx, out value, out reason);
            // 【P3·F】计数:封印牌:自身 —— 被封印在该卡上的牌数
            if (parts.Length >= 3 && parts[1] == "封印牌")
                return CountSealedOnSelf(parts[2], in ctx, out value, out reason);

            reason = $"无法解析的计数表达式「{e}」";
            return ValueEvalResult.Fail;
        }

        private static ValueEvalResult CountZone(string zoneName, string category, string side, in ValueContext ctx,
            out int value, out string reason)
        {
            value = 0;
            reason = null;
            var zm = ctx.ZoneManager;
            if (zm == null) { reason = "ZoneManager 未注入，无法区域计数"; return ValueEvalResult.Fail; }
            if (ctx.Source == null) { reason = "来源卡为空，无法判定『自己/对手』"; return ValueEvalResult.Fail; }

            CardZone? zone = zoneName switch
            {
                "墓地" => CardZone.Graveyard,
                "手牌" => CardZone.Hand,
                "资产" or "资产区" => CardZone.AssetZone,
                "争夺区" => CardZone.AreaZone,
                "附属区" => CardZone.AttachedZone,
                "秘社" => CardZone.CouncilZone,
                "封印区" => CardZone.SealedZone,
                _ => null,
            };
            if (zone == null) { reason = $"未登记的区域名「{zoneName}」"; return ValueEvalResult.Fail; }

            int self = ctx.Source.ControllerId;
            var players = new System.Collections.Generic.List<int>();
            switch (side)
            {
                case "自己": players.Add(self); break;
                case "对手": players.Add(self == 1 ? 2 : 1); break;
                case "双方": players.Add(1); players.Add(2); break;
                default: reason = $"未登记的阵营「{side}」（自己/对手/双方）"; return ValueEvalResult.Fail;
            }

            foreach (var p in players)
                foreach (var c in zm.GetZoneCards(zone.Value, p))
                {
                    if (c == null || c.IsHidden) continue;
                    if (!MatchCategory(c, category)) continue;
                    value++;
                }
            return ValueEvalResult.Ok;
        }

        /// <summary>类别筛选：`角色/附属/法术/事务/暗藏者/任意`
        ///（`暗藏者` 按"当前是否背面朝上"计，与印刷类别无关；暗藏只存在于场上，离场即清除）。</summary>
        private static bool MatchCategory(CardInstance c, string category)
        {
            if (category == "任意" || string.IsNullOrEmpty(category)) return true;
            if (category == "暗藏者") return c.IsCovert;
            var flag = c.PlayerTemplate?.CategoryFlag;
            if (flag == null) return false;
            return category switch
            {
                "角色" => (flag.Value & CardCategory.Character) != 0,
                "附属" => (flag.Value & CardCategory.Attachment) != 0,
                "法术" or "事务" => (flag.Value & CardCategory.Sorcery) != 0,
                _ => false,
            };
        }

        /// <summary>
        /// 【P3·F】"其上封印牌"计数（`计数:封印牌:自身`）：被封印在**该卡**上的牌数（JC109 每张封印牌 → 黑刀）。
        /// </summary>
        private static ValueEvalResult CountSealedOnSelf(string subject, in ValueContext ctx,
            out int value, out string reason)
        {
            value = 0;
            reason = null;
            var card = subject == "目标" ? (ctx.Target ?? ctx.Source) : ctx.Source;
            if (card == null) { reason = "来源卡为空，无法计数其上封印牌"; return ValueEvalResult.Fail; }
            value = card.SealedCards?.Count ?? 0;
            return ValueEvalResult.Ok;
        }

        private static ValueEvalResult CountCardCounter(string marker, string subject, in ValueContext ctx,
            out int value, out string reason)
        {
            value = 0;
            reason = null;
            var card = subject == "目标" ? ctx.Target : ctx.Source;
            if (card == null) { reason = $"『{subject}』卡为空，无法计数标志"; return ValueEvalResult.Fail; }
            value = card.Counters != null && card.Counters.TryGetValue(marker, out var n) ? n : 0;
            return ValueEvalResult.Ok;
        }

        private static ValueEvalResult CountAreaMark(string side, string areaName, in ValueContext ctx,
            out int value, out string reason)
        {
            value = 0;
            reason = null;
            if (ctx.Source == null) { reason = "来源卡为空，无法判定『自己/对手』"; return ValueEvalResult.Fail; }
            var areaManager = ctx.AreaManager;
            if (areaManager == null) { reason = "AreaManager 未注入，无法地区标志计数"; return ValueEvalResult.Fail; }

            string areaUid = areaName == "目标地区"
                ? (ctx.TargetAreaId ?? "")
                : (ctx.Source.ZoneDetailId ?? "");
            if (string.IsNullOrEmpty(areaUid)) { reason = $"『{areaName}』推不出地区 UID"; return ValueEvalResult.Fail; }

            int self = ctx.Source.ControllerId;
            int player = side == "对手" ? (self == 1 ? 2 : 1) : self;
            if (side != "对手" && side != "自己") { reason = $"未登记的阵营「{side}」（自己/对手）"; return ValueEvalResult.Fail; }
            value = areaManager.GetPowerMark(areaUid, player);
            return ValueEvalResult.Ok;
        }
    }
}
