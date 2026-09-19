namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 完整性修复 H3】**服务端停止钩子**（可选实现）。
    ///
    /// 为什么单独一个接口而不是加进 `ILobbyHandler`：停止清理是"进程收尾"职责，与"处理玩家请求"不同层；
    /// 独立接口让测试替身（只关心 `Handle`/`OnSessionLeft` 的假实现）不必被迫实现它，也不破坏既有签名。
    ///
    /// 调用时机（`ServerHost.Stop()`）：**先**本钩子（关房 + 给在座客户端下发 `Closed`），**再**停传输——
    /// 顺序反了客户端就收不到"房间已关闭"的通知，只会看到连接无声消失。
    /// </summary>
    public interface ILobbyShutdown
    {
        /// <summary>服务端即将停止：关闭全部房间并通知在座会话（实现必须幂等）。</summary>
        void OnServerStopping(string reason);
    }
}
