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
    /// 【JC057 监控员②·持续可见性授权】＋【鼠标悬浮预览】的可见性判定。
    ///
    /// 卡文：「持续：**只要监控员未横置，你可以随时检视本地区的任一暗藏者**。」
    /// 用户 2026-09 口径（本轮答复）：
    ///  · **所有可见卡**鼠标悬浮都弹"整张卡图"的悬浮 UI；
    ///  · **自己的暗藏者**也按同样方式弹；
    ///  · **正常情况下**悬浮在**对手的暗藏者**或**手牌**上**不弹**；
    ///  · **但**若（查看者有一张）**JC057 在场且未横置**，悬浮**它所在地区的敌方暗藏者**时**可以弹**（显示卡图）。
    ///
    /// 判定单一真相源＝`CovertInspectRule.CanViewerSeeCardFace`（静态纯函数：读卡面印刷规则 ＋ 实例态，
    /// 单机/联机客户端/服务端同解，不依赖 EffectEngine 实例）；"随时检视"这种**不经过效果结算**的查看通道
    /// 由 UI 悬浮预览承担（`CardUI` → `CardPreviewBus` → `CardHoverPreview`）。
    /// </summary>
    public class P4CovertInspectTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 570 };
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>把一个暗藏者放到某地区（`IsCovert=true` = 背面朝上）。</summary>
        private static CardInstance CovertOnField(GameInitializer game, int ownerId, string area, string uid)
        {
            var card = game.CardManager.CreateCardInstance(Probe(uid), ownerId);
            game.ZoneManager.MoveToArea(card, area, ownerId);
            card.SetCovert(true);
            return card;
        }

        // ==================== ① 结构：JC057 声明了"随时检视本地区任暗藏者"的持续静止规则 ====================

        public void TestJC057_结构()
        {
            var tmpl = Load("JC057_jiankongyuan");

            var inspect = FindEffect(tmpl, "JC057_1_InspectTargetPlayerHand");
            Assert.IsNotNull(inspect, "JC057 ①：现身触发检视目标玩家手牌");
            Assert.AreEqual(EffectTiming.UncoverTrigger, inspect.Timing, "现身触发");
            Assert.AreEqual(TargetKind.Player, inspect.Target.Kind, "目标＝玩家");
            Assert.AreEqual(ActionVerb.Inspect, inspect.Actions[0].Verb, "动作＝检视");

            var cont = FindEffect(tmpl, "JC057_2_InspectLocalCovertAnyTime");
            Assert.IsNotNull(cont, "JC057 ②：持续随时检视本地区任一暗藏者");
            Assert.AreEqual(EffectTiming.Static, cont.Timing, "静止规则（持续生效，不结算）");
            Assert.AreEqual(ZoneScope.AreaZone, cont.Scope, "在争夺区生效");
            Assert.IsNotNull(cont.PrereqSpec, "有前提声明");
            Assert.AreEqual(PrereqKind.SelfUntapped, cont.PrereqSpec.Kind, "前提＝来源**未横置**");
            Assert.AreEqual(1, cont.StaticEffects.Count, "一条静止规则");
            Assert.AreEqual(StaticKind.AllowInspectLocalCovert, cont.StaticEffects[0].Kind, "种类＝本地区暗藏者检视授权");
        }

        // ==================== ② 可见性判定：自己的卡一律可见 ====================

        public void TestJC057_自己的卡一律可见()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();

            var ownHand = game.CardManager.CreateCardInstance(Probe("TS_ci_hand1"), 1);
            game.ZoneManager.MoveToHand(ownHand, 1);
            var ownCovert = CovertOnField(game, 1, areas[0], "TS_ci_cov1");

            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(ownHand, 1, game.CardManager),
                "**自己的手牌**可见（悬浮预览可弹）");
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(ownCovert, 1, game.CardManager),
                "**自己的暗藏者**可见（用户口径：自己的暗藏者也可以弹出悬浮UI）");
        }

        // ==================== ③ 对手的隐藏信息默认不可见 ====================

        public void TestJC057_对手手牌与暗藏者默认不可见()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();

            var enemyHand = game.CardManager.CreateCardInstance(Probe("TS_ci_hand2"), 2);
            game.ZoneManager.MoveToHand(enemyHand, 2);
            var enemyCovert = CovertOnField(game, 2, areas[0], "TS_ci_cov2");
            var enemyFaceUp = game.CardManager.CreateCardInstance(Probe("TS_ci_open2"), 2);
            game.ZoneManager.MoveToArea(enemyFaceUp, areas[0], 2);

            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyHand, 1, game.CardManager),
                "**对手手牌**不可见（不弹悬浮UI）");
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "**对手暗藏者**默认不可见（无 JC057 授权时）");
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyFaceUp, 1, game.CardManager),
                "对手的**场上正面卡**是公开信息 → 可见（用户口径：所有可见卡都弹）");
        }

        // ==================== ④ JC057 在场且未横置、且同地区 → 授权检视敌方暗藏者 ====================

        public void TestJC057_未横置同地区可检视敌方暗藏者()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(jc057, areas[0], 1);
            var enemyCovert = CovertOnField(game, 2, areas[0], "TS_ci_cov3");

            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "JC057 未横置且与被检视暗藏者**同地区** → 授权可见（可弹卡图）");
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 2, game.CardManager),
                "对该暗藏者自己的控制者而言它本就是自己的卡 → 同样可见（不构成隐藏信息）");
        }

        public void TestJC057_横置即失去授权_重置恢复()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(jc057, areas[0], 1);
            var enemyCovert = CovertOnField(game, 2, areas[0], "TS_ci_cov4");

            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager), "前置：未横置 → 可见");

            jc057.SetTapped(true);
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "**横置**后失去授权（卡文『只要监控员未横置』）");
            Assert.IsFalse(CovertInspectRule.HasPrintedLocalCovertInspect(jc057), "静态判定同步为假");

            jc057.SetTapped(false);
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "**重置**后授权恢复（可反复开关）");
        }

        public void TestJC057_异地区不授权()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(jc057, areas[0], 1);
            var enemyCovert = CovertOnField(game, 2, areas[1], "TS_ci_cov5");   // 对手暗藏者在**另一地区**

            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "授权限『**本地区**』：异地区的敌方暗藏者仍不可见");

            // 把 JC057 机动到该地区 → 授权成立
            game.ZoneManager.MoveToArea(jc057, areas[1], 1);
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "JC057 移到该地区后 → 授权成立（实时按所在地判定）");
        }

        public void TestJC057_离场后不授权()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(jc057, areas[0], 1);
            var enemyCovert = CovertOnField(game, 2, areas[0], "TS_ci_cov6");
            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager), "前置：可见");

            game.ZoneManager.MoveToHand(jc057, 1);   // 离开争夺区（对手隐藏信息不再被授权）
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(enemyCovert, 1, game.CardManager),
                "JC057 不在场 → 授权消失（授权要求来源在**争夺区**）");
        }

        public void TestJC057_只授权给其控制者()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var jc057 = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(jc057, areas[0], 1);
            // 玩家2 的暗藏者（同地区）：玩家1 可看（授权），玩家2 自己也可看（自己的卡）
            var own2 = CovertOnField(game, 2, areas[0], "TS_ci_cov7");
            // 玩家1 的暗藏者（同地区）：玩家2 **不**获得授权（JC057 属玩家1）
            var own1 = CovertOnField(game, 1, areas[0], "TS_ci_cov8");

            Assert.IsTrue(CovertInspectRule.CanViewerSeeCardFace(own2, 1, game.CardManager), "玩家1 经 JC057 授权检视对手暗藏者");
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(own1, 2, game.CardManager),
                "玩家2 **不**因玩家1 的 JC057 获得授权（授权只给其控制者）");
        }

        // ==================== ⑤ 判定边界（fail-closed） ====================

        public void TestJC057_判定边界()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var covert = CovertOnField(game, 2, areas[0], "TS_ci_cov9");

            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(null, 1, game.CardManager), "空卡 → 不可见");
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(covert, 0, game.CardManager), "查看者非法（≤0）→ 不可见");
            Assert.IsFalse(CovertInspectRule.CanViewerSeeCardFace(covert, 1, null), "无 CardManager → 不授权（fail-closed）");
            Assert.IsFalse(CovertInspectRule.IsInspectAuthorized(covert, 1, null), "无 CardManager 时授权判定不成立");
        }

        // ==================== ⑥ 悬浮预览总线：装配/反装配生命周期（未装配时悬浮请求被静默忽略） ====================

        public void TestJC057_悬浮预览总线生命周期()
        {
            // 未接管：判定/浮层皆空 → 悬浮请求无副作用（不应抛异常）
            Script.UI.CardPreviewBus.Detach(null);
            Assert.IsTrue(Script.UI.CardPreviewBus.CanPreview == null, "未接管时判定为空");
            Script.UI.CardPreviewBus.RequestHover(null);
            Script.UI.CardPreviewBus.EndHover(null);
            Assert.IsTrue(Script.UI.CardPreviewBus.CanPreview == null, "无接管时悬浮请求被静默忽略");

            // 接管（浮层留空＝模拟"没有浮层控件"的 headless 场景）：判定可被记录，但不显示任何东西
            int calls = 0;
            Script.UI.CardPreviewBus.Attach(card => { calls++; return true; }, null, viewerPlayerId: 1);
            Assert.IsNotNull(Script.UI.CardPreviewBus.CanPreview, "接管后判定非空");
            Assert.AreEqual(1, Script.UI.CardPreviewBus.ViewerPlayerId, "记录查看者座位");

            Script.UI.CardPreviewBus.Detach(null);
            Assert.IsTrue(Script.UI.CardPreviewBus.CanPreview == null, "Detach 后判定清空（视图离场不留悬挂引用）");
            Assert.AreEqual(0, calls, "无浮层时不触发判定（避免无谓的规则查询）");
        }
    }
}
