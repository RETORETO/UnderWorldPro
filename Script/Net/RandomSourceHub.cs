using System.Collections.Generic;
using Script.Game;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块E】随机源快照（纯数据，可序列化）。断线重连时随 GameSnapshot 下发。
    ///
    /// 记录 MasterSeed 与各子流已消耗步数。服务端：RandomSourceHub.Snapshot() 产出本对象；
    /// 客户端重连：RandomSourceHub.Restore(snap) 精确复原所有子流到中间态，后续随机与服务端一致。
    /// </summary>
    public class RandomSnapshot
    {
        public int MasterSeed;
        public Dictionary<string, long> StreamSteps = new Dictionary<string, long>();
    }

    /// <summary>
    /// 【C/S 化 P3·模块E】随机源中心（RandomSourceHub）：管理整局全部确定性随机子流。
    ///
    /// 背景（前置条件 ⑤）：断线重连要"服务端执行 N 次随机后，Client 重连精确复原到中间态"。
    /// 原 SeededRandomSource 不记录步数、System.Random 无法复原中间态；本 Hub 用 DeterministicRandom
    /// （splitmix64 + Step 计数），并按子流（洗牌流 / UID 流 / 效果流等）隔离步数，互不干扰。
    ///
    /// 用法：
    ///   - 初始化：new RandomSourceHub(masterSeed)
    ///   - 取流：hub.GetStream("shuffle") 返回 DeterministicRandom（各流种子由 MasterSeed + 流名派生）
    ///   - 快照导出：hub.Snapshot() → RandomSnapshot
    ///   - 复原：hub.Restore(snap) → 精确回到快照时的步数
    ///
    /// 纯托管、可序列化，不依赖 Godot，可在 Headless 服务端运行。Manager 层可经 GameInitializer
    /// 注入本 Hub 作为统一随机入口（替代/并行于现有 SeededRandomSource）。
    /// </summary>
    public class RandomSourceHub
    {
        private readonly int _masterSeed;
        private readonly Dictionary<string, DeterministicRandom> _streams = new Dictionary<string, DeterministicRandom>();

        public int MasterSeed => _masterSeed;

        public RandomSourceHub(int? masterSeed = null)
        {
            _masterSeed = masterSeed ?? new System.Random().Next();
        }

        /// <summary>取（或惰性创建）指定子流。各流用 MasterSeed 与流名派生独立种子，互不串扰。</summary>
        public DeterministicRandom GetStream(string name)
        {
            if (!_streams.TryGetValue(name, out var rng))
            {
                int streamSeed = DeriveSeed(_masterSeed, name);
                rng = new DeterministicRandom(streamSeed);
                _streams[name] = rng;
            }
            return rng;
        }

        /// <summary>导出当前随机状态（MasterSeed + 各流步数），用于下发/持久化。</summary>
        public RandomSnapshot Snapshot()
        {
            var snap = new RandomSnapshot { MasterSeed = _masterSeed };
            foreach (var kv in _streams)
                snap.StreamSteps[kv.Key] = kv.Value.Step;
            return snap;
        }

        /// <summary>按快照精确复原所有子流（断线重连关键）。重建各流并 Restore 到快照步数。</summary>
        public void Restore(RandomSnapshot snap)
        {
            if (snap == null) return;
            _streams.Clear();
            foreach (var kv in snap.StreamSteps)
            {
                int streamSeed = DeriveSeed(snap.MasterSeed, kv.Key);
                var rng = new DeterministicRandom(streamSeed);
                rng.Restore(streamSeed, kv.Value);
                _streams[kv.Key] = rng;
            }
        }

        /// <summary>由 MasterSeed + 流名派生子流种子（稳定哈希）。</summary>
        private static int DeriveSeed(int master, string name)
        {
            ulong h = 0x9E3779B97F4A7C15UL ^ (ulong)(uint)master;
            foreach (char c in name)
            {
                h ^= (ulong)c;
                h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 29;
            }
            return (int)(h & 0x7FFFFFFF);
        }
    }
}
