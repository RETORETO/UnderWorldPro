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
    /// 【K10·额外费用 · 暗藏者现身】用户 2026-09 口径：**现身同样要校验额外费用**
    /// （"没有特殊说明的情况下需要校验额外费用，JC 卡包内是都需要校验"）。
    ///  · 与**打出**共用同一份实现 `TurnManager.TryResolveExtraCost`（单一真相源，不分叉）：
    ///    `RevealHandCards`（JC081，**可选**）→ 现身时展示手牌同样**减免本次现身费用**；不展示＝全额；
    ///    `SacrificeCharacter`（**必须支付**）→ 指定候选必须合法，无候选 → **拒绝现身**；
    ///    `PayTargetCost`（**必须支付**，需先选目标）→ 现身路径无目标交互 ⇒ **拒绝现身**（fail-closed，绝不白放行）。
    ///  · 单机交互＝与打出 1:1 的同款窗口（多选展示窗 / 牺牲候选窗）；
    ///  · 联机＝服务端接管（`RevealCovertWithExtraCostAsync`：`SelectHandCards` / `SelectTarget`）。
    /// </summary>
    public class P4RevealExtraCostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 815 };
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

        /// <summary>探针角色（可暗藏），可指定额外费用。</summary>
        private static PlayerCardData CharProbe(string uid, int cost, ExtraCostSpec extra)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = CardCategory.Character, Defense = 5,
            };
            tmpl.ExtraCost = extra;
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>给玩家放 n 张未横置资产（可选颜色 → 提供忠诚；JC081 需要【白】3 忠诚）。</summary>
        private static void GiveMana(GameInitializer game, int playerId, int n, CardColor? color = null)
        {
            for (int i = 0; i < n; i++)
            {
                var probe = new PlayerCardData
                {
                    UID = $"TS_rec_mana_{playerId}_{i}", CardName = $"探针资产{i}", Cost = 0,
                    CategoryFlag = CardCategory.Attachment, Defense = -1, _effectsInjected = true,
                };
                if (color.HasValue) probe.Colors.Add(color.Value);
                game.ZoneManager.MoveToAsset(game.CardManager.CreateCardInstance(probe, playerId), playerId);
            }
        }

        /// <summary>把手牌里的暗藏者放到某地区并翻面为暗藏者（＝暗藏状态）。</summary>
        private static CardInstance DeployedCovert(GameInitializer game, CardInstance card, int playerId, string area)
        {
            game.ZoneManager.MoveToArea(card, area, playerId);
            card.SetCovert(true);
            return card;
        }

        private static void Ready(GameInitializer game, int playerId)
        {
            game.TurnManager.FirstPlayerId = playerId;
            game.TurnManager.PriorityPlayerId = playerId;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
        }

        // ==================== ① JC081：现身时展示手牌 → 减免现身费用 ====================

        public void TestRevealExtraCost_JC081展示手牌减免现身费用()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 8, CardColor.White);
            var jc081 = game.CardManager.CreateCardInstance(Load("JC081_yaluoer"), 1);
            game.ZoneManager.MoveToHand(jc081, 1);
            DeployedCovert(game, jc081, 1, areas[0]);

            // 手牌里的合格牌（组织:圣贤 → 命中 RevealFilter）
            var sageTmpl = new PlayerCardData
            {
                UID = "TS_rec_sage", CardName = "探针圣贤", Cost = 0,
                CategoryFlag = CardCategory.Sorcery, Organization = "圣贤", Defense = -1, _effectsInjected = true,
            };
            var sage = game.CardManager.CreateCardInstance(sageTmpl, 1);
            game.ZoneManager.MoveToHand(sage, 1);

            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, jc081.UID, null, new List<string> { sage.UID });

            Assert.IsTrue(ok, "带额外费用选择的现身应成功");
            Assert.IsFalse(jc081.IsCovert, "已现身（正面朝上）");
            Assert.AreEqual(manaBefore - (7 - 1), game.PlayerManager.GetAvailableMana(1),
                "现身费用 ＝ 印刷 7 − 展示合格 1 张减免 1 ＝ 6（**现身同样校验额外费用**）");
        }

        public void TestRevealExtraCost_JC081不展示则全额现身()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 8, CardColor.White);
            var jc081 = game.CardManager.CreateCardInstance(Load("JC081_yaluoer"), 1);
            game.ZoneManager.MoveToHand(jc081, 1);
            DeployedCovert(game, jc081, 1, areas[0]);
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, jc081.UID);   // 不展示

            Assert.IsTrue(ok, "该额外费用**可选**（不展示＝全额支付，照样能现身）");
            Assert.AreEqual(manaBefore - 7, game.PlayerManager.GetAvailableMana(1), "全额支付 7");
        }

        public void TestRevealExtraCost_展示候选不在手牌则拒绝现身()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 8, CardColor.White);
            var jc081 = game.CardManager.CreateCardInstance(Load("JC081_yaluoer"), 1);
            game.ZoneManager.MoveToHand(jc081, 1);
            DeployedCovert(game, jc081, 1, areas[0]);
            // 场上卡（不在手牌）冒充"展示候选"
            var onField = game.CardManager.CreateCardInstance(
                CharProbe("TS_rec_onfield", 0, null), 1);
            game.ZoneManager.MoveToArea(onField, areas[0], 1);
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, jc081.UID, null, new List<string> { onField.UID });

            Assert.IsFalse(ok, "展示候选不在手牌 → 拒绝现身（fail-closed，与打出同口径）");
            Assert.IsTrue(jc081.IsCovert, "仍是暗藏状态");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
        }

        // ==================== ② 必须支付类：现身路径无法满足 → 拒绝现身 ====================

        public void TestRevealExtraCost_PayTargetCost形态拒绝现身()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 9);
            var probe = game.CardManager.CreateCardInstance(
                CharProbe("TS_rec_paytarget", 3, new ExtraCostSpec
                {
                    Kind = ExtraCostKind.PayTargetCost,
                    Mandatory = true,
                }), 1);
            game.ZoneManager.MoveToHand(probe, 1);
            DeployedCovert(game, probe, 1, areas[0]);
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, probe.UID);

            Assert.IsFalse(ok, "额外费用需先选目标、现身路径无目标交互 → **拒绝现身**（绝不放行未付额外费用的现身）");
            Assert.IsTrue(probe.IsCovert, "仍是暗藏状态");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");
        }

        public void TestRevealExtraCost_牺牲代价按所选定落地()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var probe = game.CardManager.CreateCardInstance(
                CharProbe("TS_rec_sac", 2, new ExtraCostSpec
                {
                    Kind = ExtraCostKind.SacrificeCharacter,
                    Mandatory = true,
                }), 1);
            game.ZoneManager.MoveToHand(probe, 1);
            DeployedCovert(game, probe, 1, areas[0]);
            var allyA = game.CardManager.CreateCardInstance(CharProbe("TS_rec_a", 0, null), 1);
            game.ZoneManager.MoveToArea(allyA, areas[0], 1);
            var allyB = game.CardManager.CreateCardInstance(CharProbe("TS_rec_b", 0, null), 1);
            game.ZoneManager.MoveToArea(allyB, areas[0], 1);
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, probe.UID, allyB.UID);

            Assert.IsTrue(ok, "指定合法牺牲对象 → 现身成功");
            Assert.AreEqual(CardZone.Graveyard, allyB.CurrentZone, "按**所选**牺牲（不是兜底第一个）");
            Assert.AreEqual(CardZone.AreaZone, allyA.CurrentZone, "未被选者不受影响");
            Assert.IsFalse(probe.IsCovert, "已现身");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "基础费用照付");
        }

        public void TestRevealExtraCost_无牺牲对象则拒绝现身()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var probe = game.CardManager.CreateCardInstance(
                CharProbe("TS_rec_sac2", 2, new ExtraCostSpec
                {
                    Kind = ExtraCostKind.SacrificeCharacter,
                    Mandatory = true,
                }), 1);
            game.ZoneManager.MoveToHand(probe, 1);
            DeployedCovert(game, probe, 1, areas[0]);   // 场上没有其它本方角色可牺牲
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, probe.UID);

            Assert.IsFalse(ok, "无合法牺牲对象 → 拒绝现身（必须支付的额外费用无法满足）");
            Assert.IsTrue(probe.IsCovert, "仍是暗藏状态");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
        }

        // ==================== ③ 无额外费用：现身行为不变（回归） ====================

        public void TestRevealExtraCost_无额外费用行为不变()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            var probe = game.CardManager.CreateCardInstance(CharProbe("TS_rec_plain", 2, null), 1);
            game.ZoneManager.MoveToHand(probe, 1);
            DeployedCovert(game, probe, 1, areas[0]);
            Ready(game, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovert(1, probe.UID);

            Assert.IsTrue(ok, "无额外费用 → 照常现身");
            Assert.IsFalse(probe.IsCovert, "已现身");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "只付基础费用 2");
        }

        // ==================== ④ 联机：服务端接管（选择回环） ====================

        private sealed class RevealGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public string HandAnswer;     // SelectHandCards 回包（null＝首个候选）

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
                Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                switch (req?.RequestKind)
                {
                    case Script.Net.ChoiceRequest.Kind.SelectHandCards:
                        string pick = HandAnswer
                            ?? (req.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                        if (!string.IsNullOrEmpty(pick)) r.SelectedUids.Add(pick);
                        r.Confirmed = true;
                        break;
                    default:
                        r.SelectedUid = req?.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null;
                        break;
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        public void TestRevealExtraCost_联机接管展示手牌()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 8, CardColor.White);
            var jc081 = game.CardManager.CreateCardInstance(Load("JC081_yaluoer"), 1);
            game.ZoneManager.MoveToHand(jc081, 1);
            DeployedCovert(game, jc081, 1, areas[0]);
            var sageTmpl = new PlayerCardData
            {
                UID = "TS_rec_sage2", CardName = "探针圣贤2", Cost = 0,
                CategoryFlag = CardCategory.Sorcery, Organization = "圣贤", Defense = -1, _effectsInjected = true,
            };
            var sage = game.CardManager.CreateCardInstance(sageTmpl, 1);
            game.ZoneManager.MoveToHand(sage, 1);
            Ready(game, 1);

            var gw = new RevealGateway { HandAnswer = sage.UID };
            game.TurnManager.InputGateway = gw;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            bool ok = game.TurnManager.RevealCovertWithExtraCostAsync(1, jc081.UID).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "联机接管流程应完成");
            Assert.AreEqual(1, gw.Requests.Count, "发了一次展示手牌请求");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectHandCards, gw.Requests[0].RequestKind,
                "请求种类＝展示任意数量手牌（与打出同款）");
            Assert.IsTrue(gw.Requests[0].OptionUids.Contains(sage.UID), "候选含手牌里的合格牌");
            Assert.IsFalse(gw.Requests[0].OptionUids.Contains(jc081.UID),
                "候选**不含本卡自身**（防自我减费）");
            Assert.IsFalse(jc081.IsCovert, "现身成功");
            Assert.AreEqual(manaBefore - (7 - 1), game.PlayerManager.GetAvailableMana(1), "展示 1 张 → 现身费用 7−1=6");
        }

        public void TestRevealExtraCost_联机接管_无选择类额外费用不接管()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            GiveMana(game, 1, 5);
            GiveMana(game, 1, 8, CardColor.White);   // JC081 需要【白】3 忠诚
            var probe = game.CardManager.CreateCardInstance(CharProbe("TS_rec_plain2", 2, null), 1);
            game.ZoneManager.MoveToHand(probe, 1);
            DeployedCovert(game, probe, 1, areas[0]);
            game.TurnManager.InputGateway = new RevealGateway();

            Assert.IsFalse(game.PlayerActionService.TryBeginRevealCovertExtraCostOnline(1, probe.UID),
                "无额外费用 → 不接管（走既有同步现身）");

            var jc081 = game.CardManager.CreateCardInstance(Load("JC081_yaluoer"), 1);
            game.ZoneManager.MoveToHand(jc081, 1);
            DeployedCovert(game, jc081, 1, areas[1]);
            Assert.IsTrue(game.PlayerActionService.TryBeginRevealCovertExtraCostOnline(1, jc081.UID),
                "有额外费用＋已注入网关 → 服务端接管");
        }
    }
}
