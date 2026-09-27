using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC121 空鸣御守 效果（附属 / 公开 / 灵体）。
    /// 卡文（用户 2026-09 澄清：伤害那一条是**持续效果**，不是触发）：
    ///      进场触发：将空鸣御守结附于位于本地区的目标本方角色。
    ///      每当受此结附的角色将受到伤害时，该伤害改为对空鸣御守造成。
    ///
    /// 本轮交付**第二条**（**K3·持续型伤害重定向**）：
    ///  · `EffectData.DamageRedirect = DamageRedirectVictim.AttachedHost` + `Timing = Continuous`（前提常驻）：
    ///    本卡**处于结附状态**期间，其**结附的宿主**将受到的伤害**改由本卡承受**；
    ///  · 消费点＝`CardManager.DealDamageToInstance` → `EffectEngine.ResolveDamageRedirect`（一次伤害只重定向一次）；
    ///  · 前缀"灵体"由既有词条通道承担；"不能移动"前缀见进度表该行（人工归类）。
    /// **第一条（进场触发：自行选结附目标）仍未实现**——需"附属进场时选结附目标"的路径（见进度表该行）。
    /// </summary>
    public static class JC121_kongmingyushou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC121_kongmingyushou", Build);

        private static void Build(PlayerCardData data)
        {
            // 灵体（关键词：与 JC111 同款"持续关键字授予"，引擎级 `DamageRules.IsSpirit` 消费）。
            // 附属卡：Scope 用 AttachTarget（与"受此结附的…"同区），保障结附期间生效。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            // ==================== ① 进场触发：自行选结附目标（K12 死角） ====================
            // 卡文："进场触发：将空鸣御守结附于**位于本地区**的目标**本方**角色。"
            //  · 目标＝角色 + `Friendly`（本方）+ `LocalZone`（位于本地区，K14/规则⑥）；
            //  · 动作＝`Attach{"目标"}`（本批实现的新执行器：把**本卡自己**结附到该宿主上）；
            //  · **用户口径 A18**：**没有合法宿主时留在场上（无宿主状态）**——触发在"取目标"阶段因无候选而**不入堆叠**，
            //    本卡因此保持未结附地留在场上；结附后随角色一起移动（现有移动逻辑天然支持）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_C_EnterAttachToLocalAlly",
                Timing = EffectTiming.EnterTrigger,
                // ⚠ 生效区：本卡是"角色/附属"双类别（CategoryFlag=5），而附属的**默认生效区**是结附区；
                //    本触发发生在它**进场（争夺区）那一刻**，故必须显式声明 `Scope = AreaZone`，
                //    否则触发收集阶段会因"不在结附区"被跳过（本轮实测踩到）。
                Scope = ZoneScope.AreaZone,
                Forced = false,                        // 卡文"进场触发"（可选窗口）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Friendly,     // 本方
                        TargetRestriction.LocalZone,    // 位于本地区
                    },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Attach, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "进场时选择本地区一个本方角色，将本卡结附于其上；本地区没有合法角色时留在场上（不结附）。",
            });

            // ==================== ③ 「不能移动」（前缀；A18/A19） ====================
            // 卡文前缀"不能移动"＝**独立效果行**：**只有未结附于角色时才生效**（此时本卡位于争夺区）；
            // 结附于角色后本卡随宿主一起移动，本条自动失效（`Scope = AreaZone` 使其只在该状态注册）。
            // 消费点＝`CardManager.MoveCardToArea`（机动 / 效果移动 / 置换的唯一收口）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_D_CannotMoveWhileUnattached",
                Timing = EffectTiming.Static,
                Scope = ZoneScope.AreaZone,          // 未结附时位于争夺区 → 区域化注册天然只在该状态成立
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.CannotMoveWhileUnattached },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "未结附于角色时，本卡不能被移动（机动/效果移动/置换一律不生效）；结附于角色后随之移动。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_B_RedirectHostDamageToSelf",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于角色时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "受此结附的角色将受到的伤害改由本卡承受",
                DamageRedirect = new DamageRedirectSpec
                {
                    Victim = DamageRedirectVictim.AttachedHost,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间：宿主将受到的伤害改为对空鸣御守造成；伤害落到它身上后再判它的『灵体』防止。",
            });
        }
    }
}
