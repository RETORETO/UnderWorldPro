using System;
using System.Collections.Generic;

namespace Script.Game
{
    /// <summary>
    /// 【C/S 化 P3·模块E】可复原的确定性 PRNG（splitmix64）。
    ///
    /// 背景（断线重连 · 前置条件 ⑤）：原 SeededRandomSource 基于 System.Random，虽固定种子可复现
    /// 但**不记录已消耗步数**，服务端执行 N 次随机后无法从种子精确复原到中间态——断线重连时
    /// Client 无法与 Server 的后续随机对齐。
    ///
    /// 本实现改用 splitmix64（纯托管、快速、无内建状态泄漏的经典 64 位确定性 PRNG），并显式维护
    /// <see cref="Step"/> 消耗计数器。splitmix64 的每步输出仅依赖"当前状态"且是**可逆/可前进**的，
    /// 故给定 (Seed, Step) 即可用 <see cref="Restore(int, long)"/> 精确重建到任意中间态：
    ///   服务端：取走 N 次随机 → Step = N，随 GameSnapshot 下发 RandomSnapshot{Seed, Step}
    ///   客户端重连：Restore(Seed, Step) → 后续随机与服务端完全一致
    ///
    /// 纯 POCO、不依赖 Godot，可在 Headless 服务端 / 独立网络客户端运行。
    /// </summary>
    public class DeterministicRandom
    {
        private ulong _state;
        /// <summary>本流已消耗的随机步数。随快照序列化，供重连复原。</summary>
        public long Step { get; private set; }

        public int Seed { get; }

        /// <param name="seed">任意整数种子。</param>
        public DeterministicRandom(int seed)
        {
            Seed = seed;
            _state = SplitMix64((ulong)seed);
        }

        /// <summary>用种子 + 已消耗步数复原到中间态（断线重连关键）。</summary>
        public void Restore(int seed, long step)
        {
            _state = SplitMix64((ulong)seed);
            for (long i = 0; i < step; i++) NextRaw();
            Step = step;
        }

        /// <summary>返回 [0, maxExclusive) 随机整数。maxExclusive &lt;= 0 返回 0。</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) return 0;
            return (int)(NextRaw() % (ulong)maxExclusive);
        }

        /// <summary>返回 [minInclusive, maxExclusive) 随机整数。</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + Next(maxExclusive - minInclusive);
        }

        /// <summary>Fisher-Yates 原地洗牌，消耗 list.Count-1 步随机。</summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = (int)(NextRaw() % (ulong)(i + 1));
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>取出原始 64 位随机值并推进状态、累加步数。</summary>
        private ulong NextRaw()
        {
            Step++;
            return SplitMix64(_state);
        }

        /// <summary>splitmix64：一次状态混洗 + 输出（标准实现）。</summary>
        private static ulong SplitMix64(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
