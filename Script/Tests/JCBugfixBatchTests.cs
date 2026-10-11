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
    /// 【2026-10 用户实测 BUG 批】五条实测缺陷的回归单测（一条一测，先冻结"用户看到的现象"）：
    ///  ① 声望去重：一个地区内多个声望角色，一次势力对抗广播只放 **1** 个势力标志（对齐威名）；
    ///  ② JC078 光能盾必须能以 **JC081 亚洛尔** 为目标（"除装备与祝福外不受附属"只约束**附属**）；
    ///  ③ JC082 索亚之书必须能正确**结附于本方秘社**（不得直接进墓地）；
    ///  ④ JC083 睿智的斯芬克司的"准备步骤：选择一个数字"必须真的触发并弹出数值窗；
    ///  ⑤ JC073 授予宿主的【屏障】必须拦住『袭击』的**目标选择**（宿主不得出现在候选里）。
    /// </summary>
    public class JCBugfixBatchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 基础工具 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(2);
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

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, area, playerId);
            return inst;
        }

        private static PlayerCardData Probe(string uid, int strike = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            if (strike > 0)
            {
                var s = KeywordEffects.Strike(strike);
                s.EffectId = uid + "_strike";
                tmpl.EffectDataList.Add(s);
            }
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_bug_asset_{playerId}_{i}", CardName = "探针资产",
                    Cost = 0, CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl.Colors.Add(color);
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>连续让过直到堆叠清空（触发/效果结算收尾）。</summary>
        private static void Drain(GameInitializer game)
        {
            for (int i = 0; i < 60 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static void AttachToHost(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? FirstArea(game), attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }
        // ==================== ① 声望去重 ====================

        /// <summary>
        /// 【BUG 1】一个地区内 P1 有两个声望角色、P2 没有 ⇒ 一次"势力对抗结束"广播只应放 **1** 个势力标志。
        /// 修复前：每个声望角色的触发各放一个 ⇒ +2（用户实测）。
        /// </summary>
        public void TestBug1_多个声望角色一次势力对抗只放一个标志()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var r1 = OnField(game, Load("JC070_qianxinxiushi"), 1, area);   // 虔心修士：公开 + 声望
            var r2 = OnField(game, Load("JC076_wudaosengren"), 1, area);    // 悟道僧人：声望
            var e1 = OnField(game, Probe("TS_rn_e1"), 2, area);

            int before = game.AreaManager.GetPowerMark(area, 1);
            game.TurnManager.EmitPowerCombatEnded(area);
            Drain(game);

            int after = game.AreaManager.GetPowerMark(area, 1);
            Assert.AreEqual(before + 1, after,
                "同一势力对抗广播内，多个声望角色只放 1 个势力标志（去重与威名同款）");
        }

        // ==================== ② JC078 以 JC081 为目标 ====================

        /// <summary>
        /// 【BUG 2】JC078 光能盾（事务卡·法术）必须能以 JC081 亚洛尔为目标：
        /// JC081 的"除装备与祝福外不受附属"（`OnlyAttachWithSubCategories`）只约束**附属**，
        /// 不是"不能被卡牌指定为目标"。
        /// </summary>
        public void TestBug2_JC078可以以JC081为目标()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);
            var spell = game.CardManager.CreateCardInstance(Load("JC078_guangnengdun"), 1);
            game.ZoneManager.MoveToHand(spell, 1);

            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(spell, jc081),
                "JC078（事务卡）应能以 JC081 为目标：白名单只限制『结附』");

            // 结算：护盾登记在 JC081 上（防止伤害通道）
            var e = FindEffect(spell.PlayerTemplate, "JC078_A_PreventAllDamage");
            Assert.IsNotNull(e, "JC078 应有『防止伤害』效果");
            EffectEngine.Instance.ResolveEffect(e, spell, jc081, area);
            Assert.IsTrue(EffectEngine.Instance.IsDamagePrevented(jc081),
                "结算后 JC081 应处于伤害护盾下（IsDamagePrevented）");
            // 护盾真的拦伤害：造成 5 点后不落地
            game.CardManager.DealDamageToInstance(jc081, 5);
            Assert.AreEqual(0, jc081.Damage, "护盾生效 ⇒ 伤害不落地");
        }

        // ==================== ③ JC082 结附秘社 ====================

        /// <summary>
        /// 【BUG 3】JC082 索亚之书必须能结附于**本方秘社**（"收藏"模式），不得直接进弃牌堆。
        /// </summary>
        public void TestBug3_JC082可结附于本方秘社()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3, (CardColor)2);   // JC082＝费用 2 ＋ 颜色忠诚 2:1（读 .tres）
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.MoveToHand(book, 1);

            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1);
            Assert.IsTrue(councils != null && councils.Count > 0, "前置：P1 秘社区应有一张秘社");
            var council = councils[0];

            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(book, council),
                "JC082 应能以**本方秘社**为合法打出目标（选择一项＝收藏）");

            // 附属＝A 档：必须处于**行动步骤**、堆叠为空、且持优先权（与常规附属打出同门禁）
            for (int i = 0; i < 80; i++)
            {
                bool ready = (game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionFirst
                              || game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionSecond)
                             && game.TurnManager.PriorityPlayerId == 1;
                if (ready) break;
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            }

            bool played = game.PlayerActionService.PlayCard(1, book.UID, null, council.UID);
            Assert.IsTrue(played,
                $"JC082 打出（结附本方秘社）应成功（阶段={game.TurnManager.CurrentPhase} 优先权={game.TurnManager.PriorityPlayerId} 费用={game.PlayerManager.GetAvailableMana(1)}）");
            Drain(game);

            Assert.AreEqual(CardZone.AttachedZone, book.CurrentZone,
                "JC082 应结附（AttachedZone），不得进弃牌堆");
            Assert.IsTrue(ReferenceEquals(book.AttachedTo, council), "JC082 应绑定到秘社实例");
        }

        /// <summary>
        /// 【BUG 3·后半】"选择一项"的**打出方式参考 JC063**：先弹"选择一项"窗口（借阅／收藏），
        /// 再为所选项选结附目标；选"收藏"⇒ 结附于本方秘社（不得进弃牌堆）。
        /// </summary>
        public void TestBug3b_JC082选择一项的打出流程()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3, (CardColor)2);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.MoveToHand(book, 1);
            Assert.IsTrue(book.PlayerTemplate.PlayChoice, "JC082 应声明 PlayChoice（打出时选择一项）");

            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1)[0];

            var hooks = new ConfirmStub { ModeChoice = 1 };   // 第 2 项＝收藏（本方秘社）
            var prevHooks = EffectEngine.Instance.UIHooks;
            EffectEngine.Instance.UIHooks = hooks;

            // 附属＝A 档：推进到行动步骤并持优先权
            for (int i = 0; i < 80; i++)
            {
                bool ready = (game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionFirst
                              || game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionSecond)
                             && game.TurnManager.PriorityPlayerId == 1;
                if (ready) break;
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            }

            game.TurnManager.BeginModeCardPlay(1, book.UID);
            Drain(game);

            Assert.IsTrue(hooks.ModePrompts >= 1, "应弹出『选择一项』窗口（JC063 同款打出流程）");
            Assert.AreEqual(2, hooks.LastModeCount, "选项数＝2（借阅／收藏）");
            Assert.AreEqual(CardZone.AttachedZone, book.CurrentZone, "选『收藏』后应结附（不得进弃牌堆）");
            Assert.IsTrue(ReferenceEquals(book.AttachedTo, council), "选『收藏』应结附于本方秘社");
            EffectEngine.Instance.UIHooks = prevHooks;   // 【测试卫生】还原（避免污染后续套件：联机用例要求 UIHooks==null）
        }

        /// <summary>
        /// 【BUG 3·追加】声望是**地区级**规则：1 地区的声望角色不得参与 2 地区的声望触发
        /// （用户实测：用 JC074 给 1 地区角色授予声望后，它参与了 2 地区的势力对抗触发，
        ///  且 `PlaceToken` 按来源所在地区落点 ⇒ 把标志放进了错误的地区）。
        /// </summary>
        public void TestBug3c_声望只由本地区角色触发()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            Assert.IsTrue(areas.Count >= 2, "前置：需要至少 2 个地区");
            string area1 = areas[0], area2 = areas[1];
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            // P1：area2 有 1 个声望角色（合法触发源）；area1 另有 1 个声望角色（不得参与 area2 的触发）
            var r2 = OnField(game, Load("JC076_wudaosengren"), 1, area2);
            var r1 = OnField(game, Load("JC070_qianxinxiushi"), 1, area1);
            var e2 = OnField(game, Probe("TS_rn2_e"), 2, area2);

            int before1 = game.AreaManager.GetPowerMark(area1, 1);
            int before2 = game.AreaManager.GetPowerMark(area2, 1);

            game.TurnManager.EmitPowerCombatEnded(area2);   // 2 地区势力对抗结束
            Drain(game);

            Assert.AreEqual(before2 + 1, game.AreaManager.GetPowerMark(area2, 1),
                "2 地区的声望触发应在 2 地区放 1 个标志");
            Assert.AreEqual(before1, game.AreaManager.GetPowerMark(area1, 1),
                "1 地区的声望角色**不得**参与 2 地区的声望触发（地区级规则）；更不得把标志放到 1 地区");
        }

        /// <summary>
        /// 【BUG 5·真实流程】按正常打出流程把 JC073 结附到宿主（而不是测试里手工挂载），
        /// 宿主获得【屏障】后不得出现在『袭击』的候选里。
        /// </summary>
        public void TestBug5b_真实结附流程下屏障拦住袭击()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 4, (CardColor)2);

            var host = OnField(game, Probe("TS_bf2_host"), 1, area);
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            game.ZoneManager.MoveToHand(dream, 1);

            // 附属＝A 档：推进到行动步骤并持优先权，按正常流程打出 JC073 结附宿主
            for (int i = 0; i < 80; i++)
            {
                bool ready = (game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionFirst
                              || game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionSecond)
                             && game.TurnManager.PriorityPlayerId == 1;
                if (ready) break;
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            }
            bool played = game.PlayerActionService.PlayCard(1, dream.UID, null, host.UID);
            Assert.IsTrue(played, "JC073 应能正常打出结附宿主");
            Drain(game);
            Assert.AreEqual(CardZone.AttachedZone, dream.CurrentZone, "JC073 应已结附（非墓地）");
            Assert.IsTrue(host.HasActiveKeyword("屏障"),
                "真实结附流程下宿主必须获得【屏障】（结附后需登记持续授予）");

            // 敌方袭击者的候选里不得出现宿主
            RecordingTriggerView.LastGame = game;
            var view = new RecordingTriggerView();
            game.TurnManager.SetTriggerView(view);
            var bystander = OnField(game, Probe("TS_bf2_bystander"), 1, area);
            var striker = OnField(game, Probe("TS_bf2_striker", strike: 1), 2, area);
            striker.SetCovert(true);
            game.CardManager.RevealCard(striker);

            Assert.IsTrue(view.Candidates.Contains(bystander.UID), "对照组角色应在候选里");
            Assert.IsFalse(view.Candidates.Contains(host.UID), "带【屏障】的宿主不得出现在袭击候选里");
        }
        /// <summary>
        /// 【BUG 5·用户实测现场】用 **PM 命令 `attach`** 结附 JC073：宿主同样必须获得【屏障】。
        /// 根因：`MoveToAttachedZone` 的自动登记发生在 `AttachedTo` 尚未建立时（顺序注释要求的次序），
        /// 那时"授予宿主"解析不到宿主 ⇒ 须在绑定建立后**重登记**一次。
        /// </summary>
        public void TestBug5c_PM命令attach后宿主获得屏障()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var host = OnField(game, Probe("TS_bf3_host"), 1, area);
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            game.ZoneManager.MoveToArea(dream, area, 1);   // 未结附地放在争夺区

            bool ok = game.PMCommandManager.TryExecuteCommand($"attach {dream.UID} {host.UID}", out string err);
            Assert.IsTrue(ok, "PM 命令 attach 应执行成功：" + err);
            Assert.AreEqual(CardZone.AttachedZone, dream.CurrentZone, "附属已移入附属区");
            Assert.IsTrue(ReferenceEquals(dream.AttachedTo, host), "双向绑定已建立");
            Assert.IsTrue(host.HasActiveKeyword("屏障"),
                "PM attach 后宿主必须获得【屏障】（绑定建立后需重登记持续授予）");

            // 袭击候选里也不得出现该宿主
            RecordingTriggerView.LastGame = game;
            var view = new RecordingTriggerView();
            game.TurnManager.SetTriggerView(view);
            var bystander = OnField(game, Probe("TS_bf3_bystander"), 1, area);
            var striker = OnField(game, Probe("TS_bf3_striker", strike: 1), 2, area);
            striker.SetCovert(true);
            game.CardManager.RevealCard(striker);
            Assert.IsTrue(view.Candidates.Contains(bystander.UID), "对照组角色应在候选里");
            Assert.IsFalse(view.Candidates.Contains(host.UID), "带【屏障】的宿主不得出现在袭击候选里");
        }
        // ==================== ④ JC083 准备步骤触发 ====================

        /// <summary>
        /// 【BUG 4】JC083 睿智的斯芬克司"准备步骤触发：选择一个数字…"必须真的触发：
        /// 收集阶段**不得**因 `ValueExpr="玩家选择"` 尚未取值而丢掉该触发（修复前完全不触发）。
        /// </summary>
        public void TestBug4_JC083准备步骤触发并弹数值窗()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var sphinx = OnField(game, Load("JC083_ruizhidesifenkesi"), 1, area);

            // 牌库顶放一张"印刷费用 = 玩家将选择的数字"的牌（费用 3）⇒ 结算后应进手牌
            var topTmpl = new PlayerCardData
            {
                UID = "TS_bug_jc083_top", CardName = "探针顶牌", Cost = 3,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            topTmpl._effectsInjected = true;
            var top = game.CardManager.CreateCardInstance(topTmpl, 1);
            game.ZoneManager.MoveToDeckTop(top, 1);

            var stub = new ConfirmStub { NumberAnswer = 3 };
            var prevHooks2 = EffectEngine.Instance.UIHooks;
            EffectEngine.Instance.UIHooks = stub;

            // 【注意】`StartMatch` 已经跑过**第一回合**的准备步骤（那时本卡还不在场，触发谈不上）
            // ⇒ 直接进入**下一个回合**（`NextTurn`）：届时本卡已在争夺区，`OnTurnStarted` 才会收集它。
            game.TurnManager.NextTurn();
            for (int i = 0; i < 200 && stub.NumberCalls == 0; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            Drain(game);

            Assert.IsTrue(stub.NumberCalls >= 1,
                $"JC083 应弹出一次数值输入窗（修复前完全不触发）（阶段={game.TurnManager.CurrentPhase} 回合={game.TurnManager.TurnCount} "
                + $"本卡区={sphinx.CurrentZone} 顶牌区={top.CurrentZone}）");
            Assert.AreEqual(0, stub.LastMin, "数值范围下限＝0（A31）");
            Assert.AreEqual(20, stub.LastMax, "数值范围上限＝20（A31）");
            Assert.AreEqual(CardZone.Hand, top.CurrentZone, "所展示牌的印刷费用＝所选数字 ⇒ 置入手牌");
            EffectEngine.Instance.UIHooks = prevHooks2;   // 【测试卫生】还原
        }

        // ==================== ⑤ 屏障拦住袭击 ====================

        /// <summary>
        /// 【BUG 5】JC073 授予宿主的【屏障】必须让宿主**不出现在『袭击』的目标候选**里
        /// （"不能被敌方的卡牌或能力指定为目标"）。
        /// </summary>
        public void TestBug5_屏障宿主不出现在袭击候选()
        {
            var game = NewGame();
            string area = FirstArea(game);
            RecordingTriggerView.LastGame = game;
            var view = new RecordingTriggerView();
            game.TurnManager.SetTriggerView(view);

            // P1：宿主 + JC073（授予宿主白眼与屏障）
            var host = OnField(game, Probe("TS_bf_host"), 1, area);
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            AttachToHost(game, host, dream);
            Assert.IsTrue(host.HasActiveKeyword("屏障"), "前置：宿主已获得【屏障】");

            // P2：另一个可被袭击的角色（对照组）+ 带"袭击1"的袭击者（暗藏者 → 现身触发袭击）
            var bystander = OnField(game, Probe("TS_bf_bystander"), 1, area);
            var striker = OnField(game, Probe("TS_bf_striker", strike: 1), 2, area);
            striker.SetCovert(true);

            game.CardManager.RevealCard(striker);   // 现身 → 收集"袭击1"触发（需选目标）

            Assert.IsTrue(view.Candidates.Count > 0, "袭击触发应弹出选目标（候选非空）");
            Assert.IsTrue(view.Candidates.Contains(bystander.UID),
                "对照组角色应在候选里（证明候选枚举正常）");
            Assert.IsFalse(view.Candidates.Contains(host.UID),
                "带【屏障】的宿主**不得**出现在袭击候选里（敌方来源不能指定其为目标）");
        }
    }
}
