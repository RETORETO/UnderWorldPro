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

        public UiNotificationBus()
        {
            Instance = this;
        }

        public void NotifyVisualDirty() => OnVisualDirty?.Invoke();

        public void NotifyCostModChanged() => CostModChanged?.Invoke();

        /// <summary>再来一局时清空订阅（UI 节点重建会重新订阅，避免旧 Arena 残留）。</summary>
        public void Clear()
        {
            OnVisualDirty = null;
            CostModChanged = null;
        }
    }
}
