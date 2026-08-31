using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块B】快照渲染驱动：把"客户端收到的新快照"与"已渲染快照"比对，
    /// 经 SnapshotDiff 得到增量指令，再逐条路由到 ISnapshotView sink 做 UI 应用。
    ///
    /// 职责：
    ///   - 持有"上次已渲染的快照"（_last），用于与每次新快照 Diff；
    ///   - 持有 ISnapshotView sink（由具体 UI 层实现，如 Arena），把指令翻译为 UI 操作；
    ///   - 提供 ApplySnapshot(next)：Diff 上次→本次，逐条调 sink，并更新 _last。
    ///
    /// 与现有单机事件驱动路径的关系（模块 B 增量路线）：
    ///   - 单机默认仍走 ZoneManager 事件总线 → SyncCardUIToZone（本地 Model 直读，行为不变）；
    ///   - 本驱动是"网络渲染模式"的可选路径：开启后客户端改由快照驱动刷新，二者可经开关切换。
    /// 本类纯托管，不依赖 Godot / Arena，逻辑可单测。
    /// </summary>
    public class SnapshotRenderDriver
    {
        private readonly ISnapshotView _view;
        private GameSnapshot _last;

        public SnapshotRenderDriver(ISnapshotView view) => _view = view;

        /// <summary>当前已渲染快照（供外部查询/重置）。</summary>
        public GameSnapshot LastApplied => _last;

        /// <summary>应用一个新到快照：Diff 上次→本次并路由增量指令到 sink。首帧（_last 为 null）视为全量 Add。</summary>
        public void ApplySnapshot(GameSnapshot next)
        {
            if (next == null) return;
            var cmds = SnapshotDiff.Diff(_last, next);
            foreach (var cmd in cmds) Route(cmd);
            _last = next;
        }

        /// <summary>强制以给定快照重建（清空上次缓存，走全量 Add；用于连上服务端首包）。</summary>
        public void RebuildFrom(GameSnapshot snap)
        {
            _last = null;
            ApplySnapshot(snap);
        }

        private void Route(RenderCommand cmd)
        {
            switch (cmd.Kind)
            {
                case RenderCommand.CmdKind.AddCard:
                    _view.AddCard((CardRenderCommand)cmd); break;
                case RenderCommand.CmdKind.RemoveCard:
                    _view.RemoveCard(((CardRenderCommand)cmd).Uid); break;
                case RenderCommand.CmdKind.MoveCard:
                    _view.MoveCard((CardRenderCommand)cmd); break;
                case RenderCommand.CmdKind.UpdateCard:
                    _view.UpdateCard((CardRenderCommand)cmd); break;
                case RenderCommand.CmdKind.StackPush:
                    _view.StackPush((StackRenderCommand)cmd); break;
                case RenderCommand.CmdKind.StackPop:
                    _view.StackPop(); break;
                case RenderCommand.CmdKind.PlayerUpdate:
                    _view.UpdatePlayer((PlayerRenderCommand)cmd); break;
                case RenderCommand.CmdKind.PhaseChange:
                    _view.UpdatePhase((PhaseRenderCommand)cmd); break;
            }
        }
    }

    /// <summary>
    /// 【C/S 化 P3·模块B】快照渲染 sink 抽象：渲染层（Arena / 未来独立网络客户端 UI）实现本接口，
    /// 把 SnapshotRenderDriver 路由来的增量指令应用到 UI。接口参数均为纯数据，不泄漏 Godot 类型。
    /// </summary>
    public interface ISnapshotView
    {
        void AddCard(CardRenderCommand cmd);
        void RemoveCard(string uid);
        void MoveCard(CardRenderCommand cmd);
        void UpdateCard(CardRenderCommand cmd);
        void StackPush(StackRenderCommand cmd);
        void StackPop();
        void UpdatePlayer(PlayerRenderCommand cmd);
        void UpdatePhase(PhaseRenderCommand cmd);
    }
}
