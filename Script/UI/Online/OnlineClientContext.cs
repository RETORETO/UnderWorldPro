using Godot;
using Script.Net.Godot;

namespace Script.UI.Online
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 B】在线客户端上下文：**一个客户端进程只有一条到服务端的连接**，
    /// 大厅页 → 房间页 → 战场页共用同一条 `ClientSession`。
    ///
    /// 为什么用进程级静态持有：连接是进程级资源（与 `GameManager` 的应用级状态同层），
    /// 各页面之间传递 session 参数会污染场景构造约定（`GameManager.GoToState` 只按 enum 切场景，无法传参）。
    /// 因此这里只做"当前会话的持有/释放"，不含任何业务规则。
    /// </summary>
    public static class OnlineClientContext
    {
        private static ClientSession _session;

        /// <summary>当前会话（未连接时为 null）。</summary>
        public static ClientSession Session => _session;

        public static bool HasSession => _session != null;

        /// <summary>最近一次使用的服务端地址/端口/昵称（用于返回大厅时回填输入框）。</summary>
        public static string LastHost = "127.0.0.1";
        public static ushort LastPort = 18950;
        public static string LastName = "玩家";

        /// <summary>
        /// 进入大厅页后**自动连接**（由 `NetEntry --lobby` 置位）。仅影响"命令行直接进大厅"的场景，
        /// 主菜单点进来的流程保持手动点「连接」。
        /// </summary>
        public static bool AutoConnect;

        /// <summary>
        /// **联机 UI 冒烟模式**（开发用，`NetEntry --lobby … --smoke` 置位）：
        /// 大厅页/房间页在 `_Process` 中自动走一遍"创建或加入房间 → 提交卡组 → 准备 →（房主）开始"，
        /// 走的是**与按钮完全相同**的处理函数，用于在无显示器环境下验证 UI 代码路径；
        /// 正式玩法下恒为 false，不影响任何交互。
        /// </summary>
        public static bool SmokeMode;

        /// <summary>冒烟角色：true=房主（创建房间）；false=加入者。</summary>
        public static bool SmokeIsHost = true;

        /// <summary>
        /// 【建立主机 · 增量 D】**一次性**"主机自动流程"标志：房主点首页「建立主机」时置位，
        /// 大厅页在 `_Ready` 里**消费**它（见 `ConsumeHostAutoFlow`）→ 自动连接本机内嵌服务端 → Hello 应答后自动创建房间。
        ///
        /// 为什么必须一次性：房主点「离开房间」会回到大厅页；若标志常驻，会被立刻重新拖进房间（等于无法离开）。
        /// 与既有语义的关系：本标志**不改动** `AutoConnect` / `SmokeMode`，`--lobby` 与冒烟流程行为完全不变。
        /// </summary>
        public static bool HostAutoFlow;

        /// <summary>消费"主机自动流程"标志（读取并清零；只会返回一次 true）。</summary>
        public static bool ConsumeHostAutoFlow()
        {
            bool v = HostAutoFlow;
            HostAutoFlow = false;
            return v;
        }

        /// <summary>接管一个已连接/正在连接的会话（大厅页调用）。</summary>
        public static void SetSession(ClientSession session) => _session = session;

        /// <summary>取会话（不建立）；返回 null 表示当前没有连接。</summary>
        public static ClientSession Take() => _session;

        /// <summary>断开并释放会话（离开大厅/退出房间/回主菜单时调用）。</summary>
        public static void Disconnect()
        {
            try { _session?.Disconnect(); } catch { /* 已断开则忽略 */ }
            _session = null;
        }
    }
}
