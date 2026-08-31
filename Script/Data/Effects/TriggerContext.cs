using Script.Game.Entities;

namespace Script.Data.Effects
{
    /// <summary>
    /// 触发事件的上下文载体：业务点 Emit(GameEvent, TriggerContext) 时携带，
    /// 供 TriggerRegistry 做条件判定与构建 StackEffectInstance 使用。
    /// 新增时机若需新上下文，直接在此类追加属性即可，不影响旧字段。
    ///
    /// 字段语义约定（务必遵守，否则条件判定会取错卡）：
    ///  - ContextCard = "事件的宾语/被作用对象"，即触发条件要检查的那张卡。
    ///    例：OnCardPlayed → 刚被打出的卡；EnterTrigger → 进场卡结附/指定的目标卡（可为 null）。
    ///  - SourceCard  = "事件的主语/引发者"，即引发该事件的卡本身。
    ///    例：EnterTrigger → 刚进场的那张卡；OnCardDied → 死亡的那张卡。
    ///  - 反应类事件（OnCardPlayed）二者可同为"被打出的卡"：它既是引发者也是被检查对象。
    /// </summary>
    public class TriggerContext
    {
        /// <summary>触发上下文卡（事件宾语，如"刚被打出的卡"，供反应触发条件判定 PlayedSorcery 等）。</summary>
        public CardInstance ContextCard { get; set; }

        /// <summary>触发来源卡（事件主语/引发者，如进场触发的"刚进场的卡"、死亡触发的"死亡卡"）。</summary>
        public CardInstance SourceCard { get; set; }

        /// <summary>目标地区 UID（如赢区触发的"被赢取地区"、进场触发的"进入的地区"）。</summary>
        public string TargetAreaId { get; set; }

        /// <summary>
        /// 引发该事件的玩家 Id（如打出卡的玩家、回合归属玩家、赢取地区的玩家）。
        /// 约定：-1 表示"无归属/系统事件"（如 ManeuverTrigger 这类不由单个玩家引发的窗口事件）。
        /// 供触发条件做敌我判定（如"当对手打出…"）使用。
        /// </summary>
        public int TriggeringPlayerId { get; set; } = NoPlayer;

        /// <summary>TriggeringPlayerId 的"无归属"哨兵值。</summary>
        public const int NoPlayer = -1;

        /// <summary>是否存在明确的引发玩家。</summary>
        public bool HasTriggeringPlayer => TriggeringPlayerId != NoPlayer;

        /// <summary>附加整数参数（如回合号），扩展用。</summary>
        public int ExtraInt { get; set; }

        /// <summary>
        /// 条件判定用的"被检查对象"：优先 ContextCard，缺省回落到 SourceCard。
        /// 用于 EnterTrigger 这类"无结附目标时 ContextCard 为 null"的场景，
        /// 使条件谓词仍能拿到进场卡本身而非 null。
        /// </summary>
        public CardInstance EffectiveContextCard => ContextCard ?? SourceCard;

        public override string ToString()
            => $"TriggerContext[ctx={ContextCard?.PlayerTemplate?.CardName ?? "null"}, "
             + $"src={SourceCard?.PlayerTemplate?.CardName ?? "null"}, "
             + $"area={TargetAreaId ?? "null"}, byP{TriggeringPlayerId}, extra={ExtraInt}]";
    }
}
