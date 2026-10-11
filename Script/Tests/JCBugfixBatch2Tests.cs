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
    /// 【2026-10 实测反馈·第四批（联机优先，单机同办）】6 条用户实测缺陷的回归单测（一条一测，先冻结现象）：
    ///  ①/② **威名与声望都只有未横置的角色可以触发**；声望"哪一方触发"的数量比较**不计入横置的声望角色**
    ///     （卡文："若你比对手操控更多**未横置**的声望角色"；口径＝横置的角色不提供其词条）；
    ///  ③ **JC085 复生行尸**：墓地行动——墓地中双击 → 弹**选择地区**窗 → 从墓地正面打出（本套件锁引擎侧落点）；
    ///  ④ **JC089 毒血诅咒**：结附的 -1 防御必须**导致死亡**（防御 1 的宿主、以及防御 3 已受 2 伤的宿主），
    ///     但不能误杀未受伤的 3 防御宿主；
    ///  ⑤ **JC092 死灵召唤**：选地区窗的提示文字必须按卡文给（"以暗藏者状态置于该地区"），
    ///     不得再用移动类效果的"将移动至该地区"；
    ///  ⑥ **JC093 怨灵咒刃**："每回合至多发动两次"用尽后不得再发动（UI 侧不得再开面板/无反馈）。
    ///
    /// 说明：纯 UI 交互（双击信号、面板置灰渲染）在 headless 下不可点，故对"数据/门禁/落点"做行为断言，
    /// 对"UI 接线"另由 `JCEffectSelfAuditTests.TestAudit_承诺落地清单` 的源码级承诺钉住（判据㉖）。
    /// </summary>
    public class JCBugfixBatch2Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 基础工具 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 92 };
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
            => game.AreaManager?.GetOrderedAreaUids() is { Count: > 0 } l ? l[0] : "AreaSlot_1";

        private static List<string> Areas(GameInitializer game)
            => game.AreaManager?.GetOrderedAreaUids() ?? new List<string>();

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, area, playerId);
            return inst;
        }

        /// <summary>探针角色（可指定战力 / 防御 / 是否带【声望】词条）。</summary>
        private static PlayerCardData Probe(string uid, int strength = 0, int defense = 1, bool renown = false)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = defense, Strength = strength,
            };
            if (renown) tmpl.EffectDataList.Add(KeywordEffects.RenownKeyword());
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_b2_asset_{playerId}_{i}", CardName = "探针资产",
                    Cost = 0, CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl.Colors.Add(color);
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>连续让过直到堆叠清空（触发/效果结算收尾）。</summary>
        private static void Drain(GameInitializer game)
        {
            for (int i = 0; i < 60 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>结附（走生产同款单点 `ZoneManager.AttachAndRegister`：移动 → 绑定 → 登记持续效果）。</summary>
        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
            => game.ZoneManager.AttachAndRegister(attachment, host);

        /// <summary>记录"被收集进触发排序窗"的触发（用于断言"横置不触发"）。</summary>
        private sealed class 推入触发视图 : Script.Interfaces.ITriggerView
        {
            public readonly List<StackEffectInstance> Collected = new List<StackEffectInstance>();

            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                if (triggers != null) Collected.AddRange(triggers);
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked) => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<StackEffectInstance> entries) { }
        }

        // ==================== ①/② 未横置才有词条（威名 / 声望） ====================

        /// <summary>
        /// 【用户口径】威名（`KeywordEffects.WeiMing()`）**只有未横置的角色可以触发**：
        /// 同一场战斗对抗获胜——横置的威名卡**不收集、不放标志**；未横置的威名卡照常触发。
        /// </summary>
        public void TestB2_1A_威名_横置不触发_未横置触发()
        {
            // (a) 横置的威名卡：战斗对抗照常获胜（另有未横置的 3 战力探针），但威名不触发
            {
                var game = NewGame();
                string area = FirstArea(game);
                var view = new 推入触发视图();
                game.TurnManager.SetTriggerView(view);
                var weiMing = OnField(game, Load("JC018_morenchuanren"), 1, area);   // 威名（战力 1）
                OnField(game, Probe("TS_b2_wm_win", strength: 3), 1, area);           // 保证获胜（P2 空场）
                weiMing.SetTapped(true);

                int before = game.AreaManager.GetPowerMark(area, 1);
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
                Drain(game);

                Assert.AreEqual(0, view.Collected.Count, "横置的【威名】卡**不收集触发**（横置的角色不提供其词条）");
                Assert.AreEqual(before, game.AreaManager.GetPowerMark(area, 1), "横置的【威名】不放势力标志");
            }
            // (b) 未横置的威名卡：照常触发（对照组，证明 (a) 不是"整条链路没跑"）
            {
                var game = NewGame();
                string area = FirstArea(game);
                var view = new 推入触发视图();
                game.TurnManager.SetTriggerView(view);
                OnField(game, Load("JC018_morenchuanren"), 1, area);
                OnField(game, Probe("TS_b2_wm_win2", strength: 3), 1, area);

                int before = game.AreaManager.GetPowerMark(area, 1);
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
                int collected = view.Collected.Count;
                Drain(game);

                Assert.AreEqual(1, collected, "对照：未横置的【威名】被收集为触发");
                Assert.AreEqual(before + 1, game.AreaManager.GetPowerMark(area, 1), "未横置的【威名】照常放置势力标志");
            }
        }

        /// <summary>
        /// 【用户口径·②】声望的"哪一方触发"比较**不计入横置的声望角色**，且横置的声望角色**自身也不触发**：
        ///  · P1 = 横置声望 1 个 + 未横置声望 1 个，P2 = 未横置声望 1 个 ⇒ 计数 1:1 ⇒ **不触发**；
        ///  · P1 把横置那个竖置后 ⇒ 计数 2:1 ⇒ 触发（并因窗口去重只放 1 枚）。
        /// </summary>
        public void TestB2_2_声望_横置不计入数量且不触发()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            var tapped = OnField(game, Load("JC070_qianxinxiushi"), 1, area);    // 声望（将横置）
            OnField(game, Load("JC076_wudaosengren"), 1, area);                  // 声望（未横置）
            OnField(game, Probe("TS_b2_rn_foe", renown: true), 2, area);          // 对手：1 个未横置声望

            tapped.SetTapped(true);
            Assert.AreEqual(1, RenownRule.CountRenownCharacters(game.ZoneManager, area, 1),
                "P1 的声望角色数＝1（横置的虔心修士**不计入**）");
            Assert.AreEqual(1, RenownRule.CountRenownCharacters(game.ZoneManager, area, 2), "P2 的声望角色数＝1");

            int before = game.AreaManager.GetPowerMark(area, 1);
            game.TurnManager.EmitPowerCombatEnded(area);
            Assert.AreEqual(0, view.Collected.Count, "1:1 不构成多数 ⇒ 声望不触发（横置者不计数、也不自行触发）");
            Drain(game);
            Assert.AreEqual(before, game.AreaManager.GetPowerMark(area, 1), "不触发放置");

            // 竖置后：P1 = 2 个未横置声望 vs P2 = 1 个 ⇒ 触发（两个声望角色各收集 1 条，但只放 1 枚）
            tapped.SetTapped(false);
            Assert.AreEqual(2, RenownRule.CountRenownCharacters(game.ZoneManager, area, 1), "竖置后计入（2 个）");
            game.TurnManager.EmitPowerCombatEnded(area);
            Assert.AreEqual(2, view.Collected.Count, "P1 的两个声望角色各产生 1 条触发");
            Drain(game);
            Assert.AreEqual(before + 1, game.AreaManager.GetPowerMark(area, 1), "同一广播窗口内只放 1 枚（按标签分桶去重）");
        }

        /// <summary>
        /// 【用户口径·①+②·运行期授予路径】JC089 授予宿主【威名】后，宿主**横置即不触发**（与卡面固有同办）。
        /// </summary>
        public void TestB2_2B_JC089授予的威名_宿主横置不触发()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var view = new 推入触发视图();
            game.TurnManager.SetTriggerView(view);

            // 宿主防御 4：吃 JC089 的 -1 防御后仍存活（本用例只验"横置不触发"，不验致死——后者见 ④）
            var host = OnField(game, Probe("TS_b2_rk_host", defense: 4), 1, area);
            OnField(game, Probe("TS_b2_rk_win", strength: 3), 1, area);   // 保证本场战斗对抗有胜者（P2 空场）
            Attach(game, host, game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1));
            Assert.IsTrue(host.HasKeyword("威名"), "前置：宿主获得运行期授予的【威名】");

            host.SetTapped(true);
            game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
            Assert.AreEqual(0, view.Collected.Count, "宿主横置 ⇒ 运行期授予的【威名】不触发");
            Drain(game);

            host.SetTapped(false);
            game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
            Assert.AreEqual(1, view.Collected.Count, "宿主竖置 ⇒ 运行期授予的【威名】照常触发");
        }

        // ==================== ③ JC085 墓地行动（引擎侧落点） ====================

        /// <summary>
        /// 【用户口径·③】JC085 墓地行动的**落点**：双击后由 View 选定地区再上行 `PlayCard{targetAreaUid}`
        /// ⇒ 卡必须落在**所选地区**（本用例锁引擎侧：显式地区参数生效 + 正面进场）。
        /// UI 侧（墓地双击 → 弹选择地区窗）由 `TestAudit_承诺落地清单` 的源码承诺钉住。
        /// </summary>
        public void TestB2_3_JC085_墓地行动按所选地区落地()
        {
            var game = NewGame();
            var areas = Areas(game);
            Assert.IsTrue(areas.Count >= 2, "前置：至少两个地区");
            string targetArea = areas[1];
            GiveAssets(game, 1, 3, (CardColor)7);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);
            Assert.IsTrue(zombie.PlayerTemplate.PlayableFromGraveyard, "JC085 声明『墓地行动』（可从墓地正面打出）");

            Assert.IsTrue(game.TurnManager.PlayCard(1, zombie.UID, targetArea, null), "按所选地区从墓地正面打出");
            Drain(game);

            Assert.AreEqual(CardZone.AreaZone, zombie.CurrentZone, "结算后进入争夺区");
            Assert.AreEqual(targetArea, zombie.ZoneDetailId, "落在**双击后选定的地区**（不是默认地区）");
            Assert.IsFalse(zombie.IsCovert, "正面进场");
        }

        /// <summary>
        /// 【用户口径·③·UI 入口的同源门禁】`TurnManager.CanPlayFromGraveyardNow`（单机高亮/双击与联机客户端共用）：
        ///  · 在墓地 + 声明『墓地行动』+ 有行动权 + 行动步骤 ⇒ true（⇒ 墓地中的卡会**高亮**）；
        ///  · 无行动权 / 不在墓地 / 未声明该标志 ⇒ false（⇒ 不高亮、双击无反馈）。
        /// （窗口形态＝"先技能选择窗、再选择地区窗"由 ㉖ 承诺钉住；headless 不可点击。）
        /// </summary>
        public void TestB2_3B_墓地行动门禁_高亮与双击同源()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 3, (CardColor)7);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var zombie = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToGraveyard(zombie, 1);
            Assert.IsTrue(game.TurnManager.CanPlayFromGraveyardNow(zombie, out string why1),
                $"墓地行动的可行动高亮判据应为真（{why1}）");

            // 无行动权 ⇒ 不高亮
            game.TurnManager.PriorityPlayerId = 2;
            Assert.IsFalse(game.TurnManager.CanPlayFromGraveyardNow(zombie, out string why2), "无行动权 ⇒ 不可（不高亮）");
            Assert.IsTrue(!string.IsNullOrEmpty(why2), "应给出原因（供日志/提示）");
            game.TurnManager.PriorityPlayerId = 1;

            // 未声明『墓地行动』的普通卡在墓地 ⇒ 不可
            var plain = game.CardManager.CreateCardInstance(Probe("TS_b2_plain_gy", defense: 1), 1);
            game.ZoneManager.MoveToGraveyard(plain, 1);
            Assert.IsFalse(game.TurnManager.CanPlayFromGraveyardNow(plain, out _), "未声明『墓地行动』⇒ 不可");

            // 不在墓地 ⇒ 不可（即便模板声明了该标志）
            var notInGrave = game.CardManager.CreateCardInstance(Load("JC085_fushengxingshi"), 1);
            game.ZoneManager.MoveToHand(notInGrave, 1);
            Assert.IsFalse(game.TurnManager.CanPlayFromGraveyardNow(notInGrave, out _), "不在墓地 ⇒ 不可");
        }

        // ==================== ④ JC089 减防致死 ====================

        /// <summary>
        /// 【用户口径·④】JC089 结附后宿主的**最大防御只有 1** ⇒ -1 防御后防御为 0 ⇒ **应死亡**
        /// （原实现只在"已受伤"时才补死亡判定 ⇒ 不死亡）。
        /// </summary>
        public void TestB2_4A_JC089减防致死_防御1未受伤()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var host = OnField(game, Probe("TS_b2_089_d1", defense: 1), 1, area);

            Attach(game, host, game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1));

            Assert.AreEqual(CardZone.Graveyard, host.CurrentZone,
                "1 防御的宿主吃到 -1 防御 ⇒ 防御 0 ⇒ **死亡**（用户实测：原先不死亡）");
        }

        /// <summary>
        /// 【用户口径·④（2026-10 修正："以剩余生命值理解"）】JC089 的 −1 防御是**上限削减**、不是伤害：
        /// 削减时**优先吸收已受伤害**（剩余生命 = 当前上限 − 已受伤害）⇒
        ///  · 2 防御已受 1 伤 → 伤害被吸收为 0、上限 1 ⇒ **剩余 1 ⇒ 不死**（用户实测：原先误判为死亡）；
        ///  · 3 防御已受 2 伤 → 伤害 1、上限 2 ⇒ 剩余 1 ⇒ 不死（同款）；
        ///  · 但"上限已被削减"⇒ **后续 1 点伤害即致命**（削减仍然削弱生存力，不是纯装饰）。
        /// 对照：未受伤的 1 防御宿主吃 −1 ⇒ 上限 0 ⇒ **死亡**（见 4A）；光环撤销（撤销**正向**授予）不吸收
        /// ⇒ JC059 判例不变（`P3AuraFilterTests.TestAuraFilter_减防导致死亡`）。
        /// </summary>
        public void TestB2_4B_JC089减防吸收已受伤害_不误杀()
        {
            // (a) 2 防御、已受 1 伤
            {
                var game = NewGame();
                string area = FirstArea(game);
                var host = OnField(game, Probe("TS_b2_089_d2", defense: 2), 1, area);
                game.CardManager.DealDamageToInstance(host, 1);
                Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "前置：2 防御受 1 伤 ⇒ 存活");

                Attach(game, host, game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1));

                Assert.AreEqual(CardZone.AreaZone, host.CurrentZone,
                    "2 防御受 1 伤 +（−1 上限）：按『剩余生命』**不死亡**（上限削减吸收已受伤害）");
                Assert.AreEqual(1, host.CurrentDefense, "上限 2−1＝1");
                Assert.AreEqual(0, host.Damage, "已受的 1 点伤害被上限削减吸收（并未受到新的伤害）");

                // 削减仍然削弱生存力：再受 1 点伤害（剩余 1 − 1 = 0）即致命
                game.CardManager.DealDamageToInstance(host, 1);
                Assert.AreEqual(CardZone.Graveyard, host.CurrentZone,
                    "上限已降为 1 ⇒ 后续 1 点伤害即致命（诅咒确实削弱生存力）");
            }
            // (b) 3 防御、已受 2 伤（同款"剩余生命"口径）
            {
                var game = NewGame();
                string area = FirstArea(game);
                var host = OnField(game, Probe("TS_b2_089_d3b", defense: 3), 1, area);
                game.CardManager.DealDamageToInstance(host, 2);
                Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "前置：3 防御受 2 伤 ⇒ 存活");

                Attach(game, host, game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1));

                Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "3 防御受 2 伤 +（−1 上限）⇒ 剩余 1 ⇒ 不死亡");
                Assert.AreEqual(2, host.CurrentDefense, "上限 3−1＝2");
                Assert.AreEqual(1, host.Damage, "2 点伤害中被吸收 1 点");
            }
        }

        /// <summary>【回归防线】3 防御、**未受伤**的宿主：-1 防御后防御 2、2 &gt; 0 ⇒ **不得误杀**。</summary>
        public void TestB2_4C_JC089减防不误杀_防御3未受伤()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var host = OnField(game, Probe("TS_b2_089_alive", defense: 3), 1, area);

            Attach(game, host, game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1));

            Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "未受伤的 3 防御宿主不因 -1 防御死亡");
            Assert.AreEqual(2, host.CurrentDefense, "防御 3-1=2");
        }

        // ==================== ⑤ JC092 提示文字（卡文口径） ====================

        /// <summary>
        /// 【用户口径·⑤】JC092 死灵召唤"选择一个地区"槽的**提示文字**必须按卡文给
        /// （"将你墓地中的角色牌以暗藏者状态置于该地区"），**不得**是移动类效果的"将移动至该地区"
        /// （该文案原先硬编码在 UI 里 ⇒ 联机 JC092 选地区时显示成了"将移动至该地区"）。
        /// 同款修正覆盖 JC133（正面进场）与 JC021（真正的移动语义）。
        /// </summary>
        public void TestB2_5_JC092选地区提示为卡文口径()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var areas = Areas(game);

            // JC092：地区槽提示＝卡文口径
            var jc092 = Load("JC092_silingzhaohuan");
            var effect092 = MultiTargetSlots.FindMultiTargetEffect(jc092, EffectTiming.OnSorceryCast);
            Assert.IsNotNull(effect092, "JC092 应有多目标槽位效果");
            var choices = new List<TargetChoice> { TargetChoice.Empty, TargetChoice.Empty };
            var cand0 = MultiTargetSlots.CollectSlot(effect092, 0, 1, choices, game.ZoneManager,
                game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.AreEqual("将你墓地中的角色牌以暗藏者状态置于该地区", cand0.Hint,
                "JC092 选地区槽的提示＝卡文口径（放在 `TargetSlot.Hint`，由 UI 原样展示）");
            Assert.IsFalse(cand0.Hint == "将移动至该地区", "不得再用移动类效果的提示文字");

            // JC133：同款（正面进场）
            var jc133 = Load("JC133_caokongsishi");
            var effect133 = MultiTargetSlots.FindMultiTargetEffect(jc133, EffectTiming.OnSorceryCast);
            var cand133 = MultiTargetSlots.CollectSlot(effect133, 0, 1, choices, game.ZoneManager,
                game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(cand133.Hint != null && cand133.Hint.Contains("正面进场"),
                "JC133 的提示按卡文给（正面进场并置于该地区）");

            // JC021：真正"移动"语义的卡保留移动提示（数据驱动，不靠 UI 硬编码）
            var jc021 = Load("JC021_xialuxiangfeng");
            var effect021 = MultiTargetSlots.FindMultiTargetEffect(jc021, EffectTiming.OnSorceryCast);
            var cand021 = MultiTargetSlots.CollectSlot(effect021, 0, 1, choices, game.ZoneManager,
                game.CardManager, game.PlayerManager, null, game.AreaManager);
            Assert.IsTrue(cand021.Hint != null && cand021.Hint.Contains("移动"),
                "JC021 是移动语义 ⇒ 提示由该卡数据给出（含『移动』）");
        }

        // ==================== ⑥ JC093 次数用尽 ====================

        /// <summary>
        /// 【用户口径·⑥】JC093"该能力每回合至多只能发动两次"：用满两次后
        ///  · `ActivateEligibility.IsAbilityUsesExhausted` 为真（UI 据此置灰/不开面板）；
        ///  · `CanActivate` 为假（候选/高亮同源）；
        ///  · 一次都没用 / 用了一次时仍可发动（不得把上限 2 误判成 1）。
        /// </summary>
        public void TestB2_6_JC093次数用尽后不可发动()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.PriorityPlayerId = 1;

            var host = OnField(game, Probe("TS_b2_093_host", defense: 3), 1, area);
            OnField(game, Probe("TS_b2_093_fodder", defense: 2), 1, area);   // 牺牲代价的合法候选
            var blade = game.CardManager.CreateCardInstance(Load("JC093_yuanlingzhouren"), 1);
            Attach(game, host, blade);

            var e = FindEffect(blade.PlayerTemplate, "JC093_1_SacrificeGrantHostBladeStar");
            Assert.IsNotNull(e, "JC093 应有『牺牲一个角色』的激活能力");
            Assert.AreEqual(2, e.MaxUsesPerTurn, "前置：每回合至多发动两次");
            var abilities = new List<EffectData> { e };

            Assert.IsFalse(ActivateEligibility.IsAbilityUsesExhausted(blade, e), "未发动过 ⇒ 未用尽");
            Assert.IsFalse(ActivateEligibility.AllAbilitiesUsesExhausted(blade, abilities), "未发动过 ⇒ 未全部用尽");
            Assert.IsTrue(ActivateEligibility.CanActivate(blade, game.TurnManager, game.PlayerManager, game.CardManager),
                "未发动过 ⇒ 可发动");

            blade.MarkAbilityUsedThisTurn(e.EffectId);
            Assert.IsFalse(ActivateEligibility.IsAbilityUsesExhausted(blade, e), "用 1 次（上限 2）⇒ 仍可发动");

            blade.MarkAbilityUsedThisTurn(e.EffectId);
            Assert.IsTrue(ActivateEligibility.IsAbilityUsesExhausted(blade, e), "用满 2 次 ⇒ 次数用尽");
            Assert.IsTrue(ActivateEligibility.AllAbilitiesUsesExhausted(blade, abilities),
                "唯一能力用尽 ⇒ UI 双击应**不开面板、无反馈**");
            Assert.IsFalse(ActivateEligibility.CanActivate(blade, game.TurnManager, game.PlayerManager, game.CardManager),
                "次数用尽 ⇒ 候选/高亮侧同样不可发动（唯一实现）");
        }
    }
}
