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
    /// 【P3·A】`ActionVerb.RemoveToken`（移除**地区**势力标志）+ JC095 唐·格鲁尼 的进场触发。
    /// 口径（用户 2026-09）：**直接移除（不由对手分配）**、**只作用于指定地区**（"本地区"缺省），
    /// 与遏制X（`Curb`：X&lt;总数时被遏制者按地区分配、范围全场）区分。
    /// </summary>
    public class P3RemoveTokenTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 41 };
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① RemoveToken 原语 ====================

        /// <summary>缺省＝移除"本地区 + 对手"的全部标志，且**不影响其它地区**。</summary>
        public void TestRemoveToken_移除本地区对手全部标志()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            var src = game.CardManager.CreateCardInstance(Probe("TS_rt_src"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);

            // 注意 `AddPowerMark` 带"净差逻辑"（新增先抵扣对方）→ 这里只布置对手标志，避免互相抵扣
            game.AreaManager.AddPowerMark(a1, 2, 3);
            game.AreaManager.AddPowerMark(a2, 2, 2);   // 另一地区的标志：不应被影响
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(a1, 2), "前置：a1 对手 3 个标志");

            var e = new EffectData
            {
                EffectId = "TS_rt_e",
                Timing = EffectTiming.CharacterActivated,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "对手", "本地区" },
                    },
                },
            };
            EffectEngine.Instance.ResolveEffect(e, src, null, a1);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 2), "本地区对手标志被全部移除");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 1), "本方标志数不变（原为 0，未被凭空增加）");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(a2, 2), "**其它地区**的标志不受影响（卡文限定本地区）");
        }

        /// <summary>指定数量：只移除 N 个（钳制到该地区该玩家的现有数）。</summary>
        public void TestRemoveToken_指定数量()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];
            var src = game.CardManager.CreateCardInstance(Probe("TS_rt_src2"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            game.AreaManager.AddPowerMark(a1, 2, 2);

            var e = new EffectData
            {
                EffectId = "TS_rt_e2",
                Timing = EffectTiming.CharacterActivated,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "5", "对手", "本地区" },   // 5 > 现有 2 → 钳到 2
                    },
                },
            };
            EffectEngine.Instance.ResolveEffect(e, src, null, a1);
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 2), "指定数量超过现有数 → 按现有数移除");
        }

        /// <summary>归属＝自己：移除本方标志（不影响对手）。</summary>
        public void TestRemoveToken_归属自己()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];
            var src = game.CardManager.CreateCardInstance(Probe("TS_rt_src3"), 1);
            game.ZoneManager.MoveToArea(src, a1, 1);
            game.AreaManager.AddPowerMark(a1, 1, 2);
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(a1, 1), "前置：a1 本方 2 个标志");

            var e = new EffectData
            {
                EffectId = "TS_rt_e3",
                Timing = EffectTiming.CharacterActivated,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "1", "自己", "本地区" },
                    },
                },
            };
            EffectEngine.Instance.ResolveEffect(e, src, null, a1);
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(a1, 1), "本方标志被移除 1 个");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 2), "对手标志不受影响（原为 0）");
        }

        // ==================== ② JC095 结构 + 端到端 ====================

        public void TestRemoveToken_JC095结构()
        {
            var tmpl = Load("JC095_tanggeluni");
            Assert.IsNotNull(tmpl, "JC095 模板应可加载");
            var e = FindEffect(tmpl, "JC095_1_MoveEnemyMarksToSelf");
            Assert.IsNotNull(e, "JC095 应有进场触发效果");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual("计数:地区标志:对手:本地区", e.ValueExpr, "X＝本地区对手势力标志数");
            Assert.AreEqual(2, e.Actions.Count, "两个动作：先移除、再放置等量");
            Assert.AreEqual(ActionVerb.RemoveToken, e.Actions[0].Verb, "动作 1＝移除地区势力标志");
            Assert.AreEqual("全部", e.Actions[0].Args[1], "移除全部");
            Assert.AreEqual(ActionVerb.PlaceToken, e.Actions[1].Verb, "动作 2＝放置势力标志");
            Assert.AreEqual("$X", e.Actions[1].Args[1], "放置数量＝$X（等量）");
        }

        /// <summary>端到端：本地区 3 个敌方标志 → 全部移除并换成 3 个本方标志；其它地区不动。</summary>
        public void TestRemoveToken_JC095_等量转换()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            var dong = game.CardManager.CreateCardInstance(Load("JC095_tanggeluni"), 1);
            game.ZoneManager.MoveToArea(dong, a1, 1);

            game.AreaManager.AddPowerMark(a1, 2, 3);
            game.AreaManager.AddPowerMark(a2, 2, 2);
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(a1, 2), "前置：a1 敌方 3 个标志");

            var e = FindEffect(dong.PlayerTemplate, "JC095_1_MoveEnemyMarksToSelf");
            EffectEngine.Instance.ResolveEffect(e, dong, null, a1);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 2), "本地区敌方标志清空");
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(a1, 1), "放置等量（3 个）本方标志");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(a2, 2), "其它地区的敌方标志不受影响");
        }

        /// <summary>X=0（本地区无敌方标志）→ 按 K1 口径该效果不发动（不移除也不放置）。</summary>
        public void TestRemoveToken_JC095_X为0不发动()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];
            var dong = game.CardManager.CreateCardInstance(Load("JC095_tanggeluni"), 1);
            game.ZoneManager.MoveToArea(dong, a1, 1);

            game.AreaManager.AddPowerMark(a1, 1, 2);   // 只有本方标志
            var e = FindEffect(dong.PlayerTemplate, "JC095_1_MoveEnemyMarksToSelf");
            EffectEngine.Instance.ResolveEffect(e, dong, null, a1);

            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 2), "敌方标志本来就没有（X=0）");
            Assert.AreEqual(2, game.AreaManager.GetPowerMark(a1, 1), "X=0 → 不发动，本方标志没有被追加");
        }
    }
}
