using System;
using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【JC002 完整进场触发链路集成测试 · 规则⑥（本地区限制 · 目标=自身场景）】
    ///
    /// 验证的完整链路（B 层 · 真实组件，仅以 mock 视图绕过 UI 弹窗）：
    ///   构造 JC002 风格卡（进场触发，目标限定本地区角色且可指定自身）
    ///   → MoveToArea 进场触发 EnterPlay 事件
    ///   → 触发注册表收集进场触发 → TurnManager 触发阶段（排序窗口）
    ///   → 弹选目标（mock 自动选自身）→ 入堆叠 + 写来源地区快照
    ///   → 结算前自身移动到另一地区 → FireMoved → 移动事件总线 → 比对快照 → 置“目标已丢失”
    ///   → 断言堆叠条目“目标已丢失”为真。
    /// </summary>
    public class JC002TriggerChainTests : TestRunner
    {
        // —— 自动选自身的 mock 视图：弹选目标时直接回选有效列表里等于来源自身的 UID ——
        private class 自动选自身视图 : ITriggerView
        {
            public List<string> 最近有效目标;
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<TriggerEffectInstance> triggers,
                Action<List<TriggerEffectInstance>> onEnterStack, Action onDone)
            {
                // 排序窗口：直接按原顺序入栈
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }
            public void RequestTargetSelection(TriggerEffectInstance trigger, List<string> validTargetUids, Action<string> onPicked)
            {
                最近有效目标 = validTargetUids;
                // JC002 可指定自身：选有效目标中等于来源自身的 UID（即“目标=自身”场景）
                var 自身UID = trigger.Source?.UID ?? "";
                var 选中 = validTargetUids.Contains(自身UID) ? 自身UID : (validTargetUids.Count > 0 ? validTargetUids[0] : "");
                onPicked?.Invoke(选中);
            }
            public void RemoveOrderEntries(List<TriggerEffectInstance> pushed) { }
        }

        private TurnManager _tm;
        private CardManager _cm;
        private StackManager _sm;
        private ZoneManager _zm;
        private AreaManager _地区管理器;

        private void 搭建测试环境()
        {
            _cm = new CardManager();
            _cm._Ready();                       // 单例 + 实例表初始化
            _sm = new StackManager();
            _sm._Ready();
            _zm = new ZoneManager();
            _zm.Initialize(_cm, null);          // 牌库管理器传 null（测试不涉牌库）
            // 关键连通性：CardManager 的 _zoneManager 必须由本 ZoneManager 注入，
            // 否则 TurnManager.GetLocalZoneTargets 内的 _cardManager.GetAreaInstancesBySlot
            // 会因 CardManager._zoneManager 为 null 而返回空候选 → 触发因“无合法目标”被跳过（即 0 入栈根因）。
            _cm.SetZoneManager(_zm);
            _tm = new TurnManager();
            _tm._Ready();
            // 注入依赖（与游戏初始化等价的最小集）
            _tm.Initialize(null, null, _cm);    // 牌库/玩家管理器测试用不到
            _tm.SetStackManager(_sm);
            _tm.SetZoneManager(_zm);
            // 本地区选目标（GetLocalZoneTargets）依赖 AreaManager.GetSlotIndex 判定槽位，
            // AreaManager 为 null 时该方法直接返回空候选 → 触发被跳过。注册最小地区槽位即可。
            _地区管理器 = new AreaManager();
            _地区管理器.RegisterArea("areaA", 1, _cm.CreateCardInstance(new AreaCardData(), 0));
            _地区管理器.RegisterArea("areaB", 2, _cm.CreateCardInstance(new AreaCardData(), 0));
            _tm.SetAreaManager(_地区管理器);
            _tm.SetTriggerView(new 自动选自身视图());
        }

        /// <summary>构造一张 JC002 风格的玩家卡：进场触发，目标本地区角色，可指定自身。</summary>
        private CardInstance 创建JC002风格卡(int 拥有者Id, string 地区Uid)
        {
            var 卡数据 = new PlayerCardData
            {
                CardName = "JC002_测试卡",
                // 关键：本地区选目标（GetLocalZoneTargets）按 MapInstanceToTargetKind 过滤候选，
                // 它依赖实例 PlayerTemplate.CategoryFlag 是否为 Character。未声明则候选恒空，
                // 触发会因“无合法目标”被跳过，根本不入栈（这正是测试期望 1 实际 0 的根因）。
                CategoryFlag = CardCategory.Character,
            };
            var 效果 = new EffectData
            {
                EffectId = "JC002_进场触发",
                Timing = EffectTiming.EnterTrigger,
                TriggerCovertMode = CovertTriggerMode.FaceUp,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,
                    IsLocalZone = true,    // 规则⑥：限定本地区（同 AREASLOT）
                },
            };
            卡数据.EffectDataList.Add(效果);
            var 实例 = _cm.CreateCardInstance(卡数据, 拥有者Id);
            实例.CurrentZone = CardZone.Hand;   // 模拟“从手牌进场”
            _最后效果 = 效果;
            return 实例;
        }

        private EffectData _最后效果;

        public void TestJC002进场触发目标为自身时自身移动地区应丢失目标()
        {
            搭建测试环境();
            const string 地区A = "areaA";
            const string 地区B = "areaB";

            // 1) 构造 JC002 风格卡并放到手牌（模拟即将进场）
            var 卡片 = 创建JC002风格卡(1, 地区A);

            // 诊断：确认事件总线已注入（Dispatch 为 null 时 Emit 会静默降级，导致 0 入栈且不报错）
            Assert.IsTrue(GameEventBus.Dispatch != null,
                "GameEventBus.Dispatch 未注入（TurnManager.Initialize 未执行或静态委托被覆盖），进场事件被静默丢弃");

            // 2) 进场：MoveToArea 触发 EnterPlay → 收集 → 排序窗口 → 选自身 → 入栈（写来源地区快照 地区A）
            _zm.MoveToArea(卡片, 地区A, 1);

            // 3) 断言：已有一张 JC002 进场触发入栈，且目标=自身
            Assert.AreEqual(1, _sm.Entries.Count, "期望入栈 1 条进场触发，实际 " + _sm.Entries.Count + " 条");
            var 条目 = _sm.Entries[0];
            Assert.AreEqual(卡片.UID, 条目.TargetCardUid, "目标应被设为自身（来源）UID");
            Assert.IsFalse(条目.TargetWasLost, "入栈瞬间目标不应已丢失");
            Assert.IsTrue(条目.HasSourceZoneSnapshot, "应已记录来源地区快照");
            Assert.AreEqual(地区A, 条目.SourceZoneDetailSnapshot, "来源地区快照应为 地区A");
            Assert.AreEqual(1, 条目.SourceControllerSnapshot, "来源控制者快照应为玩家1");
            int 版本号入栈时 = 条目.TargetVersionId;

            // 4) 结算前自身移动到另一地区（争夺区→争夺区）。
            //    注（2026-08-11）：目标丢失总线已移除，此处【不会】即时置位 TargetWasLost；
            //    且争夺区间移动【不刷版本号】，故版本号仍一致，必须靠结算时的发动地快照复检判丢。
            _zm.MoveToArea(卡片, 地区B, 1);

            Assert.AreEqual(版本号入栈时, 卡片.VersionId,
                "【版本号】争夺区→争夺区移动不应刷新版本号（规则⑥改由发动地快照判定）");
            Assert.IsFalse(条目.TargetWasLost,
                "总线已移除：移动瞬间不再即时置位，须等结算时判定");

            // 5) 结算时判定：目标=自身且自身已离开发动地(地区A)，规则⑥（发动地快照）判丢失
            Assert.IsTrue(结算时判丢失(条目),
                "【规则⑥·发动地快照】目标=自身时，自身离开发动地区应在结算时判丢失");
            Assert.IsTrue(条目.TargetWasLost,
                "结算判丢失后应顺带置位 TargetWasLost（供 UI 箭头读取）");
        }

        /// <summary>
        /// 调用 TurnManager 结算时的目标丢失判定（ResolveEntryTargetIsLost 为 private，测试经反射调用）。
        /// 这是总线移除后目标丢失的唯一判定点。
        /// </summary>
        private bool 结算时判丢失(StackEntry 条目)
        {
            var 方法 = typeof(TurnManager).GetMethod("ResolveEntryTargetIsLost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsTrue(方法 != null, "未找到 TurnManager.ResolveEntryTargetIsLost（方法名或签名已变更）");
            return (bool)方法.Invoke(_tm, new object[] { 条目 });
        }

        /// <summary>
        /// 【规则⑥·发动地快照】目标≠自身：目标跑出发动地区 → 结算时判丢失。
        /// 同时验证争夺区→争夺区移动不刷版本号（第②段版本号比对拦不住，必须由第③段快照复检兜住）。
        /// </summary>
        public void Test目标离开发动地区应在结算时判丢失()
        {
            搭建测试环境();
            const string 地区A = "areaA";
            const string 地区B = "areaB";

            // 先放一张“另一张角色”在地区A，作为 JC002 的非自身目标候选
            var 目标卡 = _cm.CreateCardInstance(new PlayerCardData
            {
                CardName = "本地区友军",
                CategoryFlag = CardCategory.Character,
            }, 1);
            _zm.MoveToArea(目标卡, 地区A, 1);

            var 卡片 = 创建JC002风格卡(1, 地区A);
            _zm.MoveToArea(卡片, 地区A, 1);

            Assert.AreEqual(1, _sm.Entries.Count, "期望入栈 1 条进场触发");
            var 条目 = _sm.Entries[0];
            Assert.AreEqual(地区A, 条目.SourceZoneDetailSnapshot, "发动地快照应为 地区A");

            // 若 mock 选中的是自身，改为手动把目标指向另一张卡，专门验证“目标≠自身”分支
            条目.TargetCardUid = 目标卡.UID;
            条目.TargetVersionId = 目标卡.VersionId;
            Assert.IsFalse(结算时判丢失(条目), "目标未移动时不应判丢失");

            // 目标跑到另一地区（争夺区→争夺区，不刷版本号）
            int 版本号移动前 = 目标卡.VersionId;
            _zm.MoveToArea(目标卡, 地区B, 1);
            Assert.AreEqual(版本号移动前, 目标卡.VersionId,
                "【版本号】争夺区→争夺区移动不应刷新版本号");

            Assert.IsTrue(结算时判丢失(条目),
                "【规则⑥·发动地快照】目标离开发动地区应在结算时判丢失");
        }

        /// <summary>
        /// 【版本号】离开争夺区（进手牌/墓地等）仍应刷新版本号 → 结算时第②段即判丢失。
        /// 与“争夺区内移动不刷”形成对照，确保不刷范围没有被放大。
        /// </summary>
        public void Test离开争夺区应刷新版本号并判丢失()
        {
            搭建测试环境();
            const string 地区A = "areaA";

            var 目标卡 = _cm.CreateCardInstance(new PlayerCardData
            {
                CardName = "被移走的角色",
                CategoryFlag = CardCategory.Character,
            }, 1);
            _zm.MoveToArea(目标卡, 地区A, 1);

            var 卡片 = 创建JC002风格卡(1, 地区A);
            _zm.MoveToArea(卡片, 地区A, 1);
            var 条目 = _sm.Entries[0];
            条目.TargetCardUid = 目标卡.UID;
            条目.TargetVersionId = 目标卡.VersionId;

            int 版本号移动前 = 目标卡.VersionId;
            _zm.MoveToHand(目标卡, 1);      // 离开争夺区 → 照常 BumpVersion

            Assert.IsTrue(目标卡.VersionId != 版本号移动前,
                "【版本号】离开争夺区（进手牌）必须刷新版本号");
            Assert.IsTrue(结算时判丢失(条目),
                "目标离开争夺区后结算应判丢失（第②段版本号比对命中）");
        }

        public void TestJC002进场触发目标为自身时自身未移动地区不应丢失目标()
        {
            搭建测试环境();
            const string 地区A = "areaA";

            var 卡片 = 创建JC002风格卡(1, 地区A);
            _zm.MoveToArea(卡片, 地区A, 1);

            Assert.AreEqual(1, _sm.Entries.Count, "期望入栈 1 条进场触发");
            var 条目 = _sm.Entries[0];
            Assert.IsFalse(条目.TargetWasLost, "自身未移动地区，目标不应丢失（规则⑥不触发）");
            Assert.AreEqual(地区A, 条目.SourceZoneDetailSnapshot, "来源地区快照应保持为 地区A");
        }
    }
}
