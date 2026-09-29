using System;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块E】断线重连载荷（纯数据，可序列化）。
    /// 服务端重连应答：整局状态快照（含 RandomSnapshot + 挂起输入）+ 随机源快照。
    /// </summary>
    public class ReconnectPayload
    {
        /// <summary>整局状态快照（卡/区域/堆叠/玩家/阶段 + PendingInput + Random）。</summary>
        public GameSnapshot Snapshot;

        /// <summary>冗余字段：随机源快照（也可从 Snapshot.Random 读取；此处独立便于快速访问）。</summary>
        public RandomSnapshot Random;

        /// <summary>重连目标玩家编号（1/2）。</summary>
        public int ReconnectPlayerId;

        /// <summary>服务端是否仍持有该玩家的对局（false=对局已结束/不存在）。</summary>
        public bool HasGame;
    }

    /// <summary>
    /// 【C/S 化 P3·模块E】断线重连管理器（纯托管协调器，不依赖 Godot / Manager / UI）。
    ///
    /// 职责：串起"随机源复原 + 快照恢复 + 挂起输入恢复"三条重连链，供具体网络传输层接入：
    ///   - 服务端侧：BuildPayload() 用注入的"快照获取委托"取整局快照，并把它与随机源快照拼成 ReconnectPayload 下发；
    ///   - 客户端侧：ApplyPayload(payload) 把随机源复原到精确步数、把快照交给注入的"渲染应用委托"重建 UI，
    ///     并读出挂起输入（PendingInput）供客户端恢复"等待该玩家的决策"状态。
    ///
    /// 通过构造注入的两个委托（SnapshotProvider / SnapshotConsumer）解耦具体实现：
    ///   - 服务端装配：new ReconnectManager(() => gameInitializer.BuildSnapshot(), driver.ApplySnapshot)
    ///   - 本类不直接引用 Manager / Arena，保持 Net/ 根纯托管（Headless 服务端可用）。
    /// </summary>
    public class ReconnectManager
    {
        private readonly Func<GameSnapshot> _snapshotProvider;   // 服务端：获取当前整局快照
        private readonly Action<GameSnapshot> _snapshotConsumer; // 客户端：把快照应用到渲染
        private RandomSourceHub _serverRng;                      // 服务端：随机源（随快照导出状态）

        /// <param name="snapshotProvider">服务端取整局快照的委托（如 () => gameInitializer.BuildSnapshot()）。</param>
        /// <param name="snapshotConsumer">客户端把快照应用到渲染的委托（如 driver.ApplySnapshot）。</param>
        /// <param name="serverRng">服务端随机源 Hub（可选；提供时 BuildPayload 会把它注入快照的 Random）。</param>
        public ReconnectManager(Func<GameSnapshot> snapshotProvider, Action<GameSnapshot> snapshotConsumer,
                                RandomSourceHub serverRng = null)
        {
            _snapshotProvider = snapshotProvider;
            _snapshotConsumer = snapshotConsumer;
            _serverRng = serverRng;
        }

        /// <summary>服务端：构建重连应答载荷（注入随机源状态到快照 + 独立 Random 字段）。</summary>
        public ReconnectPayload BuildPayload(int reconnectPlayerId, bool hasGame)
        {
            var payload = new ReconnectPayload
            {
                ReconnectPlayerId = reconnectPlayerId,
                HasGame = hasGame,
            };
            if (!hasGame) return payload;

            var snap = _snapshotProvider?.Invoke();
            if (snap == null) return payload;

            // 注入随机源状态（服务端权威的随机中间态）
            if (_serverRng != null)
            {
                snap.Random = _serverRng.Snapshot();
                payload.Random = snap.Random;
            }
            payload.Snapshot = snap;
            return payload;
        }

        /// <summary>客户端：应用重连载荷，恢复随机源 + 快照渲染 + 挂起输入。返回挂起输入（若有）。</summary>
        public ChoiceRequest ApplyPayload(ReconnectPayload payload)
        {
            if (payload?.Snapshot == null) return null;

            // 1) 随机源复原：客户端后续随机与服务端精确对齐（此处由外部根据 payload.Random 重建其随机 Hub）
            //    —— RandomSourceHub.Restore 由调用方执行（本类不持有客户端随机 Hub，避免重复状态）。
            // 2) 快照渲染：客户端按快照重建 UI（增量由 `SnapshotDiff` 产出指令、`ArenaOl.RouteRenderCommands` 消费；
            //    重连场景下调用方也可直接全量重建。早先注释里的 `SnapshotRenderDriver` 已删除）
            _snapshotConsumer?.Invoke(payload.Snapshot);

            // 3) 挂起输入：若服务端正等某玩家决策，客户端据此恢复"等待响应"状态
            return payload.Snapshot.PendingInput;
        }

        /// <summary>便捷：客户端用 RandomSourceHub 按载荷中的随机快照复原（断线重连随机对齐）。</summary>
        public static void RestoreClientRng(RandomSourceHub clientHub, ReconnectPayload payload)
        {
            var snap = payload?.Random ?? payload?.Snapshot?.Random;
            if (snap != null) clientHub.Restore(snap);
        }
    }
}
