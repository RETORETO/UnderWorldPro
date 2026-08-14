using System;
using System.Collections.Generic;

namespace Script.Game
{
    /// <summary>
    /// 随机源抽象。所有涉及随机的游戏逻辑（洗牌、随机弃牌、随机选目标等）
    /// 一律通过本接口取随机，禁止在任何位置直接 new System.Random()。
    ///
    /// 目的：
    ///   1) 可复现——固定种子后整局牌序完全一致，便于复现依赖执行顺序的 bug；
    ///   2) 可隔离——每局对战各持一个实例，多房间互不干扰；
    ///   3) 可同步——未来 C/S 架构下由服务端下发种子，两端结果一致。
    ///
    /// 注意：本接口及其实现均为纯 POCO，不依赖 Godot，便于未来在无引擎的服务端运行。
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>返回 [0, maxExclusive) 区间内的随机整数。</summary>
        int Next(int maxExclusive);

        /// <summary>返回 [minInclusive, maxExclusive) 区间内的随机整数。</summary>
        int Next(int minInclusive, int maxExclusive);

        /// <summary>对列表做原地洗牌（Fisher-Yates）。</summary>
        void Shuffle<T>(IList<T> list);
    }

    /// <summary>
    /// 基于种子的随机源实现。
    ///
    /// 用法：
    ///   var rng = new SeededRandomSource(12345);  // 调试期固定种子，牌序可复现
    ///   var rng = new SeededRandomSource();       // 正式运行自动生成种子
    ///
    /// 无论是否显式指定种子，实际使用的种子都会记录在 <see cref="Seed"/> 中。
    /// 务必在开局时打印它——线上出现异常牌序时，凭该值即可在本地精确重放整局。
    /// </summary>
    public class SeededRandomSource : IRandomSource
    {
        private readonly Random _rng;

        /// <summary>本局实际使用的随机种子。开局请打印并记录此值以便复现。</summary>
        public int Seed { get; }

        /// <param name="seed">指定种子；传 null 则自动生成一个并记录。</param>
        public SeededRandomSource(int? seed = null)
        {
            // 未指定时用 Guid 派生种子：比 Environment.TickCount 更不容易在
            // 短时间内连续创建多个实例时撞种子。
            Seed = seed ?? Guid.NewGuid().GetHashCode();
            _rng = new Random(Seed);
        }

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) return 0;
            return _rng.Next(maxExclusive);
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return _rng.Next(minInclusive, maxExclusive);
        }

        /// <summary>
        /// Fisher-Yates 原地洗牌。从末尾向前遍历，每次与 [0, i] 内随机位置交换，
        /// 保证每种排列出现概率相等。
        /// </summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
