using System.Collections.Generic;
using System.Linq;
using Godot;
using Script.IO;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10 修复·单人模式"看不到起始手牌 / PM draw 无 UI 变化"】根因回归。
    ///
    /// 故障链：
    ///   `Arena._Ready → GameInitializer.InitializeAll()` 装载卡组（**第 1 批** CardInstance）
    ///   → `Arena.InitializeDeckAndGraveyard()` + `BuildCardUIMap()` 按这**同一批**实例建卡 UI
    ///   → `Arena._Ready → StartMatch() → DealOpeningHands()` 里再装载一次（**第 2 批**实例，UD 全新）
    ///   ⇒ 逻辑层的"牌库→手牌"移动在 `Arena.SyncCardUIToZone` 按 UID 查不到卡 UI（静默 return）
    ///   ⇒ 手牌区永远空白、牌库 UI 永远不减（PM `draw` 同理）。
    ///
    /// 本套件锁定两条契约：
    ///  ① 发牌必须沿用 `InitializeAll` 结束时那批实例（＝ View 绑定的实例集），来源未变时**不得**重装；
    ///  ② 来源变化（`InitializeAll` 之后才赋 `P1DeckOverride`/`P2DeckOverride`）仍必须照常重装并生效；
    ///  ③ 整局重开（`ResetMatch`）仍必须强制重建整批实例（`CardManager.Reset` 已把 `_nextUID` 归 1）。
    /// </summary>
    public class DeckInstanceBindingTests : TestRunner
    {
        private const string Jc001Path = "res://Cards/Data/PlayerCards/JC/JC001_tonglingmaotouying.tres";

        // ==================== ① 发牌沿用 View 绑定实例 ====================

        /// <summary>
        /// 起手牌（以及发牌后剩余的牌库）必须仍属于 `InitializeAll` 结束时的那批实例 ——
        /// 那批实例正是 View 建卡 UI / 登记 `_cardUIMap` 的依据。换批 ⇒ 手牌无 UI。
        /// </summary>
        public void Test开局发牌必须沿用View已绑定的同一批实例()
        {
            var game = NewGame();
            var bound = new HashSet<string>(game.DeckManager.GetPlayer1DeckData().Select(c => c.UID));
            var bound2 = new HashSet<string>(game.DeckManager.GetPlayer2DeckData().Select(c => c.UID));
            Assert.IsTrue(bound.Count > 0 && bound2.Count > 0,
                "前置：InitializeAll 后双方牌库都应有实例（View 正是按这批实例建卡 UI）");

            game.StartMatch();

            foreach (int pid in new[] { 1, 2 })
            {
                var seen = pid == 1 ? bound : bound2;
                var hand = game.CardManager.GetHandCardsByOwner(pid);
                Assert.IsTrue(hand.Count > 0, $"前置：玩家{pid} 开局应有起始手牌");
                foreach (var c in hand)
                    Assert.IsTrue(seen.Contains(c.UID),
                        $"玩家{pid} 起手牌 {c.PlayerTemplate?.CardName}({c.UID}) 不属于 View 绑定的实例集" +
                        " ⇒ Arena.SyncCardUIToZone 查不到卡 UI，表现＝看不到起始手牌");

                // 牌库剩余部分同样必须留在原实例集内（否则牌库 UI 与实际牌库脱节：牌库永远不减少）
                var deck = pid == 1 ? game.DeckManager.GetPlayer1DeckData() : game.DeckManager.GetPlayer2DeckData();
                foreach (var c in deck)
                    Assert.IsTrue(seen.Contains(c.UID),
                        $"玩家{pid} 牌库剩余 {c.PlayerTemplate?.CardName}({c.UID}) 不属于 View 绑定的实例集");
            }
        }

        /// <summary>来源未变时，重复走"发牌编排"路径不得换掉整批实例（PM draw 依赖同一批实例的卡 UI）。</summary>
        public void Test来源未变时重复发牌不得重装卡组()
        {
            var game = NewGame();
            var before = new HashSet<string>(game.DeckManager.GetPlayer1DeckData().Select(c => c.UID));

            game.StartMatch();
            game.DealOpeningHands();   // 等价：开局编排再跑一次"重放卡组+秘社、发牌"

            foreach (var c in game.DeckManager.GetPlayer1DeckData())
                Assert.IsTrue(before.Contains(c.UID), $"{c.UID} 不在原实例集 ⇒ 卡组被重装了（View 将失联）");
            foreach (var c in game.CardManager.GetHandCardsByOwner(1))
                Assert.IsTrue(before.Contains(c.UID), $"手牌 {c.UID} 不在原实例集 ⇒ 卡组被重装了");
        }

        // ==================== ② 来源变化仍照常重装 ====================

        /// <summary>
        /// 既有契约（测试/联机同款）：`InitializeAll()` 之后再赋 `P1DeckOverride`，
        /// `StartMatch()` 必须按新来源重装并据此发牌（起手数取该卡组秘社；无秘社则回退默认秘社）。
        /// 本用例同时证明"跳过重装"只发生在来源**真的没变**时。
        /// </summary>
        public void TestInitializeAll之后赋入显式卡组仍生效()
        {
            var game = NewGame();
            var oldInstances = new HashSet<string>(game.DeckManager.GetPlayer1DeckData().Select(c => c.UID));

            const int copies = 30;
            var deck = new DeckList { DeckName = "测试·显式卡组（InitializeAll 之后赋入）" };
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = Jc001Path, Copies = copies });

            game.P1DeckOverride = deck;   // 来源变化（硬编码 → 显式卡组）⇒ 必须重装
            game.StartMatch();

            int handCount = game.CardManager.GetHandCardsByOwner(1).Count;
            Assert.IsTrue(handCount > 0, "前置：P1 应有起始手牌");
            Assert.AreEqual(copies - handCount, game.DeckManager.GetPlayer1DeckData().Count,
                "P1 牌库剩余张数应＝新卡组张数去掉起手牌（说明确实按本局显式卡组重装了）");
            foreach (var c in game.CardManager.GetHandCardsByOwner(1))
                Assert.IsFalse(oldInstances.Contains(c.UID),
                    $"P1 起手牌 {c.UID} 仍来自旧实例集 ⇒ 来源变化时未重装（本局卡组不生效）");
        }

        // ==================== ③ 重开一局强制重建 ====================

        /// <summary>
        /// `ResetMatch` 会先 `CardManager.Reset()`（`_allInstances` 清空、`_nextUID` 归 1），
        /// 因此即使卡组来源未变也**必须**重建整批实例，否则新旧实例共用同一批 UID。
        /// </summary>
        public void Test重开一局必须强制重建整批实例()
        {
            var game = NewGame();
            game.StartMatch();
            var oldFirst = game.DeckManager.GetPlayer1DeckData().FirstOrDefault();
            Assert.IsTrue(oldFirst != null, "前置：开局后牌库应有实例");

            game.ResetMatch();

            var newFirst = game.DeckManager.GetPlayer1DeckData().FirstOrDefault();
            Assert.IsTrue(newFirst != null, "重开后牌库应有实例");
            Assert.IsFalse(ReferenceEquals(oldFirst, newFirst),
                "重开一局必须重建整批实例（CardManager.Reset 已把 _nextUID 归 1，复用旧实例会造成 UID 冲突）");
        }

        // ==================== 辅助 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 20261005 };
            game.InitializeAll();
            return game;
        }
    }
}
