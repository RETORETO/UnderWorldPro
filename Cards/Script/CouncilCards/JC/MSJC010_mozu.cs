using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC010_mozu （貘组）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【黄】和【中立】派系的牌，以及其他派系【星】领域的牌。
    ///   行动，2，横置：从你的手中选择一张【星】领域的牌，将其封印在貘组上，然后抓一张牌。
    ///   行动，2，横置，将一张封印在貘组上的牌置于你的牌库底：目标角色获得或失去灵体能力直到回合结束。
    ///   行动，4，横置，将两张封印在貘组上的牌置于你的牌库底：选择目标地区，令一张封印在貘组上的角色牌
    ///                    正面进场并置于该地区，且由你操控。
    ///   初始手牌：6
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单模式（`AllowedColors = {黄, 中立}` + 例外 `领域=星空`）；
    ///  · ①「从手中选择一张【星】领域的牌封印在貘组上」＝ `Seal{手牌→自身}`，**候选限定【星】领域**
    ///    （`Seal` 的 `Args[3]` 筛选，复用 `DeckSearchFilter` 的 `领域:` 段），然后 `Draw{1}`；
    ///  · ②「将一张封印在貘组上的牌置于你的牌库底」＝ **牌作代价**（`CardCostSpec`：封印区/宿主自身/1 张/
    ///    `MoveToZone{目标, 牌库底, 正面, 自己}`）；收益＝`Gain{灵体,1}` 与 `Lose{灵体,1}` **两个激活选项**
    ///    （卡文"获得**或**失去"＝秘社选项窗天然的二选一，比走 `Modes` 分支更直接）；
    ///  · ③「将两张封印在貘组上的牌置于你的牌库底」＝ 同款代价（2 张）＋ 目标地区 ＋
    ///    `UnsealToArea{封印区, 正面, 自身, 筛选=类别:角色, 由我操控}`。
    /// </summary>
    [GlobalClass]
    public partial class MSJC010_mozu : CouncilCardData
    {
        public MSJC010_mozu()
        {
            // —— 选项1：行动2，横置；从手中选一张【星】领域的牌封印在貘组上，然后抓一张牌 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·2费·封印一张【星】领域手牌并抓1" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC010_1_SealStarFromHand",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = null,                                   // 无目标能力必须显式置空（A57 防呆）
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Seal,
                        // [被封印者来源, 宿主, 附加, 筛选]：从手牌选一张**【星】领域**的牌封印在貘组（自身）上
                        Args = new Array<string> { "手牌", "自身", "", "领域:星空" },
                    },
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "先封印再由你抓一张；手牌中没有【星】领域的牌时该能力不可发动（见 CanActivateCouncil 的封印候选门禁）。",
            });

            // —— 选项2/3：行动2，横置，将一张封印在貘组上的牌置于牌库底；目标角色**获得**/**失去**灵体直到回合结束 ——
            // 卡文"获得**或**失去"由**两个激活选项**表达（秘社本就有选项选择窗，玩家点哪个就是哪个）。
            var cost2 = new CardCostSpec
            {
                FromZone = "封印区", HostSelf = true, Count = 1, Mandatory = true,
                // 代价动作的"对象"必须写 "目标"：`PayCardCost` 会把选中的那张牌作为 target 传入；
                // 写"自身"会解析成来源卡（＝把秘社自己塞进牌库），实测踩过。
                CostAction = new EffectAction
                {
                    Verb = ActionVerb.MoveToZone,
                    Args = new Array<string> { "目标", "牌库底", "正面", "自己" },
                },
            };
            var targetAnyCharacter = new TargetSpec { Kind = TargetKind.Character };

            var opt2a = new CouncilActivationOption { OptionName = "横置·2费·置1张封印牌入牌库底·目标获得灵体" };
            opt2a.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC010_2a_GrantSpirit",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = targetAnyCharacter,
                CardCost = cost2,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "灵体", "1", "目标" } },
                },
                Duration = "直到回合结束",
                FaqNote = "代价＝把 1 张封印在貘组上的牌置于你的牌库底（无牌则不能发动）；目标角色获得灵体直到回合结束。",
            });

            var opt2b = new CouncilActivationOption { OptionName = "横置·2费·置1张封印牌入牌库底·目标失去灵体" };
            opt2b.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC010_2b_RemoveSpirit",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = targetAnyCharacter,
                CardCost = cost2,
                Actions = new Array<EffectAction>
                {
                    // Lose 走与 Gain 同一条属性通道的负数方向（Engine 内 `Lose` 分支 → ApplyGain 负数 ⇒ Revoke）
                    new EffectAction { Verb = ActionVerb.Lose, Args = new Array<string> { "灵体", "1" } },
                },
                Duration = "直到回合结束",
                FaqNote = "代价同上；目标角色失去灵体直到回合结束。",
            });

            // —— 选项4：行动4，横置，将两张封印在貘组上的牌置于牌库底；令一张封印在貘组上的角色牌正面进场 ——
            var opt3 = new CouncilActivationOption { OptionName = "横置·4费·置2张封印牌入牌库底·封印角色牌正面进场" };
            opt3.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC010_3_UnsealCharacterToArea",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 4,
                Forced = false,
                NeedsAreaTarget = true,                          // "选择目标地区"
                Target = null,                                   // 目标由 NeedsAreaTarget 表达，卡目标为空
                CardCost = new CardCostSpec
                {
                    FromZone = "封印区", HostSelf = true, Count = 2, Mandatory = true,
                    CostAction = new EffectAction
                    {
                        Verb = ActionVerb.MoveToZone,
                        Args = new Array<string> { "目标", "牌库底", "正面", "自己" },
                    },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.UnsealToArea,
                        // [来源区, 朝向, 宿主, 筛选, 归属]：貘组上的**角色牌**正面进场到目标地区、由你操控
                        Args = new Array<string> { "封印区", "正面", "自身", "类别:角色", "由我操控" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "代价＝置 2 张封印牌入牌库底；收益＝貘组上的一张角色牌正面进场到所选地区、由你操控。" +
                          "FAQ：若结算时目标地区已被赢取而翻开新地区，本能力因目标不合法而失效。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2a);
            ActivationOptions.Add(opt2b);
            ActivationOptions.Add(opt3);
        }
    }
}
