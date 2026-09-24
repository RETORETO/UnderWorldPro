using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC110 闲人的绿地 效果（角色，梦魇/梦境）。
    /// 卡文：灵体 手牌行动4：将闲人的绿地封印在目标角色上。
    ///      触发：当闲人的绿地被封印在一个角色上时，你可以横置或重置该角色。
    ///
    /// 本轮交付**第一条**（`手牌行动4`）：
    ///  · **灵体** ＝ 引擎级（伤害防止：被分配伤害时比较该地区双方魔法领域图标数，对手更少则防止）；
    ///  · **手牌行动4** ＝ 一条**在手牌中发动**的激活能力：`Timing = CharacterActivated`
    ///    + **`Scope = ZoneScope.HandZone`**（生效区＝手牌，K27："手牌行动"的单一表达方式）
    ///    + `ActivateWindow = "行动"`（限自己的行动步骤）+ `Cost = 4`（卡文"行动**4**"）
    ///    + 目标＝**目标角色** + 动作 `Seal{自身, 目标}`（把本卡封印到目标角色上）。
    ///    来源生效区门禁（`EffectEngine.ActivateAbility` 0.8 步）与候选扫描（`ActivateEligibility`）都已支持手牌来源。
    ///
    /// 第二条（被封印时→横置或重置该角色）＝**K16 触发版**（见下方 `JC110_2_OnSealedTapOrUntapHost`）：
    /// "横置**或**重置"＝`EffectData.Modes` 二选一，**发动者选择、入堆叠时选定**（`§7.4b A5`）；
    /// 主体由 `"封印宿主"` 表达（"该角色"＝被封印到的角色）；并配套修掉了 `ZoneScope` 缺"封印区"分支
    /// 导致"被封印时"触发在生产路径收集不到的缺口。
    /// </summary>
    public static class JC110_xianrendelvdi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC110_xianrendelvdi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC110_1_HandActionSealSelfOnCharacter",
                Timing = EffectTiming.CharacterActivated,
                Scope = ZoneScope.HandZone,          // 【K27】"手牌行动"＝在手牌中发动的激活能力
                ActivateWindow = "行动",              // 卡文"行动"（限自己的行动步骤）
                Cost = 4,                            // 卡文"行动**4**"
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 自身 → 目标角色（封印：本卡成为该角色上的封印牌）
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "自身", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "手牌行动4：支付 4 费，将本卡封印在目标角色上（从手牌发动）。",
            });

            // ==================== ② 触发：被封印在一个角色上时 → 你可以横置或重置该角色 ====================
            // 【K16·模式分支·触发版】卡文"横置**或**重置"＝二选一：`EffectData.Modes` 两个模式。
            // 口径（用户 2026-09，§7.4b A5 + 追加）：由**发动者**选择，且**入堆叠时选定**（与卡牌模式
            // "打出时选定、入堆叠不可更改"、K15"目标地区入堆叠时锁定"同一口径）——由
            // `TurnManager.EnsureTriggerModeThenPush`（单机）/ `EnsureTriggerModeAsync`（联机）在**入堆叠前**选定，
            // 写入 `StackEntry.ModeIndex`；结算只执行所选模式的动作。
            // "该角色"＝本卡被封印到的**封印宿主**（`SealCard` 写入 `SealedBy`）→ 动作主体写 `"封印宿主"`。
            // 触发本身可选（卡文"你可以"）→ `Forced = false`（排序窗口里可不入栈）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC110_2_OnSealedTapOrUntapHost",
                Timing = EffectTiming.OnSealed,
                Scope = ZoneScope.SealedZone,           // 触发时来源区＝封印区（SealCard 先入封印区再广播）
                Forced = false,                        // 卡文"你可以"
                Condition = new EffectCondition { Predicate = "TriggerBySelf" },   // 与 JC100 同一口径："本卡被封印"自身触发
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },   // 以 Modes 为准
                Modes = new Array<EffectMode>
                {
                    new EffectMode
                    {
                        Label = "横置该角色",
                        Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                        Actions = new Array<EffectAction>
                        {
                            new EffectAction { Verb = ActionVerb.Tap, Args = new Array<string> { "封印宿主" } },
                        },
                    },
                    new EffectMode
                    {
                        Label = "重置该角色",
                        Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                        Actions = new Array<EffectAction>
                        {
                            new EffectAction { Verb = ActionVerb.Untap, Args = new Array<string> { "封印宿主" } },
                        },
                    },
                },
                Duration = "瞬时",
                FaqNote = "本卡被封印到一个角色上时，你可以横置或重置**该角色**（被封印到的角色）；"
                        + "横置/重置的选择在触发入堆叠时选定，入堆叠后不可更改。",
            });
        }
    }
}
