using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC012_aifuluodejiazu （爱弗罗德家族）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【蓝】和【中立】派系的牌，以及其他派系的吸血鬼或奴仆角色牌。
    ///   持续：所有印刷费用小于或等于 2 的本方人类角色额外具有奴仆类别。
    ///   行动，1，横置，横置三个本方奴仆角色：抓一张牌。
    ///   行动，2，横置，横置三个本方吸血鬼角色：目标敌方玩家弃一张牌。
    ///   初始手牌：6
    ///
    /// FAQ：只有**场上的** 2 费及以下人类角色才额外具有奴仆类别；牌库 / 手牌 / 墓地等其他区域的人类角色牌
    ///      **不**具有奴仆类别 ⇒ 授予落点必须是"争夺区在场合法的卡"，离场自动收回（`ResetToBaseState` 清运行期授予，
    ///      重算钩子自愈），墓地/牌库中的牌天然拿不到。
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {蓝, 中立}` + 例外 `类别:角色 ∧ 子类别:{吸血鬼,奴仆}`）；
    ///  · 静态持续＝`StaticEffectList` 里的 `CouncilStaticGrant`（**秘社不是 PlayerCardData，光环机制覆盖不到**，
    ///    故按 `LeaderRule`/`PathRule` 同款建独立规则类，由装配层 `OnPostMove` → `ReevaluateCouncilStaticGrants` 重算）；
    ///  · ①② 的"横置三个本方奴仆/吸血鬼角色"＝ **牌作代价**（`CardCostSpec`：`FromZone="场上争夺区"`、
    ///    `Count=3`、`Filter="子类别:奴仆"` / `"子类别:吸血鬼"`、`UntappedOnly=true`、
    ///    `CostAction=Tap{目标}`）—— 与"牺牲/移除标志"同款：入堆叠前支付、候选不足 3 张则**不可发动**；
    ///  · ② 的目标＝**敌方玩家**（`TargetKind.Player` + `Enemy`），弃牌走既有 `Discard{"1","目标玩家","自选"}`。
    /// </summary>
    [GlobalClass]
    public partial class MSJC012_aifuluodejiazu : CouncilCardData
    {
        public MSJC012_aifuluodejiazu()
        {
            var costThreeServants = new CardCostSpec
            {
                FromZone = "场上争夺区",
                HostSelf = false,
                Count = 3,
                Mandatory = true,
                UntappedOnly = true,
                Filter = "子类别:奴仆",
                // 代价动作的"对象"写 "目标"：`PayCardCost` 会把选中的那张牌作为 target 传入
                CostAction = new EffectAction { Verb = ActionVerb.Tap, Args = new Array<string> { "目标" } },
            };
            var costThreeVampires = new CardCostSpec
            {
                FromZone = "场上争夺区",
                HostSelf = false,
                Count = 3,
                Mandatory = true,
                UntappedOnly = true,
                Filter = "子类别:吸血鬼",
                CostAction = new EffectAction { Verb = ActionVerb.Tap, Args = new Array<string> { "目标" } },
            };

            // —— 选项1：行动1，横置；横置三个本方奴仆角色 → 抓一张牌 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·1费·横置三个本方奴仆角色·抓1" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC012_1_TapThreeServantsDraw",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 1,
                Forced = false,
                Target = null,                       // 无目标（代价选的"三个奴仆"不是效果目标）
                CardCost = costThreeServants,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "代价＝横置三个**本方奴仆角色**（印刷费用≤2 的本方人类角色因持续效果而具有奴仆类别）；" +
                          "未横置的合法奴仆不足三个时不能发动。",
            });

            // —— 选项2：行动2，横置；横置三个本方吸血鬼角色 → 目标敌方玩家弃一张牌 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·2费·横置三个本方吸血鬼角色·目标敌方玩家弃1" };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC012_2_TapThreeVampiresDiscard",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                CardCost = costThreeVampires,
                Actions = new Array<EffectAction>
                {
                    // 目标玩家自己选一张手牌弃掉（弃牌唯一出口 → 广播 OnDiscard）
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "目标玩家", "自选" } },
                },
                Duration = "瞬时",
                FaqNote = "代价＝横置三个**本方吸血鬼角色**；收益＝目标敌方玩家弃一张牌（由该玩家自选）。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);

            // —— 持续：所有印刷费用 ≤2 的本方人类角色额外具有【奴仆】类别 ——
            var staticGrant = new EffectData
            {
                EffectId = "MSJC012_S_Cost2HumansAreServants",
                Timing = EffectTiming.Continuous,
                ContinuousEffect = "印刷费用≤2的本方人类角色额外具有奴仆类别",
                CouncilStatic = new CouncilStaticGrant
                {
                    FilterByCategory = true,
                    TargetCategoryFlag = CardCategory.Character,
                    FilterBySubCategories = true,
                    TargetSubCategories = new Array<string> { "人类" },
                    FilterByMaxCost = true,
                    MaxCost = 2,
                    GrantSubCategory = "奴仆",
                },
            };
            StaticEffectList.Add(staticGrant);
        }
    }
}
