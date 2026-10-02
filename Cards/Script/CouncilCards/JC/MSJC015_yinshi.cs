using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC015_yinshi （隐士）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中，非【白】且非【中立】派系的牌不能超过12张。
    ///   持续：在每局游戏开始时，在本方秘社上放置一个【隐秘】标志。
    ///   行动3，横置：选择目标地区，若你的手牌数量大于或等于四张，你可以从手中展示一张角色牌，
    ///                然后将其以暗藏者状态置于该地区。
    ///   初始手牌：6
    ///
    /// FAQ：二能力可以将**带公开关键词**的角色以暗藏者状态置于地区
    ///      ⇒ 进场不做"公开关键字禁止暗藏"的拦截（本实现直接 `SetCovert(true)` 后进场，与派遣暗藏者同序）。
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {白, 中立}` + `ExceptionMaxCount = 12`，**无例外条目**）；
    ///  · "每局游戏开始时在本方秘社上放置一个【隐秘】标志"＝ `OpeningEffectList`
    ///    （由 `GameInitializer.StartMatch` → `ApplyCouncilOpeningEffects` 在秘社就位后、发牌前执行一次）；
    ///  · ② 条件"若你的手牌数量大于或等于四张"＝ 新谓词 `HandAtLeast`（`Param="4"`）；
    ///    "**你可以**从手中展示一张角色牌"＝ `Condition` 满足时继续；"展示"走 `Reveal`；
    ///    "以暗藏者状态置于该地区"＝ `Search{筛选=类别:角色, 去向=暗藏进场, 范围=手牌}` ——
    ///    `Search` 的候选池本就取自牌库，**手牌不在其池内**，故这里改用 `MoveToZone` 家族的新动词
    ///    `UnsealToArea` 的姊妹能力：见下方 `PlaceHandCardCovertToArea`（本批次新增，从手牌选一张进暗藏）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC015_yinshi : CouncilCardData
    {
        public MSJC015_yinshi()
        {
            // —— 选项1：行动3，横置；选目标地区，若手牌 ≥4 则从手中展示一张角色牌并以暗藏者状态置于该地区 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·3费·手牌≥4时从手中暗藏派遣一张角色到目标地区", Cost = 3 };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC015_1_DeployHandCharacterCovert",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                NeedsAreaTarget = true,                 // "选择目标地区"
                Target = null,                          // 无卡目标（要选的是手牌里的牌，由动词自己问）
                Condition = new EffectCondition { Predicate = "HandAtLeast", Param = "4" },
                Actions = new Array<EffectAction>
                {
                    // [筛选, 朝向, 目的地]：从手牌选一张**角色牌**，公开展示后以**暗藏者**状态置于本次选定的地区
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceHandCardToArea,
                        Args = new Array<string> { "类别:角色", "暗藏", "目标地区", "展示" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "手牌不足四张时该能力不可发动（发动门禁与结算条件共用 `HandAtLeast` 谓词）；" +
                          "带【公开】关键词的角色同样可以被本能力以暗藏者状态置于地区（FAQ）。",
            });

            ActivationOptions.Add(opt1);

            // —— 持续：在每局游戏开始时，在本方秘社上放置一个【隐秘】标志 ——
            // 落点＝秘社自身（`PlaceCounter` 的对象写"自身"，来源就是本次结算的秘社实例）。
            OpeningEffectList.Add(new EffectData
            {
                EffectId = "MSJC015_S_OpeningSecretMarker",
                Timing = EffectTiming.Continuous,
                ContinuousEffect = "每局游戏开始时在本方秘社上放置一个【隐秘】标志",
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceCounter, Args = new Array<string> { "隐秘", "1", "自身" } },
                },
                Duration = "常驻",
            });
        }
    }
}
