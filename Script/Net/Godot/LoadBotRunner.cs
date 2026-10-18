using System;
using System.Collections.Generic;
using Godot;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【容量压测 · 2026-10-17】**进程内批量负载驱动**：一个进程内拉起 N 对 BOT（每对 = 1 host + 1 join），
    /// 目标是造出 N 间并发房，用于实测"单台服务器能同时承载多少局"。
    ///
    /// 为什么不用"N×2 个 Godot 进程"：
    ///   每个 Godot 进程约 150~250 MB，16 个就是 3~4 GB，而且会与服务端抢 CPU / 抢磁盘写日志，
    ///   把被测指标（服务端 RSS / CPU）彻底污染掉。进程内 16 个 `ClientSession` 只占约 250 MB。
    ///
    /// 为什么把 BOT 的 Verbose 关掉：
    ///   工程 `project.godot:35-36` 开了文件日志（`user://logs/uwpro.log`）。16 个 BOT 每收到一次房间状态就打一行，
    ///   会把磁盘 I/O 变成瓶颈，测出来的 CPU 曲线毫无意义。所以本类默认 `Verbose = false`；
    ///   只在"多久该让过"之类的汇总层面每 10 秒打一行。
    ///
    /// 用法（由 `NetMatchEntry` 解析 `--load` 后实例化，并每帧 `Poll`）：
    ///   godot --headless --path &lt;工程&gt; res://Scenes/Net/NetEntry.tscn -- \
    ///     --load 4 127.0.0.1 18950 [--load-interval 500]
    ///   → 造 4 间房 / 8 个连接。
    ///
    /// 观察口：每 10 秒打一行 `[LOAD] t=..s 连接=.. 对局中=..房 让过=.. 卡住=..`，
    /// 压测脚本据此判断"这一级有没有真的凑满 N 间房"。
    /// </summary>
    public class LoadBotRunner
    {
        private readonly List<NetBotClient> _bots = new List<NetBotClient>();
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly string _host;
        private readonly ushort _port;
        private readonly int _pairs;
        private readonly double _passIntervalSeconds;
        private readonly string _deckPath;

        private double _elapsed;
        private double _reportTimer;
        private int _lastPassSum;
        /// <summary>已停止（`Stop()` 幂等）。</summary>
        private bool _stopped;

        /// <summary>要造的并发房数。</summary>
        public int Pairs => _pairs;

        /// <summary>已建立的会话数（= 2 × Pairs，若全部连接成功）。</summary>
        public int SessionCount => _sessions.Count;

        /// <summary>当前真正处于"对局中"的不同房间数（这才是压测要的"并发局数"）。</summary>
        public int RoomsInMatch
        {
            get
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < _bots.Count; i++)
                {
                    var b = _bots[i];
                    if (b.InMatch && !string.IsNullOrEmpty(b.RoomId)) set.Add(b.RoomId);
                }
                return set.Count;
            }
        }

        /// <summary>处于对局中的 BOT 数（正常应为 2 × RoomsInMatch）。</summary>
        public int BotsInMatch
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _bots.Count; i++) if (_bots[i].InMatch) n++;
                return n;
            }
        }

        /// <summary>已连上服务端的会话数。</summary>
        public int BotsConnected
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _sessions.Count; i++) if (_sessions[i].IsConnected) n++;
                return n;
            }
        }

        /// <summary>已放弃重试的 BOT 数（连续失败过多，通常等于服务端连接/房间上限打满）。</summary>
        public int GaveUpCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _bots.Count; i++) if (_bots[i].GaveUp) n++;
                return n;
            }
        }

        /// <summary>累计「让过」次数（衡量真实产生了多少对局流量）。</summary>
        public int TotalPasses
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _bots.Count; i++) n += _bots[i].PassCount;
                return n;
            }
        }

        /// <summary>累计收到的对局快照数（每次下发约 103 KB 下行）。</summary>
        public int TotalSnapshots
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _bots.Count; i++) n += _bots[i].SnapshotCount;
                return n;
            }
        }

        public LoadBotRunner(string host, ushort port, int pairs, double passIntervalSeconds, string deckPath)
        {
            _host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            _port = port == 0 ? (ushort)18950 : port;
            _pairs = pairs < 1 ? 1 : pairs;
            _passIntervalSeconds = passIntervalSeconds > 0 ? passIntervalSeconds : 3.0;
            _deckPath = string.IsNullOrEmpty(deckPath) ? "res://Decks/default_deck.json" : deckPath;
        }

        /// <summary>
        /// 建立全部会话。
        /// 【注意】不在这里做"错峰启动"：每个 BOT 自身有 0.5 秒节流的"列房失败就重试"逻辑，
        /// join BOT 会一直重试直到 host 把房间建出来，因此同时拉起是安全的。
        /// 但**服务端 `--max-conns` 必须 ≥ 2 × pairs**，否则后连的直接被 `ServerFull` 拒掉。
        /// </summary>
        public void Start()
        {
            GD.Print($"[LOAD] 开始批量负载：{_pairs} 对 BOT（{_pairs * 2} 个连接）→ {_host}:{_port}，"
                + $"让过间隔 {_passIntervalSeconds:0.###}s，卡组 {_deckPath}");

            for (int i = 1; i <= _pairs; i++)
            {
                // host：建房者
                var hostSession = ClientSession.Connect(_host, _port, $"H{i}");
                var hostBot = new NetBotClient(hostSession, true, _deckPath, _passIntervalSeconds)
                {
                    Verbose = false,
                    Tag = $"H{i}",
                };
                _sessions.Add(hostSession);
                _bots.Add(hostBot);

                // join：加入者
                var joinSession = ClientSession.Connect(_host, _port, $"J{i}");
                var joinBot = new NetBotClient(joinSession, false, _deckPath, _passIntervalSeconds)
                {
                    Verbose = false,
                    Tag = $"J{i}",
                };
                _sessions.Add(joinSession);
                _bots.Add(joinBot);
            }

            GD.Print($"[LOAD] 已创建 {_sessions.Count} 个会话；等待服务端分配房间…");
        }

        /// <summary>
        /// 释放全部会话与其客户端对局视图。
        /// 【2026-10-17】每个会话懒创建 `ClientMatchView`，它内部会 `new` 9 个投影 Manager Node（不入树）。
        /// 不释放就会在进程退出时报 `ObjectDB instances were leaked`（`--load 8` 时为 16 × 9 = 144）。
        /// 顶点：`NetMatchEntry` 在 `Quit` 前、以及 `_ExitTree` 里都会调本方法。
        /// </summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            int n = _sessions.Count;
            for (int i = 0; i < _sessions.Count; i++)
            {
                try { _sessions[i]?.Dispose(); }
                catch (Exception e) { GD.PrintErr($"[LOAD] 释放会话异常（已忽略）：{e.GetType().Name} {e.Message}"); }
            }
            _sessions.Clear();
            _bots.Clear();
            GD.Print($"[LOAD] 已释放 {n} 个会话与客户端投影");
        }

        /// <summary>每帧驱动（由 `NetMatchEntry._Process` 调用）。</summary>
        public void Poll(double delta)
        {
            _elapsed += delta;
            for (int i = 0; i < _bots.Count; i++)
            {
                try { _bots[i].Poll(delta); }
                catch (Exception e) { GD.PrintErr($"[LOAD] BOT 轮询异常（已忽略）：{e.GetType().Name} {e.Message}"); }
            }

            _reportTimer += delta;
            if (_reportTimer < 10.0) return;
            _reportTimer = 0;

            int passes = TotalPasses;
            int deltaPasses = passes - _lastPassSum;
            _lastPassSum = passes;

            GD.Print($"[LOAD] t={_elapsed:0}s 连接={BotsConnected}/{_sessions.Count}"
                + $" 对局中={RoomsInMatch}房/{BotsInMatch}人"
                + $" 让过={passes}(+{deltaPasses}/10s) 快照={TotalSnapshots} 放弃={GaveUpCount}");
        }

        /// <summary>打印一次终态摘要（压测脚本收尾时用）。</summary>
        public void DumpSummary()
        {
            // 机器可读一行（压测脚本按这行解析，不要去匹配下面的中文标签）
            GD.Print($"[LOAD-SUMMARY] pairs={_pairs} rooms={RoomsInMatch} bots_in_match={BotsInMatch}"
                + $" connected={BotsConnected} sessions={_sessions.Count} passes={TotalPasses}"
                + $" snapshots={TotalSnapshots} gaveup={GaveUpCount}");

            GD.Print("[LOAD] ===== 终态摘要 =====");
            GD.Print($"[LOAD] 目标并行房数       : {_pairs}");
            GD.Print($"[LOAD] 实际并行房数       : {RoomsInMatch}");
            GD.Print($"[LOAD] 对局中 BOT 数      : {BotsInMatch} / {_sessions.Count}");
            GD.Print($"[LOAD] 已连接会话数       : {BotsConnected} / {_sessions.Count}");
            GD.Print($"[LOAD] 累计让过            : {TotalPasses}");
            GD.Print($"[LOAD] 累计收到快照        : {TotalSnapshots}");
            GD.Print($"[LOAD] 放弃重试的 BOT      : {GaveUpCount}");

            for (int i = 0; i < _bots.Count; i++)
            {
                var b = _bots[i];
                GD.Print($"[LOAD]   机器人 {b.Tag,-4} 房间={b.RoomId ?? "-",-6} 阶段={b.LastStage,-22} 让过={b.PassCount}");
            }
        }
    }
}
