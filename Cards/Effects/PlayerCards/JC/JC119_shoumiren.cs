using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC119 守秘人 效果（角色）。
    /// 卡文：现身触发：将你的一张手牌背面朝上封印在守秘人上。（你的对手不能检视该牌）
    ///      死亡触发：展示封印在守秘人上的所有牌，你可以打出其中一张牌，视作他具有【快速】且不需要支付其费用
    ///                （但仍需满足忠诚需求）。将不打出的牌移回其拥有者手上。
    /// 实现（效果级推进）：**只做现身触发**——P2 `ActionVerb.Seal` 的"手牌 → 自身"形态 + `背面朝上`；
    ///      "背面朝上"= 信息隔离（`CardInstance.IsSealedFaceDown` → 快照投影 → 非控制者只见占位，见《双端一致性》§六）。
    ///      取手牌由玩家自己选：单机走 `IGameUIHooks.PromptPickCard`，联机走 `SelectHandCard`。
    /// ⛔ 未做「死亡触发：展示封印牌后可免费打出其中一张」：需要"免费打出/视作快速"（K28）+
    ///    手牌区目标（K14）+"展示并多选一"的交互，属未具备的机制 → 见进度表该行说明。
    /// </summary>
    public static class JC119_shoumiren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC119_shoumiren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC119_1_SealHandFaceDown",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },   // 取哪张手牌由玩家选（不需要"目标卡"）
                Actions = new Array<EffectAction>
                {
                    // [被封印者来源, 宿主, 附加]：取自手牌 → 封在自己身上，背面朝上
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "手牌", "自身", "背面朝上" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时从手牌选一张背面朝上封印在守秘人上；对手不能检视该牌。",
            });

            // ② 死亡触发：展示其上封印的所有牌 → 你可以打出其中一张（视作快速、不需付费，仍需忠诚）→ 其余回拥有者手牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC119_2_RevealSealedAndFreePlayOne",
                Timing = EffectTiming.OnCardDied,
                Forced = false,                              // 卡文"你可以"
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // 展示"刚由本卡封印回手"的全部牌 → 由控制者选一张 → 登记免费打出（原子锁解除后走正常打出流程）
                    new EffectAction { Verb = ActionVerb.RevealSealedAndQueueFreePlay },
                },
                Duration = "瞬时",
                FaqNote = "死亡触发：公开展示封印在守秘人上的所有牌（此刻它们已按封印规则回到你手牌）→ "
                        + "你可选一张**免费打出**（**视作【快速】**：不受行动阶段限制；**不需支付费用**，"
                        + "但**仍需满足忠诚需求**）→ 不打出的牌留在手牌（＝移回其拥有者手上）。",
            });
        }
    }
}
