using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC066 封魔瓶瑟琳娜 效果（角色 / 人类·超能力者 / 国家机构）。
    /// 卡文：进场触发：将本地区的目标角色或暗藏者封印于瑟琳娜上。
    ///      持续：瑟琳娜额外具有被封印在其上的角色牌能力栏内的所有能力图标。
    ///
    /// ① **进场封印**：P2 `ActionVerb.Seal` 的"目标 → 自身"形态——`Seal{目标, 自身}`：
    ///    **被封印者＝本次选中的目标**（本地区角色或暗藏者，`IsLocalZone` + `IncludeCovert`），
    ///    **宿主＝瑟琳娜自身**（卡文"封印**于瑟琳娜上**"）。
    ///    （原实现写成 `Seal{自身, 目标}`＝把瑟琳娜封印到目标身上，与卡文**方向相反** → 本轮随行修正。）
    /// ② **图标镜像**（本轮交付）：`ContinuousPrereqSpec.MirrorSealedIcons = true` —— 本效果不看卡脚本里
    ///    静态声明的 `ContinuousGrants`，而是把"**被封印在本卡上的角色牌**"的**印刷能力图标**汇总后**额外授予本卡**
    ///    （`EffectEngine.BuildMirroredIconGrants`）：黑刀=战斗 / 黑眼=调查 / 黑星=势力 /
    ///    白刀=先手战斗 / 白眼=先手调查 / 白星=先手势力（沿用 `IconToAttribute` 既有映射）。
    ///    只镜像**角色牌**（非角色封印牌不提供能力图标）；只读**印刷**图标（封印牌已不在场）；
    ///    **不含魔法领域图标**（领域 ≠ 能力图标）。
    ///    授予量随封印牌集合变化：封印落地（`TurnManager.SealCard`）与任意换区（`ZoneManager.FireMoved`）
    ///    都会触发 `ReevaluatePrereqs` → 撤销旧量、按当前封印牌重算（登记键＝卡 UID + 效果 Id）。
    /// </summary>
    public static class JC066_fengmopingselinna_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC066_fengmopingselinna", Build);

        private static void Build(PlayerCardData data)
        {
            // ① 进场触发：把本地区目标角色/暗藏者封印**在瑟琳娜身上**
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC066_1_SealTarget",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,   // IncludeCovert=true 时暗藏者也可选（卡文"角色或暗藏者"）
                    NeedsSelection = true,
                    IsLocalZone = true,            // 本地区 = 瑟琳娜所在地区
                    // 【2026-09 整体复查·用户裁定】卡文未写"其他"，但**规则上瑟琳娜不能把自己封印在自己身上**
                    // ⇒ 显式排除自身（`CanTargetSelf=false`，与默认值一致但写明意图，供判据⑥审计）。
                    CanTargetSelf = false,
                    IncludeCovert = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 被封印者＝目标（Args[0]="目标"），宿主＝瑟琳娜自身（Args[1]="自身"）
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "目标", "自身" } },
                },
                Duration = "瞬时",
                FaqNote = "进场时把本地区一个目标角色或暗藏者封印在瑟琳娜上（被封印者离场时自动回其拥有者手牌）。",
            });

            // ② 持续：额外具有"被封印在其上的角色牌"的能力图标（图标镜像）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC066_2_MirrorSealedIcons",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,              // 角色在争夺区时生效
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.None,              // 前提＝常驻（判定见 EvaluatePrereq 的镜像分支）
                    MirrorSealedIcons = true,            // **授予来源＝封印在本卡上的角色牌的能力图标**
                },
                ContinuousPrereq = "常驻",
                ContinuousEffect = "额外具有被封印在其上的角色牌的能力栏内所有能力图标（图标镜像）",
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "封印牌集合变化即时重算：每张封印在瑟琳娜上的角色牌，其能力栏内的每个能力图标都给瑟琳娜 +1 对应属性。",
            });
        }
    }
}
