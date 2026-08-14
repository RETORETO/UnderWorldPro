using System;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>
    /// 目标丢失事件单元测试（2026-08-09 适配 MVC 修复）。
    /// MVC 修复后 CardInstance 声明自身事件 OnStateChanged（Model 自身事件），
    /// 由 CardManager.CreateCardInstance 工厂订阅并转发到 CardManager 全局静态总线。
    /// 注（2026-08-11·目标丢失总线全部清零）：
    ///  · 早前已删除 CardManager.OnControllerChanged / OnSubCategoriesChanged 两条总线；
    ///  · 本次删除最后一条 —— TurnManager 对 CardManager.OnCardStateChanged 的订阅
    ///    （OnCardStateChangedHandler，含翻面③与移动⑥两条即时置位分支）。
    /// 目标丢失判定现已全部下沉到【结算时】TurnManager.IsTargetLost 三段链：
    ///   ① 实例不存在 → ② 版本号比对（BumpVersion 覆盖离场/翻面/子类别等）
    ///   → ③ TargetValidity.Check（本地区发动地快照、Friendly/Enemy 等 Restrictions 复检）。
    /// CardManager.OnCardStateChanged 事件【本身保留】，但仅供 UI（Arena 箭头重绘）使用，不再参与规则判定。
    /// 本单测聚焦 Model 自身事件发点本身（OnStateChanged 是否被正确触发），不依赖 Godot/Manager 实例。
    /// 丢失判定的端到端效果由 JC002TriggerChainTests（规则⑥发动地快照 + 版本号）覆盖。
    /// </summary>
    public partial class TargetLossBusTests : TestRunner
    {
        private CardInstance NewRawInstance(string uid)
        {
            var tmpl = new PlayerCardData { CardName = "B-" + uid };
            return new CardInstance(tmpl, uid);
        }

        // ---------- A. Model 自身事件：暗藏变化经 SetCovert 触发 OnStateChanged ----------

        public void TestSetCovertTrueFiresOnStateChanged()
        {
            var card = NewRawInstance("c1");
            CardChangeKind? fired = null;
            card.OnStateChanged += (c, k) => { if (c == card) fired = k; };

            card.SetCovert(true);

            Assert.IsTrue(fired.HasValue, "SetCovert 应触发 CardInstance.OnStateChanged");
            Assert.AreEqual(CardChangeKind.CovertChanged, fired.Value);
            Assert.IsTrue(card.IsCovert);
        }

        public void TestSetCovertFalseFiresOnStateChanged()
        {
            var card = NewRawInstance("c2");
            card.SetCovert(true); // 先进入暗藏
            CardChangeKind? fired = null;
            card.OnStateChanged += (c, k) => { if (c == card) fired = k; };

            card.SetCovert(false); // 背面→正面（UNCOVER 方向）

            Assert.IsTrue(fired.HasValue, "SetCovert(false) 应触发 CardInstance.OnStateChanged");
            Assert.AreEqual(CardChangeKind.CovertChanged, fired.Value);
            Assert.IsFalse(card.IsCovert);
        }

        /// <summary>
        /// 【规则③·翻面即丢失】SetCovert 必须自增 VersionId（正→反、反→正双向）。
        /// 总线移除后翻面丢失完全依赖版本号：若此处漏 BumpVersion，结算时第②段比对不出差异，
        /// "翻面即丢"会静默失效，故单独回归。
        /// </summary>
        public void TestSetCovertBumpsVersion()
        {
            var card = NewRawInstance("c4");
            int v0 = card.VersionId;

            card.SetCovert(true);                    // 正 → 反
            Assert.IsTrue(card.VersionId != v0, "正→反翻面必须刷新版本号（规则③）");

            int v1 = card.VersionId;
            card.SetCovert(false);                   // 反 → 正
            Assert.IsTrue(card.VersionId != v1, "反→正翻面必须刷新版本号（规则③）");

            int v2 = card.VersionId;
            card.SetCovert(false);                   // 同值短路
            Assert.AreEqual(v2, card.VersionId, "同值 SetCovert 不应刷新版本号");
        }

        public void TestSetCovertNoChangeDoesNotFire()
        {
            var card = NewRawInstance("c3");
            card.SetCovert(true);
            bool fired = false;
            card.OnStateChanged += (c, k) => { if (c == card) fired = true; };

            card.SetCovert(true); // 同值，应短路不发

            Assert.IsFalse(fired, "SetCovert 同值不应重复发事件");
        }

        // ---------- A. Model 自身事件：屏障变化经 SetGuard 触发 OnStateChanged（预留，当前无运行时调用） ----------

        public void TestSetGuardFiresOnStateChanged()
        {
            var card = NewRawInstance("c4");
            CardChangeKind? fired = null;
            card.OnStateChanged += (c, k) => { if (c == card) fired = k; };

            card.SetGuard(true);

            Assert.IsTrue(fired.HasValue, "SetGuard 应触发 CardInstance.OnStateChanged");
            Assert.AreEqual(CardChangeKind.GuardChanged, fired.Value);
            Assert.IsTrue(card.HasGuard);
        }

        // ---------- 过程记忆字段语义 ----------

        public void TestTargetWasLostDefaultsFalseAndSticky()
        {
            var entry = new StackEntry(0, NewRawInstance("src"));
            Assert.IsFalse(entry.TargetWasLost, "新建条目 TargetWasLost 默认 false");

            // 2026-08-11：TargetWasLost 不再由总线即时置位，改由结算时 IsTargetLost 判丢失后置位，
            // 供 UI 箭头读取；语义仍为"只置位不清位"。
            entry.TargetWasLost = true;
            Assert.IsTrue(entry.TargetWasLost, "只置位、不清位（即使目标后来恢复合法也不撤销）");
        }
    }
}
