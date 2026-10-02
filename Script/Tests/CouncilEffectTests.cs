using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Cards.Script.CouncilCards.JC;

namespace Script.Tests
{
    /// <summary>
    /// 【秘社效果批次】MSJC001『帷幕守望』按卡文落地 + 两个新机制（【A57】每局游戏一次 / 独有检索）
    /// 以及【先手标志】谓词：
    ///  · 卡文：行动3横置"**若你具有【先手标志】**则抓一张牌"；行动4横置"从牌库**寻找一张【帷幕守望】独有牌**，
    ///    展示后入手并洗牌。**该能力每局游戏只能发动一次**"（此前实现是"从牌库底抓一张牌"的暂代）；
    ///  · `独有`＝`PlayerTemplate.UniqueField` **非空**（用户 2026-09 口径，数据由卡表回填）；
    ///  · `MaxUsesPerGame`：与 K23 `MaxUsesPerTurn` 平行，但**不随回合重置、离场/换区也不清零**，只有重开一局才恢复。
    /// </summary>
    public class CouncilEffectTests : TestRunner
    {
        private const string CouncilTres = "res://Cards/Data/CouncilCards/JC/MSJC001_weimushouwang.tres";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 20260902 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static CouncilCardData LoadCouncil()
        {
            Cards.Effects.AllEffects.RegisterAll();
            return ResourceLoader.Load<CouncilCardData>(CouncilTres);
        }

        private static PlayerCardData Probe(string uid, string org = null, string uniqueField = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            if (org != null) tmpl.Organization = org;
            if (uniqueField != null) tmpl.UniqueField = uniqueField;
            tmpl._effectsInjected = true;
            return tmpl;
        }

        // ==================== ① 结构与静态数据（.tres + .cs 是否表达全卡文） ====================

        public void TestCouncilMSJC001_结构表达全部卡文信息()
        {
            var tmpl = LoadCouncil();
            Assert.IsNotNull(tmpl, "秘社模板应可加载");
            Assert.AreEqual("帷幕守望", tmpl.CardName, "卡名");
            Assert.AreEqual("秘社-法师结社", tmpl.SubType, "子类型＝秘社-法师结社");
            Assert.AreEqual(1, tmpl.Colors.Count, "派系色单一");
            Assert.AreEqual(CardColor.Yellow, tmpl.Colors[0], "派系色＝【黄】（与玩家卡同名字段 Colors）");
            Assert.AreEqual("帷幕守望", tmpl.Organization, "组织");
            Assert.AreEqual(6, tmpl.StartingHandSize, "**初始手牌：6**（现已在 .tres 中显式声明，不再靠默认值）");
            Assert.IsTrue(tmpl.HasRestrictions, "应声明构筑限制");
            Assert.AreEqual(1, tmpl.Restrictions.Count, "一条构筑限制：≥25 张【黄】");
            Assert.IsTrue(tmpl.Restrictions[0].FilterByColor, "按颜色筛选");
            Assert.AreEqual(25, tmpl.Restrictions[0].RequiredCount, "需要 ≥25 张");
            Assert.AreEqual(2, tmpl.ActivationOptions.Count, "两个激活选项（行动3 / 行动4）");
            Assert.AreEqual(3, tmpl.ActivationOptions[0].EffectDataList[0].Cost, "选项①费用 3");
            Assert.AreEqual(4, tmpl.ActivationOptions[1].EffectDataList[0].Cost, "选项②费用 4");
        }

        public void TestCouncilMSJC001_选项1带先手标志前提()
        {
            var tmpl = LoadCouncil();
            var e = tmpl.ActivationOptions[0].EffectDataList[0];
            Assert.AreEqual("MSJC001_1_DrawIfFirstPlayer", e.EffectId, "选项①效果 Id");
            Assert.AreEqual(EffectTiming.CouncilActivated, e.Timing, "秘社激活时机");
            Assert.AreEqual("行动", e.ActivateWindow, "行动窗口");
            Assert.IsTrue(e.ActivateCondition.Contains("横置本角色"), "代价含横置本秘社");
            Assert.IsNotNull(e.Condition, "卡文『若你具有【先手标志】』⇒ 必须有条件");
            Assert.AreEqual("具有先手标志", e.Condition.Predicate, "条件＝具有先手标志");
            Assert.AreEqual(ActionVerb.Draw, e.Actions[0].Verb, "满足则抓一张牌");
        }

        public void TestCouncilMSJC001_选项2为独有检索且每局一次()
        {
            var tmpl = LoadCouncil();
            var e = tmpl.ActivationOptions[1].EffectDataList[0];
            Assert.AreEqual("MSJC001_2_SearchUniqueCard", e.EffectId, "选项②效果 Id");
            Assert.AreEqual(ActionVerb.Search, e.Actions[0].Verb,
                "卡文『从牌库中寻找』⇒ Search（不再是暂代的 DrawFromBottom）");
            Assert.AreEqual("独有;组织:帷幕守望", e.Actions[0].Args[0], "筛选＝独有 ∧ 组织:帷幕守望");
            Assert.AreEqual("手牌", e.Actions[0].Args[1], "去向＝手牌");
            Assert.IsTrue(e.Actions[0].Args.Contains("展示"), "展示后入手（附加＝展示）");
            Assert.AreEqual(1, e.MaxUsesPerGame, "**该能力每局游戏只能发动一次**（A57 新机制）");
        }

        // ==================== ② 独有检索筛选（UniqueField 非空） ====================

        public void TestCouncilManifest_独有筛选按UniqueField非空判定()
        {
            var game = NewGame();
            var unique = game.CardManager.CreateCardInstance(Probe("TS_c57_unique", "帷幕守望", "帷幕守望独有"), 1);
            var plain = game.CardManager.CreateCardInstance(Probe("TS_c57_plain", "帷幕守望"), 1);

            Assert.IsTrue(DeckSearchFilter.Matches("独有", unique), "UniqueField 非空 ⇒ 独有牌命中");
            Assert.IsFalse(DeckSearchFilter.Matches("独有", plain), "UniqueField 为空 ⇒ 非独有（当前全卡库多数卡如此）");
        }

        public void TestCouncilManifest_独有加组织为AND()
        {
            var game = NewGame();
            var hit = game.CardManager.CreateCardInstance(Probe("TS_c57_hit", "帷幕守望", "帷幕守望独有"), 1);
            var wrongOrg = game.CardManager.CreateCardInstance(Probe("TS_c57_org", "鸣钟教派", "鸣钟独有"), 1);
            var noUnique = game.CardManager.CreateCardInstance(Probe("TS_c57_noUniq", "帷幕守望"), 1);

            const string filter = "独有;组织:帷幕守望";
            Assert.IsTrue(DeckSearchFilter.Matches(filter, hit), "独有 ∧ 组织=帷幕守望 ⇒ 命中（MSJC001 行动4 的检索目标）");
            Assert.IsFalse(DeckSearchFilter.Matches(filter, wrongOrg), "组织不符 ⇒ 不命中");
            Assert.IsFalse(DeckSearchFilter.Matches(filter, noUnique), "非独有 ⇒ 不命中");
        }

        // ==================== ③ 【先手标志】谓词 ====================

        public void TestCouncilManifest_先手标志谓词()
        {
            var game = NewGame();
            var mine = game.CardManager.CreateCardInstance(Probe("TS_c57_first"), 1);

            game.PlayerManager.SetFirstPlayer(1);
            Assert.IsTrue(game.EffectEngine.EvaluateCondition("具有先手标志", mine, null),
                "玩家1 为先手 ⇒ 满足（卡文『若你具有【先手标志】』）");
            Assert.IsTrue(game.EffectEngine.EvaluateCondition("先手标志", mine, null), "别名同样成立");

            game.PlayerManager.SetFirstPlayer(2);
            Assert.IsFalse(game.EffectEngine.EvaluateCondition("具有先手标志", mine, null),
                "玩家1 非先手 ⇒ 不满足（该能力此时不产生效果）");
        }

        // ==================== ④ MaxUsesPerGame（每局游戏一次） ====================

        /// <summary>达到本局上限后：该能力不可发动（`ActivateEligibility` 门禁，与 K23 同一条判定链）。
        /// 用**最小 TurnManager**（与 `CouncilActivateWindowTests` 同款）隔离"每局上限"这一个自变量，
        /// 不受真实对局里的原子锁/阶段流转影响。</summary>
        public void TestCouncilMaxUsesPerGame_达到上限后不可发动()
        {
            var tm = new global::TurnManager();
            var sm = new StackManager();
            sm.Clear();                       // 堆叠为空（行动类激活的前提）
            tm.SetStackManager(sm);
            var pm = new PlayerManager();
            tm.Initialize(null, pm, null);
            tm.FirstPlayerId = 1;
            tm.PriorityPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            // 单选项探针秘社（0 费，隔离"每局上限"这一个自变量）
            var tmpl = new CouncilCardData { CardName = "探针秘社", UID = "TS_c57_council" };
            var opt = new CouncilActivationOption { OptionName = "0费·每局一次" };
            opt.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_c57_council_e",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 0,
                MaxUsesPerGame = 1,   // 【A57】每局游戏只能发动一次
                // ⚠ 踩过的坑：`EffectData.Target` 默认是**非 null 的 TargetSpec**，若不清空为 null，
                // `CanActivateCouncil` 会走"需要选目标"的门禁（HasLegalTarget 失败 ⇒ 永远不可激活）。
                // 秘社的"无目标能力"必须显式 `Target = null`（MSJC001 两个选项都如此）。
                Target = null,
            });
            tmpl.ActivationOptions.Add(opt);

            var council = new CardInstance(tmpl, "TS_c57_council_inst") { OwnerId = 1, ControllerId = 1 };
            var cm = new CardManager();

            // 诊断（同时锁定前置条件本身）：以下三条任一不成立都会让"可激活"为 false
            Assert.IsFalse(tm.IsAtomicLocked, "前置：非原子段");
            Assert.AreEqual(1, tm.PriorityPlayerId, "前置：优先权在控制者");
            Assert.IsTrue(tm.CanEnterBasicActionMask(1), "前置：控制者处于自己的行动步骤");
            Assert.IsNotNull(council.CouncilTemplate, "前置：识别为秘社卡");
            Assert.IsFalse(council.IsCovert, "前置：正面朝上");

            Assert.IsTrue(ActivateEligibility.CanActivate(council, tm, pm, cm),
                "前置：本局未发动过 ⇒ 可激活");

            council.MarkAbilityUsedThisGame("TS_c57_council_e");

            Assert.AreEqual(1, council.GetAbilityUsesThisGame("TS_c57_council_e"), "本局已用 1 次");
            Assert.IsTrue(council.IsGameAbilityLimitReached("TS_c57_council_e", 1), "已达每局上限");
            Assert.IsFalse(ActivateEligibility.CanActivate(council, tm, pm, cm),
                "达到每局上限 ⇒ 不再可发动（客户端投影同解，见随快照投影用例）");
        }

        /// <summary>核心口径：**回合重置不清**、**离场/换区/版本号变动也不清**，只有重开一局才恢复。</summary>
        public void TestCouncilMaxUsesPerGame_不随回合与离场清零()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Probe("TS_c57_limit"), 1);
            const string eid = "TS_c57_limit_e";

            card.MarkAbilityUsedThisGame(eid);
            card.MarkAbilityUsedThisTurn(eid);

            // ① 回合重置（DoReset → ResetAllAbilityUsesThisTurn）只清回合计数
            game.CardManager.ResetAllAbilityUsesThisTurn();
            Assert.AreEqual(0, card.GetAbilityUsesThisTurn(eid), "回合计数被清（K23 行为不变）");
            Assert.AreEqual(1, card.GetAbilityUsesThisGame(eid), "**本局计数不受回合重置影响**");

            // ② 离场/换区/回手（ResetToBaseState：版本号变动、状态复位）也不清本局计数
            var area = game.AreaManager.GetOrderedAreaUids()[0];
            game.ZoneManager.MoveToArea(card, area, 1);
            game.ZoneManager.MoveToHand(card, 1);
            card.ResetToBaseState();
            Assert.AreEqual(1, card.GetAbilityUsesThisGame(eid),
                "**离场/回手/状态复位后本局计数仍为 1**（『每局一次』不能用换区刷回来）");

            // ③ 只有重开一局才清
            game.CardManager.ResetAllAbilityUsesThisGame();
            Assert.AreEqual(0, card.GetAbilityUsesThisGame(eid), "重开一局 ⇒ 本局计数清零");
        }

        /// <summary>客户端一致性：本局计数随快照投影下行（否则客户端"本局已用过"仍高亮可发动）。</summary>
        public void TestCouncilMaxUsesPerGame_随快照投影下行()
        {
            var game = NewGame();
            var card = game.CardManager.CreateCardInstance(Probe("TS_c57_proj"), 1);
            card.MarkAbilityUsedThisGame("TS_c57_proj_e");

            var r = global::ZoneManager.ProjectCard(card, CardZone.Hand, 1);
            Assert.IsNotNull(r, "投影成功");
            Assert.IsTrue(r.UsedAbilityIdsGame.Contains("TS_c57_proj_e"), "本局已用能力 Id 随快照下行");
            Assert.AreEqual(1, r.UsedAbilityCountsGame[r.UsedAbilityIdsGame.IndexOf("TS_c57_proj_e")], "次数同序对应");

            // 回写（客户端投影重建）后与服务端同解
            var restored = game.CardManager.CreateCardInstance(Probe("TS_c57_proj2"), 2);
            var pairs = new List<KeyValuePair<string, int>> { new("TS_c57_proj_e", 1) };
            restored.SetAbilityUsesThisGame(pairs);
            Assert.AreEqual(1, restored.GetAbilityUsesThisGame("TS_c57_proj_e"), "投影恢复后计数一致");
            Assert.IsTrue(restored.IsGameAbilityLimitReached("TS_c57_proj_e", 1), "恢复后即视为已达上限");
        }

        // ==================== ⑤ 秘社激活链路：首次激活必须真的结算（双重计数缺陷回归） ====================

        /// <summary>把一张卡放到玩家1 牌库顶（作为检索目标池）。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, card);
            return card;
        }

        /// <summary>给某玩家若干未横置资产（凑够秘社费用；本作费用均为无色）。</summary>
        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_c57_mana_{playerId}_{i}", CardName = "探针资产", Cost = 0,
                    CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>
        /// 【缺陷回归】MSJC001 行动4（每局游戏一次）**第一次发动必须真的结算**：
        /// 缺陷现象＝激活时已登记"本局 1 次"，入堆栈结算时又被检查一次 ⇒ `1 ≥ 1` 直接跳过，
        /// 表现为"秘社效果第一次发动就被跳过"。本用例走完整链路（激活 → 入堆栈 → 让过结算 → 检索落地）。
        /// </summary>
        public void TestCouncilMSJC001_首次激活即正常结算()
        {
            var game = NewGame();
            GiveMana(game, 1, 6);
            game.TurnManager.FirstPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            game.TurnManager.PriorityPlayerId = 1;

            var council = game.CardManager.CreateCardInstance(LoadCouncil(), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            // 牌库顶放一张"【帷幕守望】独有牌"（满足 `独有;组织:帷幕守望`），检索应把它找出来
            var target = PutOnDeckTop(game, Probe("TS_c57_search_hit", "帷幕守望", "帷幕守望独有"));

            var e = LoadCouncil().ActivationOptions[1].EffectDataList[0];   // 行动4：检索独有牌（每局一次）
            Assert.AreEqual("MSJC001_2_SearchUniqueCard", e.EffectId, "前置：选项②＝检索独有牌");

            EffectEngine.Instance.ActivateAbility(council, e, null);

            Assert.AreEqual(1, council.GetAbilityUsesThisGame(e.EffectId), "发动被接受 → 本局计数 = 1");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "应入堆叠（等待结算）");

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Hand, target.CurrentZone,
                "**首次结算必须真的执行检索**（旧实现：结算分支二次检查 1≥1 ⇒ 首发动即被跳过）");
            Assert.AreEqual(1, council.GetAbilityUsesThisGame(e.EffectId), "结算不得把本局计数再加一次（仍为 1）");
            Assert.IsTrue(council.IsGameAbilityLimitReached(e.EffectId, 1), "本局额度已用尽");

            // 第二次发动必须被拒（额度用完；权威入口与高亮一致）
            int stackBefore = game.StackManager.Entries.Count;
            EffectEngine.Instance.ActivateAbility(council, e, null);
            Assert.AreEqual(stackBefore, game.StackManager.Entries.Count, "第二次发动被拒（不入堆叠）");
            Assert.AreEqual(1, council.GetAbilityUsesThisGame(e.EffectId), "被拒的发动不计入次数");
        }
    }
}
