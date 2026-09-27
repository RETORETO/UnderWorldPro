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
    /// 【K1·动态数值 X】按用户 2026-09 口径实现并锁定：
    ///  · 效果用 `EffectData.ValueExpr` 声明取值表达式，动作参数用占位符 `$X`（引擎替换，**不改模板数据**）；
    ///  · 已登记来源：`事件数量`（本次事件携带数量）、`计数:区域:…`、`计数:卡上标志:…`、`计数:地区标志:…`；
    ///    **`玩家选择`（2026-09 已交付）**＝玩家在**数值输入窗**里给出数字（JC083；范围由 `ValueMin/ValueMax` 声明，
    ///    声明 `ValueMin=0` 时 **0 合法且不套用"X=0 ⇒ 不发动"**）；未取值（无 UI）→ fail-closed；
    ///  · **X=0 时该效果不算发动**（用户明确；适用于"数量"型 X）：触发不收集、激活不可发动、结算直接跳过；
    ///    表达式未登记/依赖未实现同样按"不发动"处理（避免拼错静默算成 0 或全量）。
    /// 覆盖卡：**JC040 安普莎**（敌方弃牌 → 遏制X，X＝此次弃牌数量）、**JC097 阿斯旺**（袭击X，X＝你墓地中角色牌数）。
    /// </summary>
    public class P3DynamicValueTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 91 };
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

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = 3,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

        private static CardInstance InGraveyard(GameInitializer game, string uid, int ownerId,
            CardCategory category = CardCategory.Character)
        {
            var inst = game.CardManager.CreateCardInstance(Probe(uid, category), ownerId);
            game.ZoneManager.MoveToGraveyard(inst, ownerId);
            return inst;
        }

        private static EffectValueResolver.ValueContext Ctx(GameInitializer game, CardInstance src,
            CardInstance tgt = null, string areaUid = null, int eventAmount = -1)
            => new EffectValueResolver.ValueContext
            {
                Source = src,
                Target = tgt,
                TargetAreaId = areaUid,
                EventAmount = eventAmount,
                ZoneManager = game.ZoneManager,
                CardManager = game.CardManager,
                PlayerManager = game.PlayerManager,
                AreaManager = game.AreaManager,
            };

        // ==================== ① 求值器 ====================

        public void TestK1_求值器各来源()
        {
            var game = NewGame();
            var src = OnField(game, Probe("TS_k1_src"), 1, Areas(game)[0]);

            // 事件数量
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Ok,
                EffectValueResolver.Evaluate("事件数量", Ctx(game, src, null, null, 3), out int ev, out _),
                "『事件数量』应可求值");
            Assert.AreEqual(3, ev, "取本次事件数量");
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Fail,
                EffectValueResolver.Evaluate("事件数量", Ctx(game, src, null, null, -1), out _, out _),
                "事件数量缺失（非事件路径）→ fail-closed");

            // 区域计数（墓地角色牌：本方 2 张、对手 1 张）
            InGraveyard(game, "TS_k1_g1", 1);
            InGraveyard(game, "TS_k1_g2", 1);
            InGraveyard(game, "TS_k1_g3", 2);
            InGraveyard(game, "TS_k1_g4", 1, CardCategory.Attachment);
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Ok,
                EffectValueResolver.Evaluate("计数:区域:墓地:角色:自己", Ctx(game, src), out int mine, out _),
                "区域计数应可求值");
            Assert.AreEqual(2, mine, "『你墓地中角色牌的数量』＝2（不含对手、不含附属）");
            EffectValueResolver.Evaluate("计数:区域:墓地:角色:对手", Ctx(game, src), out int enemy, out _);
            Assert.AreEqual(1, enemy, "『对手墓地中角色牌的数量』＝1");

            // 卡上标志
            src.AddCounter("锁定标志", 2);
            EffectValueResolver.Evaluate("计数:卡上标志:锁定标志:自身", Ctx(game, src), out int marks, out _);
            Assert.AreEqual(2, marks, "『自身卡上锁定标志数』＝2");

            // 地区标志
            game.AreaManager.AddPowerMark(Areas(game)[0], 2, 3);
            EffectValueResolver.Evaluate("计数:地区标志:对手:本地区", Ctx(game, src), out int power, out _);
            Assert.AreEqual(3, power, "『本地区对手势力标志数』＝3");

            // 未登记 / 玩家选择（未取值）→ fail-closed（不发动）
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Fail,
                EffectValueResolver.Evaluate("随便写的表达式", Ctx(game, src), out _, out _), "未登记表达式 → fail-closed");
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Fail,
                EffectValueResolver.Evaluate("玩家选择", Ctx(game, src), out _, out _),
                "『玩家选择』**尚未取值**（无 UI / 未走数值输入窗）→ fail-closed");
            // 【K1·JC083】已取值时按玩家给出的数字求值（0 也合法：JC083 的 X 是"猜测值"而非"数量"）
            var ctxChosen = Ctx(game, src);
            ctxChosen.PlayerChosenValue = 0;
            ctxChosen.HasPlayerChosenValue = true;
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.Ok,
                EffectValueResolver.Evaluate("玩家选择", ctxChosen, out int chosen0, out _), "已取值 → Ok");
            Assert.AreEqual(0, chosen0, "取到的就是玩家选择的数字（0 合法）");
            Assert.AreEqual(EffectValueResolver.ValueEvalResult.NoExpr,
                EffectValueResolver.Evaluate("", Ctx(game, src), out _, out _), "空表达式＝无动态值");
        }

        // ==================== ② X=0 → 不发动 ====================

        public void TestK1_X为0时效果不发动()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());   // 触发阶段走网关（避免同步路径需要触发视图）
            var src = OnField(game, Probe("TS_k1_zero_src"), 1, Areas(game)[0]);

            // 结算兜底：X=0 且动作为"自身获得防御+1"（无目标）→ 不发动
            var e = new EffectData
            {
                EffectId = "TS_k1_zero_e",
                Timing = EffectTiming.OnSorceryCast,
                ValueExpr = "计数:区域:墓地:角色:自己",   // 墓地无角色 → 0
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "$X" } } },
            };
            int defBefore = src.CurrentDefense;
            EffectEngine.Instance.ResolveEffect(e, src, null);
            Assert.AreEqual(defBefore, src.CurrentDefense, "X=0 → 效果不发动（动作未执行）");

            // 带入栈路径：X 求值为 0 → 收集阶段即不收集（不发动）
            var zeroTmpl = new PlayerCardData
            {
                UID = "TS_k1_zero_watch", CardName = "探针_X0", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 3,
            };
            zeroTmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k1_zero_watch_e",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAnyCharacter" },
                ValueExpr = "计数:区域:墓地:角色:自己",   // 该控制者墓地无角色牌 → 0
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "$X" } } },
            });
            zeroTmpl._effectsInjected = true;
            OnField(game, zeroTmpl, 1, Areas(game)[0]);
            var victim = OnField(game, Probe("TS_k1_zero_victim"), 2, Areas(game)[0]);
            game.CardManager.DestroyInstance(victim);
            Assert.AreEqual(0, game.StackManager.Entries.Count, "X=0 → 触发在收集阶段即被拦下（不发动、不入堆叠）");

            // 对照：墓地里放了角色牌 → X>0 → 正常收集
            var game2 = NewGame();
            game2.SetInputGateway(new EnterAllGateway());
            var watcher2 = new PlayerCardData
            {
                UID = "TS_k1_pos_watch", CardName = "探针_X1", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 3,
            };
            watcher2.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_k1_pos_watch_e",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAnyCharacter" },
                ValueExpr = "计数:区域:墓地:角色:自己",
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "$X" } } },
            });
            watcher2._effectsInjected = true;
            OnField(game2, watcher2, 1, Areas(game2)[0]);
            InGraveyard(game2, "TS_k1_pos_g", 1);              // 本方墓地有 1 张角色牌 → X=1
            var v2 = OnField(game2, Probe("TS_k1_pos_victim"), 2, Areas(game2)[0]);
            game2.CardManager.DestroyInstance(v2);
            Assert.IsTrue(game2.StackManager.Entries.Count > 0, "X>0 → 正常收集入堆叠（对照）");
        }

        // ==================== ③ JC097 袭击X ====================

        public void TestK1_JC097_结构()
        {
            var tmpl = Load("JC097_asiwang");
            Assert.IsNotNull(tmpl, "JC097 模板应可加载");
            var e = FindEffect(tmpl, "JC097_1_StrikeByGraveyardCharacters");
            Assert.IsNotNull(e, "JC097 应有『袭击X』效果");
            Assert.AreEqual("计数:区域:墓地:角色:自己", e.ValueExpr, "X 来源＝你墓地中角色牌的数量");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e.Timing, "袭击＝现身触发");
            Assert.AreEqual(ActionVerb.DealDamage, e.Actions[0].Verb, "动作为造成伤害");
            Assert.AreEqual("$X", e.Actions[0].Args[0], "伤害量用占位符 $X");
            Assert.IsTrue(tmpl.HasInherentKeyword("隐匿"), "仍保留【隐匿】词条");
        }

        public void TestK1_JC097_按墓地角色数造成伤害()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var asiwang = OnField(game, Load("JC097_asiwang"), 1, area);
            var target = OnField(game, Probe("TS_k1_97_tgt"), 2, area);
            InGraveyard(game, "TS_k1_97_g1", 1);
            InGraveyard(game, "TS_k1_97_g2", 1);

            var e = FindEffect(asiwang.PlayerTemplate, "JC097_1_StrikeByGraveyardCharacters");
            EffectEngine.Instance.ResolveEffect(e, asiwang, target);

            Assert.AreEqual(2, target.Damage, "X＝墓地角色牌数 2 → 造成 2 点伤害（$X 已替换）");
        }

        // ==================== ④ JC040 遏制X（事件数量来源） ====================

        public void TestK1_JC040_结构()
        {
            var tmpl = Load("JC040_anpushamingfudeshinv");
            Assert.IsNotNull(tmpl, "JC040 模板应可加载");
            var e = FindEffect(tmpl, "JC040_1_CurbByDiscardCount");
            Assert.IsNotNull(e, "JC040 应有『遏制X』效果");
            Assert.AreEqual(EffectTiming.OnDiscard, e.Timing, "触发时机＝弃牌");
            Assert.AreEqual("DiscardByEnemy", e.Condition.Predicate, "条件＝敌方玩家弃牌");
            Assert.AreEqual("事件数量", e.ValueExpr, "X＝此次弃牌的数量");
            Assert.AreEqual(ActionVerb.Curb, e.Actions[0].Verb, "动作为遏制");
            Assert.AreEqual("$X", e.Actions[0].Args[0], "遏制量用占位符 $X");
        }

        /// <summary>端到端：敌方弃 2 张 → 遏制2（从其势力标志中移除 2 个）。</summary>
        public void TestK1_JC040_按弃牌数遏制()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var anpusa = OnField(game, Load("JC040_anpushamingfudeshinv"), 1, area);

            // 对手（弃牌者）在某地区有 3 个势力标志；该地区需要 ≥2 个地区才能验证"按地区分配"（此处 1 个地区够用）
            game.AreaManager.AddPowerMark(area, 2, 3);
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(area, 2), "前置：对手 3 个标志");

            var e = FindEffect(anpusa.PlayerTemplate, "JC040_1_CurbByDiscardCount");
            // 模拟"敌方玩家（P2）弃了 2 张牌"的触发结算：winnerId 槽位＝本次触发的引发者（弃牌者 P2），
            // 末参＝本次事件数量（由 `TriggerContext.ExtraInt` → `StackEntry.EventAmount` 透传）＝2。
            EffectEngine.Instance.ResolveEffect(e, anpusa, null, area, 2, -1, 2);

            Assert.AreEqual(1, game.AreaManager.GetPowerMark(area, 2), "按弃牌数 2 遏制 → 对手标志 3 → 1");
        }

        public void TestK1_JC040_弃牌数为0时不发动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var anpusa = OnField(game, Load("JC040_anpushamingfudeshinv"), 1, area);
            game.AreaManager.AddPowerMark(area, 2, 3);

            var e = FindEffect(anpusa.PlayerTemplate, "JC040_1_CurbByDiscardCount");
            EffectEngine.Instance.ResolveEffect(e, anpusa, null, area, 2, -1, 0);   // 引发者=P2（敌方），事件数量＝0

            Assert.AreEqual(3, game.AreaManager.GetPowerMark(area, 2), "X=0 → 不发动（对手标志不变）");
        }

        /// <summary>触发排序全部入堆叠的网关（本套件触发阶段不弹窗）。</summary>
        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                if (req?.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                else if (req?.OptionUids != null && req.OptionUids.Count > 0)
                    r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }
    }
}
