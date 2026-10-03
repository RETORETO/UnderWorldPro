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
    /// 【K17·条件式动作分支】按条件二选一执行（`EffectCondition.Predicate` + `ElseBranch`）：
    /// **条件成立 → `Actions`；不成立 → `ElseBranch`**（单一判定实现 `EffectEngine.ResolveEffect`）。
    /// 覆盖卡：**JC004 力场法师**（进场触发：横置本地区目标角色；若该角色已横置，则改为移回其拥有者手上）。
    ///
    /// 【2026-09 修复】JC004 的分支条件必须走 **`BranchPredicate`**（门禁 `Predicate="Always"`）：
    /// 「已横置」是**对已选定目标**的判定，而 `Predicate` 会在**触发收集阶段**先当门禁过滤一遍
    /// （那时还没有目标，上下文卡＝JC004 自己、未横置）⇒ 原实现导致该触发**连堆叠都进不去**。
    /// 本节因此补了**真实链路端到端**用例（进场 → 收集 → 排序 → 选目标 → 入堆叠 → 结算）。
    /// </summary>
    public class P3ConditionalBranchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>触发窗桩：全部入栈 + 选目标优先选 `PreferUid`（缺省取第一个候选），并记录候选。</summary>
        private sealed class PickFirstTriggerView : Script.Interfaces.ITriggerView
        {
            public readonly List<List<string>> TargetCandidates = new List<List<string>>();
            public string PreferUid;

            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger,
                List<string> validTargetUids, System.Action<string> onPicked)
            {
                TargetCandidates.Add(validTargetUids ?? new List<string>());
                string pick = (!string.IsNullOrEmpty(PreferUid) && validTargetUids != null && validTargetUids.Contains(PreferUid))
                    ? PreferUid
                    : (validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
                onPicked?.Invoke(pick);
            }

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>
        /// **网关桩**（联机/本地主机权威路径：`RunTriggerPhaseAsync` —— 用户实测日志走的就是这条）：
        /// 触发排序＝全部入栈；选目标＝按 `PickTarget` 回包（缺省取第一个候选）。记录请求供断言。
        /// </summary>
        private sealed class TriggerGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public System.Func<Script.Net.ChoiceRequest, string> PickTarget;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                }
                else if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTarget)
                {
                    r.SelectedUid = PickTarget != null
                        ? PickTarget(req)
                        : (req.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }

            public Script.Net.ChoiceRequest LastOf(Script.Net.ChoiceRequest.Kind kind)
            {
                for (int i = Requests.Count - 1; i >= 0; i--) if (Requests[i].RequestKind == kind) return Requests[i];
                return null;
            }
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 97 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData Jc004Effect(PlayerCardData tmpl)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == "JC004_1_TapOrBounceLocalCharacter") return e;
            return null;
        }

        /// <summary>按 `EffectId` 找效果（结构断言共用）。</summary>
        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestK17_JC004结构()
        {
            var tmpl = Load("JC004_lichangfashi");
            Assert.IsNotNull(tmpl, "JC004 模板应可加载");
            var e = Jc004Effect(tmpl);
            Assert.IsNotNull(e, "JC004 应有进场触发效果");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual("Always", e.Condition.Predicate,
                "门禁恒真（**不得**把「已横置」当收集门禁——那样触发会在收集阶段被丢掉）");
            Assert.AreEqual("已横置", e.Condition.BranchPredicate, "分支谓词＝已横置（对已选定目标求值；成立即走 Actions）");
            Assert.AreEqual(1, e.Actions.Count, "条件成立时单动作");
            Assert.AreEqual(ActionVerb.Bounce, e.Actions[0].Verb, "已横置 → 移回拥有者手牌");
            Assert.IsNotNull(e.Condition.ElseBranch, "必须声明 ElseBranch");
            Assert.AreEqual(1, e.Condition.ElseBranch.Count, "条件不成立时单动作");
            Assert.AreEqual(ActionVerb.Tap, e.Condition.ElseBranch[0].Verb, "未横置 → 横置该角色");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.IsLocalZone, "目标限定本地区");
            Assert.IsTrue(e.Target.CanTargetSelf,
                "卡文未排除自身（没写「另一个」）⇒ 可指定自身（与 JC008/JC069 同款约定；" +
                "否则它是本地区唯一角色时会被当作『无合法目标』而整条跳过）");
        }

        // ==================== ② 行为：未横置 → 横置；已横置 → 移回手牌 ====================

        /// <summary>未横置的目标角色：走 ElseBranch → 被横置（不变量：仍在场上）。</summary>
        public void TestK17_条件不成立则横置()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v1"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            Assert.IsFalse(victim.IsTapped, "前置：目标未横置");

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.IsTrue(victim.IsTapped, "条件（已横置）不成立 → 走 ElseBranch：横置目标");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "横置不移场：目标仍在争夺区");
        }

        /// <summary>已横置的目标角色：条件成立 → 走 Actions → 移回其拥有者手牌。</summary>
        public void TestK17_条件成立则移回手牌()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            victim.SetTapped(true);
            int handBefore = game.TurnManager.GetHandOf(2).Count;

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.AreEqual(CardZone.Hand, victim.CurrentZone, "条件（已横置）成立 → 走 Actions：移回**拥有者**（P2）手牌");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(2).Count, "P2 手牌 +1");
            Assert.AreEqual(2, victim.OwnerId, "回到拥有者（不是控制者）手上");
        }

        /// <summary>条件分支**不做半条效果**：已横置时只走"移回手牌"这一支——同地区其它角色不受影响（没有被横置）。</summary>
        public void TestK17_已横置时只走一支()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v3"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            victim.SetTapped(true);
            var bystander = game.CardManager.CreateCardInstance(Probe("TS_k17_v4"), 2);
            game.ZoneManager.MoveToArea(bystander, area, 2);

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.AreEqual(CardZone.Hand, victim.CurrentZone, "已横置 → 只执行移回手牌这一支");
            Assert.AreEqual(CardZone.AreaZone, bystander.CurrentZone, "旁观角色未被移动");
            Assert.IsFalse(bystander.IsTapped, "旁观角色未被横置（证明只执行了一支、没有落进 ElseBranch）");
        }

        // ==================== ③ 真实链路端到端：进场触发必须入堆叠并结算 ====================
        // （2026-09 回归：修复前 Predicate="已横置" 在**收集阶段**就把该触发丢掉 ⇒ 连堆叠都没有）

        /// <summary>端到端：JC004 进场 → 收集进场触发 → 排序窗 → 选本地区目标角色 → 入堆叠 → 结算。
        /// 目标未横置 → 走 `ElseBranch`：被横置。</summary>
        public void TestK17_JC004进场触发端到端_未横置则横置()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var view = new PickFirstTriggerView();
            game.TurnManager.SetTriggerView(view);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_e2e_v1"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            view.PreferUid = victim.UID;
            Assert.IsFalse(victim.IsTapped, "前置：目标未横置");

            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);   // 进场 → EnterTrigger

            Assert.IsFalse(game.StackManager.IsEmpty,
                "JC004 进场触发**必须进入堆叠**（修复前：条件门禁在收集阶段把它丢掉）");
            Assert.AreEqual(1, view.TargetCandidates.Count, "应弹过 1 次选目标窗");
            Assert.IsTrue(view.TargetCandidates[0].Contains(victim.UID), "候选含本地区的目标角色");

            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(victim.IsTapped, "未横置 → 走 ElseBranch：横置目标");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "横置不移场");
            Assert.AreEqual(CardZone.AreaZone, mage.CurrentZone, "力场法师留在场上");
        }

        /// <summary>端到端：目标**已横置** → 走 `Actions`：移回其拥有者手牌。</summary>
        public void TestK17_JC004进场触发端到端_已横置则移回手牌()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var view = new PickFirstTriggerView();
            game.TurnManager.SetTriggerView(view);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_e2e_v2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            victim.SetTapped(true);
            view.PreferUid = victim.UID;
            int handBefore = game.TurnManager.GetHandOf(2).Count;

            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            Assert.IsFalse(game.StackManager.IsEmpty, "JC004 进场触发必须进入堆叠");

            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.Hand, victim.CurrentZone, "已横置 → 走 Actions：移回拥有者手牌");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(2).Count, "P2 手牌 +1");
            Assert.AreEqual(2, victim.OwnerId, "回到拥有者（而非控制者）手上");
        }

        // ==================== ④ 网关（联机/本地主机权威）路径：用户实测走的就是这条 ====================

        /// <summary>
        /// 【用户实测回归】力场法师是本地区**唯一**的角色时，进场触发**仍要能产生效果**：
        /// `CanTargetSelf=true` ⇒ 候选＝它自己 → 选自己 → 走 `ElseBranch` 横置自己。
        /// （修复前：候选为空 ⇒ 触发被当"无合法目标"跳过 ⇒ 玩家看不到任何效果。）
        /// </summary>
        public void TestK17_JC004网关路径_独自在场时取自身为目标()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var gateway = new TriggerGateway();
            game.SetInputGateway(gateway);   // 触发排序/选目标走网关（＝权威异步路径）

            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);   // 进场 → EnterTrigger（本地区只有它自己）

            var targetReq = gateway.LastOf(Script.Net.ChoiceRequest.Kind.SelectTarget);
            Assert.IsNotNull(targetReq, "应下发选目标请求（本地区唯一角色＝它自己，也必须可被选）");
            Assert.IsTrue(targetReq.OptionUids.Contains(mage.UID), "候选必须包含力场法师自身（CanTargetSelf）");

            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(mage.IsTapped, "目标（自身）未横置 → 走 ElseBranch：横置自己");
            Assert.AreEqual(CardZone.AreaZone, mage.CurrentZone, "横置不移场");
        }

        /// <summary>网关路径 + 本地区另有角色：按回包选它 → 未横置则横置、已横置则回手。</summary>
        public void TestK17_JC004网关路径_按回包选择目标()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_gw_v"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var gateway = new TriggerGateway { PickTarget = _ => victim.UID };
            game.SetInputGateway(gateway);

            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            Assert.IsNotNull(gateway.LastOf(Script.Net.ChoiceRequest.Kind.SelectTarget), "应下发选目标请求");
            Assert.IsFalse(game.StackManager.IsEmpty, "选目标完成后应入堆叠");

            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.IsTrue(victim.IsTapped, "未横置 → 横置所选目标");
            Assert.IsFalse(mage.IsTapped, "力场法师自身不受影响（目标是回包所选的那张）");
        }

        // ==================== ⑤ 同一约定：其余"卡文未排除自身"的目标声明 ====================
        // （用户 2026-09 口径：卡文没写"另一个"/未用敌方限定 ⇒ 目标角色含自己；否则"独自在场"时
        //   候选为空 ⇒ 触发被静默跳过。与 JC008/JC069 的既有注释同款。）

        public void TestK17_同约定_其余卡目标声明含自身()
        {
            Cards.Effects.AllEffects.RegisterAll();
            var expect = new (string File, string EffectId)[]
            {
                ("JC017_chuzhoushi", "JC017_1_DestroyEnemyAttachmentsThenBarrier"),   // "目标角色"
                ("JC087_gelunidezousike", "JC087_1_LatentAllyThenPlaceMark"),          // "本地区的目标本方角色"
                ("JC105_emenglingzhu", "JC105_B_PayToSealDeadSpiritOnTarget"),         // "目标角色"
                ("JC055_annisiduola", "JC055_A_PreparePlaceTimeMarker"),               // "目标角色或附属"
                ("JC003_weimuhuwei", "JC003_1_Tap"),                                   // "横置目标角色或暗藏者"
            };
            foreach (var (file, id) in expect)
            {
                var tmpl = Load(file);
                var e = FindEffect(tmpl, id);
                Assert.IsNotNull(e, $"{file} 应有效果 {id}");
                Assert.IsTrue(e.Target?.CanTargetSelf == true,
                    $"{file} 的「{e.FaqNote ?? id}」卡文未排除自身 ⇒ CanTargetSelf 必须为 true" +
                    "（否则本地区只有它自己时该效果会因『无合法目标』被静默跳过）");
            }

            // JC019 的"此角色对位于本地区的目标非人类角色造成1点伤害"是**运行期授予宿主**的能力
            // （嵌在 `GrantedAbility` 里，不在卡面效果列表中），单独断言。
            var jc019 = Load("JC019_mierdundezhenmoqiang");
            var grant = FindEffect(jc019, "JC019_1_GrantBlackBladeAndAbility");
            Assert.IsNotNull(grant?.GrantedAbility, "JC019 应声明运行期授予的激活能力");
            Assert.IsTrue(grant.GrantedAbility.Target?.CanTargetSelf == true,
                "JC019 授予能力的目标『位于本地区的目标非人类角色』卡文未排除自身 ⇒ CanTargetSelf 必须为 true" +
                "（宿主若是人类会被 NotCurrentSubCategory 过滤，不会自伤）");
        }
    }
}
