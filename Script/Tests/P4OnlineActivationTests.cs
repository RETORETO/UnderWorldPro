using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P2·联机】激活的"选择类入参"由**服务端接管**收集（与单机 View 侧那几套窗 **1:1**）：
    ///  · **目标地区**（JC117 红眼航班）→ `SelectTarget`（候选＝合法地区，客户端走"地区槽位高亮 + 点击"）；
    ///  · **牺牲代价**（K22）→ `SelectActivationCost` 单选窗（客户端 `OnlineActivationChoiceWindow`）；
    ///  · **标志代价**（K5）→ `SelectActivationCost`：固定数量＝单选；`AmountByPlayerChoice`＝**逐卡分配**
    ///    （回包 `SelectedUids`+`SelectedAmounts`；服务端按"该卡现有标志数"权威钳制，且总量 ≥ 1）；
    ///  · **玩家目标**（K24）→ `SelectPlayer`。
    /// 顺序与单机 `Arena.BeginActivate` 一致：**目标地区 → 牺牲 → 标志 → 玩家目标**；
    /// 取消/超时/回包不在候选内 → **整次激活不发动**（不付任何代价）。
    /// </summary>
    public class P4OnlineActivationTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1170 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_oa_mana_{playerId}_{i}", CardCategory.Attachment), playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static void Ready(GameInitializer game, int playerId)
        {
            game.TurnManager.FirstPlayerId = playerId;
            game.TurnManager.PriorityPlayerId = playerId;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        /// <summary>脚本化网关：按键分派回包；记录全部请求。</summary>
        private sealed class ActivationGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public string TargetAreaAnswer;                  // SelectTarget 回包（地区或卡）
            public string SacrificeAnswer;                   // SelectActivationCost 单选回包
            public bool CancelCost;                          // 代价窗取消
            public System.Collections.Generic.Dictionary<string, int> AllocationAnswer; // 逐卡分配回包（null＝用默认 1 张各 1）
            public bool CancelTarget;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                switch (req?.RequestKind)
                {
                    case Script.Net.ChoiceRequest.Kind.SelectTarget:
                        r.Cancelled = CancelTarget;
                        if (!CancelTarget)
                            r.SelectedUid = TargetAreaAnswer
                                ?? (req.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                        break;
                    case Script.Net.ChoiceRequest.Kind.SelectActivationCost:
                        r.Cancelled = CancelCost;
                        if (CancelCost) break;
                        // 逐卡分配模式＝服务端下发了"每卡现有标志数上限"（`OptionMaxAmounts` 非空）
                        if (req.OptionMaxAmounts != null && req.OptionMaxAmounts.Count > 0)
                        {
                            var alloc = AllocationAnswer;
                            if (alloc == null)
                            {
                                alloc = new System.Collections.Generic.Dictionary<string, int>();
                                if (req.OptionUids.Count > 0) alloc[req.OptionUids[0]] = 1;
                            }
                            foreach (var kv in alloc)
                            {
                                r.SelectedUids.Add(kv.Key);
                                r.SelectedAmounts.Add(kv.Value);
                            }
                        }
                        else
                        {
                            r.SelectedUid = SacrificeAnswer
                                ?? (req.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                        }
                        break;
                    case Script.Net.ChoiceRequest.Kind.SelectPlayer:
                        r.SelectedPlayerId = req.OptionPlayerIds != null && req.OptionPlayerIds.Count > 0
                            ? req.OptionPlayerIds[0] : -1;
                        break;
                    default:
                        r.Cancelled = true;
                        break;
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        // ==================== ① JC117：地区目标 ====================

        public void TestOnlineActivation_JC117地区目标()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 4);
            var jc117 = game.CardManager.CreateCardInstance(Load("JC117_hongyanhangban"), 1);
            game.ZoneManager.MoveToAttachedZone(jc117, "", 1);           // 结附于秘社（本卡为徽章类附属）
            var mover = game.CardManager.CreateCardInstance(Probe("TS_oa_mover"), 1);
            game.ZoneManager.MoveToArea(mover, areas[0], 1);
            Ready(game, 1);

            var ability = FindAbility(jc117, "JC117_B_MoveAllyToArea")
                          ?? FindFirstActivation(jc117);
            Assert.IsNotNull(ability, "JC117 应有『移动到目标地区』的激活能力");
            Assert.IsTrue(ability.NeedsAreaTarget, "该能力需要目标地区");

            var gw = new ActivationGateway { TargetAreaAnswer = areas[1] };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, jc117.UID, ability.EffectId,
                mover.UID, -1).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "服务端接管流程应完成");
            Assert.AreEqual(1, gw.Requests.Count, "只发一次选择请求（地区）");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectTarget, gw.Requests[0].RequestKind, "请求种类＝选目标（地区）");
            Assert.IsTrue(gw.Requests[0].OptionUids.Contains(areas[1]), "候选含目标地区");
            Assert.IsFalse(gw.Requests[0].OptionUids.Contains(mover.ZoneDetailId),
                "候选排除卡目标当前所在地（JC117『不能原地移动』由服务端权威过滤）");
            Assert.IsFalse(game.StackManager.IsEmpty, "激活已入堆叠");
            Assert.AreEqual(areas[1], game.StackManager.Top?.TargetAreaId, "入堆叠时锁定**服务端下发的候选里选中的地区**");
        }

        public void TestOnlineActivation_地区目标取消则不发动()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 4);
            var jc117 = game.CardManager.CreateCardInstance(Load("JC117_hongyanhangban"), 1);
            game.ZoneManager.MoveToAttachedZone(jc117, "", 1);
            var mover = game.CardManager.CreateCardInstance(Probe("TS_oa_mover2"), 1);
            game.ZoneManager.MoveToArea(mover, areas[0], 1);
            Ready(game, 1);

            var ability = FindFirstActivation(jc117);
            var gw = new ActivationGateway { CancelTarget = true };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, jc117.UID, ability.EffectId,
                mover.UID, -1).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消选地区 → 不发动");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        // ==================== ② 牺牲代价（K22） ====================

        public void TestOnlineActivation_牺牲代价()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var unit = game.CardManager.CreateCardInstance(Load("JC093_yuanlingzhouren"), 1);
            game.ZoneManager.MoveToAttachedZone(unit, "", 1);   // 结附类（宿主由结附关系决定，此处只要能力可激活）
            var victimA = game.CardManager.CreateCardInstance(Probe("TS_oa_v1"), 1);
            game.ZoneManager.MoveToArea(victimA, areas[0], 1);
            var victimB = game.CardManager.CreateCardInstance(Probe("TS_oa_v2"), 1);
            game.ZoneManager.MoveToArea(victimB, areas[0], 1);

            var ability = FindFirstActivation(unit);
            Assert.IsNotNull(ability?.SacrificeCost, "JC093 的激活能力带『牺牲一个角色』代价");

            var gw = new ActivationGateway { SacrificeAnswer = victimB.UID };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, unit.UID, ability.EffectId, null, -1)
                .GetAwaiter().GetResult();

            Assert.IsTrue(ok, "接管流程应完成");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectActivationCost, gw.Requests[0].RequestKind,
                "代价窗＝SelectActivationCost（客户端用 OnlineActivationChoiceWindow，与单机按钮窗 1:1）");
            Assert.AreEqual(2, gw.Requests[0].OptionUids.Count, "候选＝2 个合法牺牲对象");
            Assert.AreEqual(2, gw.Requests[0].OptionLabels.Count, "按钮文案随请求下发");
            Assert.AreEqual(CardZone.Graveyard, victimB.CurrentZone, "按玩家所选牺牲（而非服务端兜底第一个）");
            Assert.AreEqual(CardZone.AreaZone, victimA.CurrentZone, "未被选的候选不受影响");
        }

        public void TestOnlineActivation_牺牲代价取消则不发动()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var unit = game.CardManager.CreateCardInstance(Load("JC093_yuanlingzhouren"), 1);
            game.ZoneManager.MoveToAttachedZone(unit, "", 1);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_oa_v3"), 1);
            game.ZoneManager.MoveToArea(victim, areas[0], 1);

            var ability = FindFirstActivation(unit);
            var gw = new ActivationGateway { CancelCost = true };
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, unit.UID, ability.EffectId, null, -1)
                .GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消代价选择 → 不发动");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "未牺牲任何角色");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣任何费用（代价也不付）");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        // ==================== ③ 标志代价：逐卡分配（K5·JC055②） ====================

        public void TestOnlineActivation_标志代价逐卡分配()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var unit = game.CardManager.CreateCardInstance(Load("JC055_annisiduola"), 1);
            game.ZoneManager.MoveToArea(unit, areas[0], 1);
            // 两张带时间标志的本方卡（供逐卡分配）
            var c1 = game.CardManager.CreateCardInstance(Probe("TS_oa_t1"), 1);
            game.ZoneManager.MoveToArea(c1, areas[0], 1);
            c1.AddCounter("时间标志", 2);
            var c2 = game.CardManager.CreateCardInstance(Probe("TS_oa_t2"), 1);
            game.ZoneManager.MoveToArea(c2, areas[0], 1);
            c2.AddCounter("时间标志", 1);

            var ability = FindCounterCostAbility(unit);
            Assert.IsNotNull(ability, "JC055 应有带标志代价的激活能力");
            Assert.IsTrue(ability.CounterCost.AmountByPlayerChoice, "该代价＝X 由玩家选择（逐卡分配）");
            Assert.AreEqual(2, game.EffectEngine.GetCounterCostCandidates(unit, ability.CounterCost).Count,
                "前置：服务端枚举出 2 个带时间标志的本方场上卡");
            Ready(game, 1);

            var gw = new ActivationGateway
            {
                AllocationAnswer = new System.Collections.Generic.Dictionary<string, int> { [c1.UID] = 2 },   // 从 c1 上移除 2 个
            };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, unit.UID, ability.EffectId, null, -1)
                .GetAwaiter().GetResult();

            Assert.IsTrue(ok, "接管流程应完成");
            var costReq = gw.Requests.Find(r => r.RequestKind == Script.Net.ChoiceRequest.Kind.SelectActivationCost);
            Assert.IsNotNull(costReq, "应下发代价窗请求");
            Assert.AreEqual(costReq.OptionUids.Count, costReq.OptionMaxAmounts.Count,
                "逐卡分配模式的判据＝服务端**下发了每卡上限**（`OptionMaxAmounts` 与候选同序同数）；同时该值就是客户端显示的『共 N』");
            Assert.AreEqual(costReq.OptionUids.Count, costReq.OptionLabels.Count, "按钮文案随请求下发");
            Assert.AreEqual(0, c1.Counters.TryGetValue("时间标志", out var n1) ? n1 : 0, "按回包从 c1 移除 2 个时间标志");
            Assert.AreEqual(1, c2.Counters.TryGetValue("时间标志", out var n2) ? n2 : 0, "未分配的卡标志不变");
            Assert.IsFalse(game.StackManager.IsEmpty, "激活已入堆叠");
        }

        public void TestOnlineActivation_标志代价回包受权威钳制()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var unit = game.CardManager.CreateCardInstance(Load("JC055_annisiduola"), 1);
            game.ZoneManager.MoveToArea(unit, areas[0], 1);
            var c1 = game.CardManager.CreateCardInstance(Probe("TS_oa_t3"), 1);
            game.ZoneManager.MoveToArea(c1, areas[0], 1);
            c1.AddCounter("时间标志", 1);

            var ability = FindCounterCostAbility(unit);
            Assert.IsNotNull(ability, "前置：JC055 有标志代价能力");
            Ready(game, 1);
            var gw = new ActivationGateway
                {
                // 越权回包：声称从 c1 移除 5 个（实际只有 1 个）→ 服务端钳制到 1
                AllocationAnswer = new System.Collections.Generic.Dictionary<string, int> { [c1.UID] = 5 },
            };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.ActivateAbilityWithChoicesAsync(1, unit.UID, ability.EffectId, null, -1)
                .GetAwaiter().GetResult();

            Assert.IsTrue(ok, "钳制后仍可发动（移除 1 个）");
            Assert.AreEqual(0, c1.Counters.TryGetValue("时间标志", out var n) ? n : 0, "只移除该卡现有的 1 个标志（不越权）");
        }

        // ==================== ④ 无网关 / 无选择类入参 → 不接管 ====================

        public void TestOnlineActivation_无网关不接管()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var unit = game.CardManager.CreateCardInstance(Load("JC055_annisiduola"), 1);
            game.ZoneManager.MoveToArea(unit, areas[0], 1);
            var ability = FindCounterCostAbility(unit);

            Assert.IsFalse(game.PlayerActionService.TryBeginActivationChoiceOnline(1, unit.UID, ability.EffectId, null, -1),
                "未注入网关（单机/headless）→ 不接管，走既有同步激活路径");

            game.TurnManager.InputGateway = new ActivationGateway();
            Assert.IsTrue(game.PlayerActionService.TryBeginActivationChoiceOnline(1, unit.UID, ability.EffectId, null, -1),
                "已注入网关且该能力带选择类入参 → 服务端接管");

            // 无选择类入参的能力（普通角色激活）→ 不接管
            var plain = game.CardManager.CreateCardInstance(Load("JC003_weimuhuwei"), 1);
            game.ZoneManager.MoveToArea(plain, areas[0], 1);
            var plainAbility = FindFirstActivation(plain);
            if (plainAbility != null && !plainAbility.NeedsAreaTarget && plainAbility.SacrificeCost == null
                && plainAbility.CounterCost == null
                && !Script.Manager.PlayerTargetPool.IsPlayerSpec(plainAbility.Target))
            {
                Assert.IsFalse(game.PlayerActionService.TryBeginActivationChoiceOnline(1, plain.UID, plainAbility.EffectId, null, -1),
                    "无选择类入参 → 不接管（避免无谓的异步接管）");
            }
        }

        // ==================== 辅助 ====================

        private static EffectData FindAbility(CardInstance inst, string effectId)
        {
            if (inst?.PlayerTemplate?.EffectDataList == null) return null;
            foreach (var e in inst.PlayerTemplate.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>该卡第一个激活类效果。</summary>
        private static EffectData FindFirstActivation(CardInstance inst)
        {
            if (inst?.PlayerTemplate?.EffectDataList == null) return null;
            foreach (var e in inst.PlayerTemplate.EffectDataList)
                if (e != null && (e.Timing == EffectTiming.CharacterActivated
                                  || e.Timing == EffectTiming.AttachmentActivated)) return e;
            return null;
        }

        /// <summary>该卡带标志代价的激活效果。</summary>
        private static EffectData FindCounterCostAbility(CardInstance inst)
        {
            if (inst?.PlayerTemplate?.EffectDataList == null) return null;
            foreach (var e in inst.PlayerTemplate.EffectDataList)
                if (e != null && e.CounterCost != null) return e;
            return null;
        }
    }
}
