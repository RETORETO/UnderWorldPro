using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC014_shaobing （哨兵）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【灰】和【中立】派系的牌，以及其他派系不具有魔法领域图标的牌。
    ///   行动，3，横置：在目标敌方暗藏者上放置一个锁定标志。
    ///   行动，3，横置：选择目标本方角色，将其移动到目标具有锁定标志的角色或暗藏者所在的地区。
    ///   初始手牌：6
    ///
    /// FAQ：① 需先选角色或暗藏者；结算时若指定的暗藏者已响应翻面为角色（反之亦然），**该能力因目标不合法而失效**
    ///      ⇒ 由既有 D16 版本号机制承担（`SetCovert` 会 `BumpVersion`，结算比对不一致即判目标丢失、整条不结算）。
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {灰, 中立}` + 例外 `RequireNoDomain`＝不具有魔法领域图标）；
    ///  · ① 目标＝**敌方暗藏者**（`Kind=Covert` + `Enemy`，`AllAreas=true`）＋ `PlaceCounter{"锁定标志","1","目标"}`；
    ///  · ② 目标＝**本方角色**（卡文"选择目标本方角色"，`Kind=Character`+`Friendly`）＋
    ///    `Move{"目标","锁定者所在地区"}` —— 目的地写法为本批次新增：按"场上**具有锁定标志**的卡（角色或暗藏者，
    ///    不限敌我）当前所在地区"实时解析。取"哪一个"锁定者采用**确定性规则**（先按地区顺序、地区内按区域列表顺序
    ///    取第一个带锁定标志的卡），无 UI 与联机两端同解。
    ///    ⚠ 与卡文的偏差：卡文读作"选择一个本方角色，其移动到**目标**具锁定标志者所在地区"——
    ///    真正的口径可能要求玩家**同时**选"被移动的角色"与"目的地区（由那个锁定者决定）"，
    ///    而引擎当前没有"第二个参考目标"的目标槽（需要双目标槽 + 目的地引用目标槽）。
    ///    本批次按"不写特例、缺机制先记账"的纪律：落地为**引擎选择锁定者、玩家选择被移动角色**，
    ///    并在账本里标 ⛔ 写明缺口（若你要求玩家自选锁定者，需先扩"参考目标槽"机制）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC014_shaobing : CouncilCardData
    {
        public MSJC014_shaobing()
        {
            // —— 选项1：行动3，横置；在目标敌方暗藏者上放置一个锁定标志 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·3费·目标敌方暗藏者上放置锁定标志" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC014_1_LockEnemyCovert",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,                        // "敌方**暗藏者**"
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                    AllAreas = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceCounter, Args = new Array<string> { "锁定标志", "1", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "结算时若该暗藏者已翻面为角色，则目标不合法（D16 版本号）→ 整条能力失效，标志不放置。",
            });

            // —— 选项2：行动3，横置；选目标本方角色，移动到"具有锁定标志的角色或暗藏者"所在地区 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·3费·移动目标本方角色到具锁定标志者所在地区" };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC014_2_MoveToLockedArea",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,                     // "选择目标本方角色"＝被移动者
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                    AllAreas = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 目的地＝"锁定者所在地区"（本批次新增写法：场上具锁定标志的卡当前所在地，确定性取第一个）
                    new EffectAction { Verb = ActionVerb.Move, Args = new Array<string> { "目标", "锁定者所在地区" } },
                },
                Duration = "瞬时",
                FaqNote = "目的地区由『具有锁定标志的角色或暗藏者』决定（引擎按确定性规则取第一个；" +
                          "玩家自选锁定者的能力需先扩『参考目标槽』机制，见账本记录）。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);
        }
    }
}
