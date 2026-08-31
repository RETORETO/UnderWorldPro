using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块B】快照差异引擎：对比前后两个 GameSnapshot，产出增量 RenderCommand 列表。
    ///
    /// 这是"客户端消费快照而非本地 Model"的核心算法：
    /// 服务端权威模式下，服务端把全量快照经网络下发；客户端用本引擎对比"上次渲染的快照"与
    /// "新到的快照"，得到最小增量指令集，再交给渲染层（SnapshotRenderDriver → ISnapshotView）
    /// 增量刷新 UI，避免全量重建。
    ///
    /// 纯函数、纯托管、无状态：输入两个快照，输出指令列表。可序列化、可单测，Headless / 独立
    /// 客户端进程均可复用，不依赖 Godot 或具体 UI。
    /// </summary>
    public static class SnapshotDiff
    {
        /// <summary>对比前快照 prev 与后快照 next，产出增量指令。prev 传 null 视为"空初始态"（全部为 Add）。</summary>
        public static List<RenderCommand> Diff(GameSnapshot prev, GameSnapshot next)
        {
            var cmds = new List<RenderCommand>();
            if (next == null) return cmds;

            // ---------- 卡牌：按 UID 求差集（增/删/移动/属性更新） ----------
            var prevByUid = new Dictionary<string, ZoneCardRef>();
            if (prev != null)
                foreach (var c in prev.Zones) prevByUid[c.Uid] = c;
            var nextByUid = new Dictionary<string, ZoneCardRef>();
            foreach (var c in next.Zones) nextByUid[c.Uid] = c;

            // 新增卡
            foreach (var c in next.Zones)
                if (!prevByUid.ContainsKey(c.Uid))
                    cmds.Add(BuildCard(RenderCommand.CmdKind.AddCard, c));

            // 移除卡
            if (prev != null)
                foreach (var c in prev.Zones)
                    if (!nextByUid.ContainsKey(c.Uid))
                        cmds.Add(new CardRenderCommand { Kind = RenderCommand.CmdKind.RemoveCard, Uid = c.Uid });

            // 同 UID 存在：比较位置/属性
            foreach (var c in next.Zones)
            {
                if (!prevByUid.TryGetValue(c.Uid, out var p)) continue; // 已是新增
                if (Moved(p, c))
                    cmds.Add(BuildCard(RenderCommand.CmdKind.MoveCard, c));
                else if (Changed(p, c))
                    cmds.Add(BuildCard(RenderCommand.CmdKind.UpdateCard, c));
            }

            // ---------- 堆叠：按条目内容逐条比对（新增=Push，减少=Pop） ----------
            var nextStack = next.Stack ?? new List<StackEntrySnap>();
            var prevStack = prev?.Stack ?? new List<StackEntrySnap>();
            if (nextStack.Count > prevStack.Count)
            {
                // 简单近似：尾部新增条目视为 Push（堆叠后进先出，新条目总是压顶）
                for (int i = prevStack.Count; i < nextStack.Count; i++)
                    cmds.Add(BuildStack(RenderCommand.CmdKind.StackPush, nextStack[i]));
            }
            else if (nextStack.Count < prevStack.Count)
            {
                for (int i = 0; i < prevStack.Count - nextStack.Count; i++)
                    cmds.Add(new StackRenderCommand { Kind = RenderCommand.CmdKind.StackPop });
            }

            // ---------- 玩家级状态 ----------
            if (next.Players != null)
                foreach (var np in next.Players)
                {
                    PlayerSnapshot pp = null;
                    if (prev?.Players != null)
                        pp = prev.Players.Find(x => x.PlayerId == np.PlayerId);
                    if (pp == null || pp.AvailableMana != np.AvailableMana || pp.StorybookCount != np.StorybookCount || pp.IsGameOver != np.IsGameOver)
                        cmds.Add(new PlayerRenderCommand
                        {
                            Kind = RenderCommand.CmdKind.PlayerUpdate,
                            PlayerId = np.PlayerId,
                            AvailableMana = np.AvailableMana,
                            StorybookCount = np.StorybookCount,
                            IsGameOver = np.IsGameOver,
                        });
                }

            // ---------- 回合/优先权/阶段 ----------
            if (prev == null
                || prev.TurnCount != next.TurnCount
                || prev.FirstPlayerId != next.FirstPlayerId
                || prev.PriorityPlayerId != next.PriorityPlayerId
                || prev.CurrentPhase != next.CurrentPhase)
            {
                cmds.Add(new PhaseRenderCommand
                {
                    Kind = RenderCommand.CmdKind.PhaseChange,
                    TurnCount = next.TurnCount,
                    FirstPlayerId = next.FirstPlayerId,
                    PriorityPlayerId = next.PriorityPlayerId,
                    CurrentPhase = next.CurrentPhase,
                });
            }

            return cmds;
        }

        private static bool Moved(ZoneCardRef p, ZoneCardRef n)
            => p.Zone != n.Zone || p.ZoneOwnerId != n.ZoneOwnerId || p.ZoneDetailId != n.ZoneDetailId;

        private static bool Changed(ZoneCardRef p, ZoneCardRef n)
            => p.IsCovert != n.IsCovert || p.IsTapped != n.IsTapped
            || p.IsAttached != n.IsAttached || p.IsSealed != n.IsSealed
            || p.Damage != n.Damage || p.Trauma != n.Trauma
            || p.CurrentInvestigation != n.CurrentInvestigation
            || p.CurrentStrength != n.CurrentStrength
            || p.CurrentPower != n.CurrentPower || p.CurrentDefense != n.CurrentDefense;

        private static CardRenderCommand BuildCard(RenderCommand.CmdKind kind, ZoneCardRef c) => new CardRenderCommand
        {
            Kind = kind,
            Uid = c.Uid,
            Zone = c.Zone,
            ZoneOwnerId = c.ZoneOwnerId,
            ZoneDetailId = c.ZoneDetailId,
            ControllerId = c.ControllerId,
            IsCovert = c.IsCovert,
            IsTapped = c.IsTapped,
            IsAttached = c.IsAttached,
            IsSealed = c.IsSealed,
            Damage = c.Damage,
            Trauma = c.Trauma,
            CurrentInvestigation = c.CurrentInvestigation,
            CurrentStrength = c.CurrentStrength,
            CurrentPower = c.CurrentPower,
            CurrentDefense = c.CurrentDefense,
            MagicDomains = c.MagicDomains,
        };

        private static StackRenderCommand BuildStack(RenderCommand.CmdKind kind, StackEntrySnap s) => new StackRenderCommand
        {
            Kind = kind,
            OwnerId = s.OwnerId,
            SourceUid = s.SourceUid,
            EffectText = s.EffectText,
        };
    }
}
