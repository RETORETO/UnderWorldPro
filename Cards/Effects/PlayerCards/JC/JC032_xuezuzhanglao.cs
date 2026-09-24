using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC032 血族长老 效果（角色）。
    /// 卡文：行动2：自你牌库顶的六张牌中寻找一张吸血鬼角色牌，将该牌展示后以暗藏者状态置于本地区，
    ///      然后将其余的牌洗回牌库。该能力每回合只能发动一次。
    ///
    /// 实现（P3）：
    ///  · 激活：`ActivateWindow="行动"` + `Cost=2`（行动2）；
    ///  · **K23 次数上限**：`MaxUsesPerTurn=1`（每张卡·每回合·每能力；版本号变动时该卡限制重新可用）；
    ///  · 效果：`Search` 检索（筛选 `类别:角色;子类别:吸血鬼`、范围 `牌库顶:6`、去向 `暗藏进场`（＝来源所在地区）、
    ///    附加 `展示`）；检索后由引擎统一洗牌（卡文"然后将其余的牌洗回牌库"）。
    /// </summary>
    public static class JC032_xuezuzhanglao_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC032_xuezuzhanglao", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC032_1_SearchVampireCovert",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",
                Cost = 2,
                MaxUsesPerTurn = 1,   // 【K23】该能力每回合只能发动一次
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },   // 检索不取目标（落点＝本地区）
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        // [筛选, 去向, 附加, 范围]
                        Args = new Array<string> { "类别:角色;子类别:吸血鬼", "暗藏进场", "展示", "牌库顶:6" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "自牌库顶 6 张中检索一张吸血鬼角色牌，展示后以暗藏者状态置于本地区，然后洗牌；每回合一次。",
            });
        }
    }
}
