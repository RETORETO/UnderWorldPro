using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【联机客户端·快照应用队列】把"快照到达"与"应用时机"解耦，是客户端延迟掩盖/动画表现的基础设施：
    ///
    ///  - **合并（coalescing）**：同一帧内到达多份快照时只保留最后一份，避免无意义地多次全量重建 UI；
    ///  - **延后（deferred）**：表现层（动画/拖拽/本地选择窗口）进行中可置 <see cref="IsDeferred"/>，
    ///    暂缓应用但不丢失快照（"最多延后多久"由调用方控制，避免长期不同步）；
    ///  - **配对（pairing）**：出队时同时给出"上一份已应用快照"，供 <see cref="SnapshotDiff.Diff"/>
    ///    计算增量指令 → 作为表现层（动画）的事件源，无需服务端额外下发事件消息。
    ///
    /// 纪律：纯托管、无 Godot 依赖，可单测；不持有 UI/Manager 引用，只做快照的排队与配对。
    /// </summary>
    public class SnapshotApplicationQueue
    {
        private GameSnapshot _pending;
        private GameSnapshot _applied;

        /// <summary>是否处于延后窗口（如动画进行中/本地交互窗口中）——为 true 时 <see cref="TryDequeue"/> 不产出。</summary>
        public bool IsDeferred { get; set; }

        /// <summary>已应用的最新快照（尚未应用过任何快照时为 null）。</summary>
        public GameSnapshot Applied => _applied;

        /// <summary>是否有待应用快照。</summary>
        public bool HasPending => _pending != null;

        /// <summary>累计入队次数（含被合并掉的）。</summary>
        public int EnqueueCount { get; private set; }

        /// <summary>因合并而丢弃的旧待应用快照次数（观测用）。</summary>
        public int CoalescedCount { get; private set; }

        /// <summary>入队一份快照；若已存在待应用快照则覆盖（合并），并计入 <see cref="CoalescedCount"/>。</summary>
        public void Enqueue(GameSnapshot snap)
        {
            if (snap == null) return;
            if (_pending != null) CoalescedCount++;
            _pending = snap;
            EnqueueCount++;
        }

        /// <summary>
        /// 尝试取出待应用快照。返回 true 时：
        /// prev = 上一份已应用快照（首次应用为 null，供 Diff 视作"空初始态"），next = 本次要应用的快照。
        /// 处于延后窗口或无待应用快照时返回 false（快照保留在队列中）。
        /// </summary>
        public bool TryDequeue(out GameSnapshot prev, out GameSnapshot next)
        {
            prev = _applied;
            next = null;
            if (_pending == null || IsDeferred) return false;
            next = _pending;
            _pending = null;
            return true;
        }

        /// <summary>标记某份快照已应用（更新 <see cref="Applied"/>，作为下次 Diff 的基准）。</summary>
        public void MarkApplied(GameSnapshot snap) => _applied = snap;

        /// <summary>重置（换局/重连时调用）：清空待应用与基准快照、复位延后与统计。</summary>
        public void Reset()
        {
            _pending = null;
            _applied = null;
            IsDeferred = false;
            EnqueueCount = 0;
            CoalescedCount = 0;
        }
    }
}
