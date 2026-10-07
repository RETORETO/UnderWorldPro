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
    /// 【P3·复合目标限制（`TargetSpec.SpecAlt`：两条完整目标声明 OR）+ `TargetRestriction.HostIsCouncil`
    ///  → JC043 纵火狂】
    /// 卡文：进场触发：消灭目标**结附于秘社或本地区**上的附属。
    ///
    /// 为什么需要新机制：两条替代条件**条件不同**（一条按"宿主是秘社"、一条按"结附于本地区"），
    /// `KindAlt` 只能表达**大类** OR、`Restrictions` 是 AND —— 故引入 `SpecAlt`（第二条整条目标声明）：
    ///  · 候选池＝两条各自收集的**并集**（去重，本体优先，顺序确定）；
    ///  · 合法性/结算复检＝**任一条整条通过**即通过（`TargetZonePool.Matches` / `MassTargetPool.Matches` /
    ///    `TargetValidity.Check` 三处同源）。
    /// </summary>
    public class P3CompositeTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
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

        private static CardInstance MakeCouncil(GameInitializer game, int playerId)
        {
            var councilTmpl = Godot.ResourceLoader.Load<CouncilCardData>(CouncilDir + "MSJC001_weimushouwang.tres");
            var inst = game.CardManager.CreateCardInstance(councilTmpl, playerId);
            game.ZoneManager.MoveToCouncil(inst, playerId);
            return inst;
        }

        /// <summary>造一个附属并结附到指定宿主（地区：host 传 null + areaUid）。</summary>
        private static CardInstance MakeAttachment(GameInitializer game, string uid, CardInstance host, string areaUid,
            int ownerId = 2)
        {
            var att = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment), ownerId);
            att.IsAttached = true;
            att.AttachedTo = host;
            host?.AttachedCards.Add(att);
            game.ZoneManager.MoveToAttachedZone(att, host == null ? areaUid : (host.ZoneDetailId ?? areaUid), ownerId);
            return att;
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            /// <summary>被收集（＝进入排序窗）的 EffectId 记录。</summary>
            public readonly List<string> Collected = new List<string>();

            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                foreach (var t in triggers) Collected.Add(t?.EffectDataRef?.EffectId ?? "?");
                onDone?.Invoke();
            }

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>
        /// 【2026-09 修复·用户实测】"JC043 效果场上没有合法目标也在触发排序窗口中显示了，
        /// **没有合法目标不能加入触发排序窗口**"：
        /// 收集阶段即用与实际取目标同一套候选枚举判定 ⇒ 无候选时不收集（排序窗里不再出现该条目）。
        /// </summary>
        public void TestCompositeTarget_无合法目标不入触发排序窗()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var view = new AbandonTriggerView();
            game.TurnManager.SetTriggerView(view);

            // 场上**没有任何附属** → JC043 无合法目标
            var pyromaniac = game.CardManager.CreateCardInstance(Load("JC043_zonghuokuang"), 1);
            game.ZoneManager.MoveToArea(pyromaniac, area, 1);
            Assert.AreEqual(0, view.Collected.Count,
                "无合法目标 ⇒ **不收集**（不进触发排序窗）");
            Assert.IsTrue(game.StackManager.IsEmpty, "同样不入堆叠");

            // 对照组：场上有一个"结附于本地区"的附属 → 应照常收集
            var game2 = NewGame();
            string area2 = Areas(game2)[0];
            var view2 = new AbandonTriggerView();
            game2.TurnManager.SetTriggerView(view2);
            MakeAttachment(game2, "TS_ct_none", null, area2, 2);
            var pyromaniac2 = game2.CardManager.CreateCardInstance(Load("JC043_zonghuokuang"), 1);
            game2.ZoneManager.MoveToArea(pyromaniac2, area2, 1);
            Assert.AreEqual(1, view2.Collected.Count, "有合法目标 ⇒ 照常收集（进排序窗）");
            Assert.AreEqual("JC043_1_DestroyAttachmentOnCouncilOrArea", view2.Collected[0], "收集的是 JC043 主效果");
        }

        // ==================== ① 结构 ====================

        public void TestCompositeTarget_JC043结构()
        {
            var tmpl = Load("JC043_zonghuokuang");
            Assert.IsNotNull(tmpl, "JC043 模板应可加载");
            var e = FindEffect(tmpl, "JC043_1_DestroyAttachmentOnCouncilOrArea");
            Assert.IsNotNull(e, "JC043 应有『消灭结附于秘社或本地区的附属』");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual(TargetKind.Attachment, e.Target.Kind, "目标＝附属");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.LocalZone), "本体条件＝本地区");
            Assert.IsNotNull(e.Target.SpecAlt, "有第二条目标声明（OR）");
            Assert.AreEqual(TargetKind.Attachment, e.Target.SpecAlt.Kind, "别名那条也是附属");
            Assert.IsTrue(e.Target.SpecAlt.Restrictions.Contains(TargetRestriction.HostIsCouncil),
                "别名条件＝宿主是秘社");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[0].Verb, "动作＝消灭");
        }

        // ==================== ② 候选池：两条并集（本地区附属 + 秘社附属） ====================

        public void TestCompositeTarget_候选为两条并集()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var pyromaniac = game.CardManager.CreateCardInstance(Load("JC043_zonghuokuang"), 1);
            game.ZoneManager.MoveToArea(pyromaniac, area, 1);
            var council = MakeCouncil(game, 1);

            var onArea = MakeAttachment(game, "TS_ct_area", null, area, 2);        // 结附于**本地区**
            var onCouncil = MakeAttachment(game, "TS_ct_council", council, area, 2); // 结附于**秘社**
            var onChar = game.CardManager.CreateCardInstance(Probe("TS_ct_char", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(onChar, area, 1);
            var onCharacter = MakeAttachment(game, "TS_ct_onchar", onChar, area, 2); // 结附于角色 → 两条都不满足

            var e = FindEffect(pyromaniac.PlayerTemplate, "JC043_1_DestroyAttachmentOnCouncilOrArea");
            var pool = TargetZonePool.Collect(e.Target, 1, game.ZoneManager, game.PlayerManager, area);

            Assert.IsTrue(pool.Contains(onArea), "结附于本地区的附属在候选里");
            Assert.IsTrue(pool.Contains(onCouncil), "结附于秘社的附属在候选里");
            Assert.IsFalse(pool.Contains(onCharacter), "结附于**角色**的附属不在候选里（两条都不满足）");
            Assert.AreEqual(2, pool.Count, "候选恰好两条并集（去重后 2 张）");
        }

        // ==================== ③ 合法性复检：任一条通过即通过 ====================

        public void TestCompositeTarget_任一条通过即合法()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var pyromaniac = game.CardManager.CreateCardInstance(Load("JC043_zonghuokuang"), 1);
            game.ZoneManager.MoveToArea(pyromaniac, area, 1);
            var council = MakeCouncil(game, 1);

            var onArea = MakeAttachment(game, "TS_ct_area2", null, area, 2);
            var onCouncil = MakeAttachment(game, "TS_ct_council2", council, area, 2);
            var onChar = game.CardManager.CreateCardInstance(Probe("TS_ct_char2", CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(onChar, area, 1);
            var onCharacter = MakeAttachment(game, "TS_ct_onchar2", onChar, area, 2);
            var otherAreaAtt = MakeAttachment(game, "TS_ct_other", null, Areas(game)[1], 2);   // 别的地区

            var e = FindEffect(pyromaniac.PlayerTemplate, "JC043_1_DestroyAttachmentOnCouncilOrArea");
            Assert.IsTrue(MassTargetPool.Matches(e.Target, onArea, 1, game.PlayerManager), "本地区附属 → 合法");
            Assert.IsTrue(MassTargetPool.Matches(e.Target, onCouncil, 1, game.PlayerManager), "秘社附属 → 合法");
            Assert.IsFalse(MassTargetPool.Matches(e.Target, onCharacter, 1, game.PlayerManager), "角色附属 → 不合法");
            // "本地区"的最终判据在**结算复检**（`TargetValidity.Check` 带来源地区快照）：
            // `TargetZonePool/MassTargetPool.Matches` 只做"大类＋属性"筛选，不含地区语义。
            var snap = new TargetValidity.TargetSnapshot(area, 1);
            Assert.IsTrue(TargetValidity.Check(pyromaniac, onArea, e.Target, snap, game.PlayerManager).valid,
                "结算复检：本地区的地区附属仍合法");
            Assert.IsTrue(TargetValidity.Check(pyromaniac, onCouncil, e.Target, snap, game.PlayerManager).valid,
                "结算复检：秘社附属仍合法（别名那条）");
            Assert.IsFalse(TargetValidity.Check(pyromaniac, otherAreaAtt, e.Target, snap, game.PlayerManager).valid,
                "结算复检：**别地区**的地区附属不合法（本地区那条不满足、也不是秘社附属）");
            Assert.IsFalse(TargetValidity.Check(pyromaniac, onCharacter, e.Target, snap, game.PlayerManager).valid,
                "结算复检：角色附属不合法");
        }

        // ==================== ④ 端到端：消灭（两条任一） ====================

        public void TestCompositeTarget_端到端消灭()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var pyromaniac = game.CardManager.CreateCardInstance(Load("JC043_zonghuokuang"), 1);
            game.ZoneManager.MoveToArea(pyromaniac, area, 1);
            var council = MakeCouncil(game, 1);

            var onArea = MakeAttachment(game, "TS_ct_area3", null, area, 2);
            var onCouncil = MakeAttachment(game, "TS_ct_council3", council, area, 2);

            var e = FindEffect(pyromaniac.PlayerTemplate, "JC043_1_DestroyAttachmentOnCouncilOrArea");
            EffectEngine.Instance.ResolveEffect(e, pyromaniac, onArea, area);
            Assert.AreEqual(CardZone.Graveyard, onArea.CurrentZone, "结附于本地区的附属被消灭");

            EffectEngine.Instance.ResolveEffect(e, pyromaniac, onCouncil, area);
            Assert.AreEqual(CardZone.Graveyard, onCouncil.CurrentZone, "结附于秘社的附属被消灭");
        }
    }
}
