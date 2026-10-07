using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC045 钟摆祭司 效果（角色）。
    /// 卡文：触发：每当一个角色死亡时，在钟摆祭司上放置一个时间标志。
    ///      持续：在其上每有一个时间标志，钟摆祭司就获得一个［黑星］。
    /// 实现（效果级推进）：
    ///  · **死亡触发放标志**：`OnCardDied`（P1 已接线）+ 谓词 `DiedAnyCharacter`
    ///    （"每当**一个**角色死亡时"，含敌方，但不含本卡自身）+ `ActionVerb.PlaceCounter`（放在自己身上）；
    ///  · **持续：每有一个时间标志 → 一个［黑星］**（P3·F 按计数缩放的持续授予）：
    ///    `PrereqSpec.ScaleExpr = "计数:卡上标志:时间标志:自身"` → N＝其上时间标志数，授予量＝`Icon:黑星 ×N`；
    ///    标志增减时实时重判（`CardManager.AddCounter/RemoveCounter` 已挂重判钩子）。
    /// </summary>
    public static class JC045_zhongbaijisi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC045_zhongbaijisi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC045_1_DeathPlaceTimeMarker",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAnyCharacter" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "时间标志", "1", "自身" },
                    },
                },
                Forced = false,   // 【2026-09 修复·用户口径】"每当一个角色死亡时…"**不是强制触发**（用户可以放弃）；
                                  // 原先误写成 `Forced = true`（把"时间标志"类效果一律当强制）——用户实测报障。
                Duration = "瞬时",
                FaqNote = "每当一个角色死亡时，在钟摆祭司上放一个时间标志（含敌方角色，不含自身死亡）。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC045_2_BlackStarPerTimeMarker",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "其上每有一个时间标志获得一个黑星",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    // 【P3·F】按计数缩放：其上的"时间标志"数量
                    ScaleExpr = "计数:卡上标志:时间标志:自身",
                },
                ContinuousEffect = "在其上每有一个时间标志，钟摆祭司就获得一个［黑星］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    // 【2026-09 修复·A65】"获得一个［黑星］"＝**数值**（黑星=势力）：两条通道都要写——
                    // `Icon` 只管"图标存在性/展示"，**数值**必须走 `Attribute:势力`。
                    // 实测：此前只写 `Icon` ⇒ 每有一个时间标志 `GetAttribute("势力")` 仍为 **0**
                    // （用户："JC045 的增加势力效果没有生效"）。缩放由 `GetPrereqScale` 施加（Amount × N）。
                    new EffectGrant { Kind = "Icon", Value = "黑星", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "势力", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "黑星数量＝其上的时间标志数量；标志增加/移除时实时重算。",
            });
        }
    }
}
