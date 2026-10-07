using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC109 沉沦的巴德勒 效果（角色）。
    /// 卡文：结束步骤触发：从你的牌库中寻找一张灵体角色牌，将该牌封印在沉沦的巴德勒上，然后洗牌。
    ///      持续：每有一张牌被封印在其上，沉沦的巴德勒就获得一个［黑刀］。
    /// 实现（效果级推进）：
    ///  · **结束步骤触发**：`EffectTiming.OnTurnEnded`（结束步骤广播，P1 已接线）+ P2 `Search`：
    ///    筛选 `关键字:灵体;类别:角色`（`;`=AND；关键字按"运行时授予 ∪ 模板固有"判定），
    ///    去向 `封印` + `附加="自身"`（宿主 = 巴德勒自己），取到牌后**必定洗牌**（卡文"然后洗牌"）。
    ///  · **持续：每有一张牌被封印在其上 → 一个［黑刀］**（P3·F 按计数缩放的持续授予）：
    ///    `PrereqSpec.ScaleExpr = "计数:封印牌:自身"` → N＝其上封印牌数，授予量＝`Icon:黑刀 ×N`；
    ///    `TurnManager.SealCard` 落地后即重判。
    /// </summary>
    public static class JC109_chenlundebadelei_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC109_chenlundebadelei", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC109_1_EndStepSealSpirit",
                Timing = EffectTiming.OnTurnEnded,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        // [筛选, 去向, 附加, 范围]：去向=封印，附加="自身" → 宿主为巴德勒本身
                        Args = new Array<string> { "关键字:灵体;类别:角色", "封印", "自身", "牌库" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "结束步骤从牌库找一张灵体角色牌，封印在巴德勒上并洗牌。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC109_2_BlackBladePerSealed",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "其上每有一张封印牌获得一个黑刀",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    // 【P3·F】按计数缩放：被封印在其上的牌数
                    ScaleExpr = "计数:封印牌:自身",
                },
                ContinuousEffect = "每有一张牌被封印在其上，沉沦的巴德勒就获得一个［黑刀］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "黑刀数量＝封印在其上的牌数；封印后实时重算。",
            });
        }
    }
}
