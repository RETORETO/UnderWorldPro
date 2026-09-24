namespace Script.Manager
{
    /// <summary>
    /// 【MVC 收口】UI 通知事件总线（实例单例，区别于 GameEventBus 的"触发领域"总线）。
    /// 承载 Controller 的"状态已变，请刷新"广播：OnVisualDirty（模型脏了）、CostModChanged（费用修正变了）。
    /// Controller 只广播，不关心谁订阅；View 订阅后自行刷新。取代原 EffectEngine 的 static 委托/事件，
    /// 使"通知"从"View 往 Controller 塞 static 委托"倒转为"Controller 广播、View 订阅"。
    /// </summary>
    public class UiNotificationBus
    {
        public static UiNotificationBus Instance { get; private set; }

        /// <summary>模型可见状态（横置等）已变，请求 UI 刷新。</summary>
        public event System.Action OnVisualDirty;

        /// <summary>费用修正栈已变（登记/消费/到期），请求 UI 刷新手牌减费提示。</summary>
        public event System.Action CostModChanged;

        /// <summary>
        /// 【P2】公开"展示"某张牌（如检索后展示该牌）：参数 = 被展示的卡、展示者玩家 id。
        /// 语义：**双方都看得到**（区别于"检视"仅自己可见）；服务端广播，客户端据此显示展示提示。
        /// 消费点：`EffectEngine` 的检索"展示"标记与 `ActionVerb.Reveal`。
        /// </summary>
        public event System.Action<Script.Game.Entities.CardInstance, int> CardRevealed;

        /// <summary>
        /// 【P3·H】"**检视**"某张牌（如 JC062 检视目标暗藏者）：参数 = 被检视的卡、**检视者**玩家 id。
        /// 语义与"展示"相对的**私密**行为：**只有检视者本人可见**（单机＝本机玩家；联机＝仅向该玩家下发该卡面）。
        /// 消费点：`EffectEngine` 的 `ActionVerb.Inspect`（单机）/ `ChoiceRequest.Kind.InspectCards`（联机）。
        /// </summary>
        public event System.Action<Script.Game.Entities.CardInstance, int> CardInspected;

        public UiNotificationBus()
        {
            Instance = this;
        }

        public void NotifyVisualDirty() => OnVisualDirty?.Invoke();

        public void NotifyCostModChanged() => CostModChanged?.Invoke();

        /// <summary>广播"某玩家公开展示了一张牌"（View 负责呈现；无订阅者时只留日志）。</summary>
        public void NotifyCardRevealed(Script.Game.Entities.CardInstance card, int byPlayerId)
            => CardRevealed?.Invoke(card, byPlayerId);

        /// <summary>广播"某玩家**检视**了一张牌"（私密：只有该玩家本人应看到；无订阅者时只留日志）。</summary>
        public void NotifyCardInspected(Script.Game.Entities.CardInstance card, int inspectorPlayerId)
            => CardInspected?.Invoke(card, inspectorPlayerId);

        /// <summary>再来一局时清空订阅（UI 节点重建会重新订阅，避免旧 Arena 残留）。</summary>
        public void Clear()
        {
            OnVisualDirty = null;
            CostModChanged = null;
            CardRevealed = null;
            CardInspected = null;
        }
    }
}
