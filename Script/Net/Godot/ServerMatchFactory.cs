using System;
using System.Collections.Generic;
using Godot;
using Script.Manager;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】房间 → 对局宿主工厂（服务端侧）。
    ///
    /// 职责：为一间房创建**独立一套** `GameInitializer`（含双方卡组、初始地区），并包成 `MatchHost`。
    /// 关键点：
    ///  · **双方卡组来自房间提交**（`P1DeckOverride/P2DeckOverride`），不再读 UI 单例或硬编码；
    ///  · 每场一套 Manager（批次 2 已保证规则层不依赖进程级单例）→ 支持一个服务端进程多场并发；
    ///  · `owner` 可为 null（测试/无场景树场景）：为 null 时对局节点登记在本工厂内并由 `Destroy` 释放，
    ///    避免"节点无人回收"；非 null 时挂到 owner 之下（随场景树一起销毁）。
    /// </summary>
    public class ServerMatchFactory
    {
        private readonly Node _owner;              // 服务端入口节点（对局节点的父；可为 null）
        private readonly INetTransport _transport;
        private readonly Dictionary<string, GameInitializer> _games = new Dictionary<string, GameInitializer>();

        public ServerMatchFactory(Node owner, INetTransport transport)
        {
            _owner = owner;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>为房间创建对局宿主（返回 null 表示建局失败；异常会向上抛由房间层回退）。</summary>
        public IRoomMatchHost Create(RoomSession room)
        {
            if (room == null) return null;

            // 【M2】同房间若已有对局节点（D10 重开一局、或上次建局失败残留），先释放 —— 否则 `_games[roomId]`
            // 直接覆盖会让上一局的整棵 Manager/卡实例永远挂在场景树下（服务端长跑内存线性增长）。
            Destroy(room);

            var game = new GameInitializer { Name = $"Match_{room.RoomId}" };
            if (_owner != null && GodotObject.IsInstanceValid(_owner)) _owner.AddChild(game);

            // 双方卡组（房间内已通过校验；此处仍做空值保护）
            game.P1DeckOverride = room.Seat(1)?.Deck;
            game.P2DeckOverride = room.Seat(2)?.Deck;

            try
            {
                game.InitializeAll();
                game.StartMatch();

                // 初始地区（唯一真相源＝房间设置）
                int areaCount = room.Settings?.InitialAreaCount ?? 3;
                var initial = game.PlayerActionService?.SetupInitialAreas(areaCount);
                GD.Print($"[ServerMatchFactory] 房间 {room.RoomId} 建局完成：初始地区 {initial?.Count ?? 0} 个"
                    + $"（卡组：座位1={game.P1DeckOverride?.DeckName ?? "（默认）"} / 座位2={game.P2DeckOverride?.DeckName ?? "（默认）"}）");
            }
            catch
            {
                // 装配失败：先释放**已注册/已入树**的半成品节点，再把异常交给房间层做状态回退（H1）
                _games.Remove(room.RoomId);
                FreeGameNode(room.RoomId, game);
                throw;
            }

            _games[room.RoomId] = game;   // 成功后才登记（避免登记到半成品）

            var host = new MatchHost(room.RoomId, game, _transport);
            host.OnGameOver = winner => GD.Print($"[ServerMatchFactory] 房间 {room.RoomId} 对局结束，获胜座位 {winner}");
            return host;
        }

        /// <summary>释放某房间的对局节点（房间销毁/重开/建局失败回退时调用）。</summary>
        public void Destroy(RoomSession room)
        {
            if (room == null) return;
            if (!_games.TryGetValue(room.RoomId, out var node)) return;
            _games.Remove(room.RoomId);
            FreeGameNode(room.RoomId, node);
        }

        /// <summary>真正释放节点（入树则 RemoveChild+QueueFree，未入树则 Free）。</summary>
        private void FreeGameNode(string roomId, GameInitializer node)
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            if (_owner != null && GodotObject.IsInstanceValid(_owner) && node.GetParent() == _owner)
            {
                _owner.RemoveChild(node);
                node.QueueFree();
            }
            else
            {
                node.Free();   // 未入树：直接释放
            }
            GD.Print($"[ServerMatchFactory] 已释放房间 {roomId} 的对局节点");
        }
    }
}
