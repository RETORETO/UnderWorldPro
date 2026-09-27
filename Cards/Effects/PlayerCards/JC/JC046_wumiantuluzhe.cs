using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC046 无面屠戮者 效果（角色）。
    /// 卡文：杀伤2 现身强制触发：每位玩家各牺牲一个角色。
    /// 实现（效果级推进）：**只做「杀伤2」**（`KeywordEffects.Kill(2)` → 参与战斗对抗并获胜时可分配伤害 +2）。
    ///  · **现身强制触发：每位玩家各牺牲一个角色**（口径 A11）＝`SacrificeChoice{"角色","每位玩家","任意"}`：
    ///    按**先手优先**顺序逐个让该玩家自选（后选者可见先选者的选择），全部选定后**一起牺牲**
    ///    （原子锁期间不分死亡先后）；**无合法候选者跳过、什么都不付出**；牺牲＝死亡 → 照常触发死亡时机。
    /// </summary>
    public static class JC046_wumiantuluzhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC046_wumiantuluzhe", Build);

        private static void Build(PlayerCardData data)
        {
            var kill = KeywordEffects.Kill(2);
            kill.EffectId = "JC046_A_Kill2";
            data.EffectDataList.Add(kill);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC046_B_UncoverEachPlayerSacrificesCharacter",
                Timing = EffectTiming.UncoverTrigger,
                Forced = true,                       // 卡文"强制触发"
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.SacrificeChoice,
                        Args = new Array<string> { "角色", "每位玩家", "任意" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "现身时：每位玩家各自选择牺牲一个角色（先手先选、之后一起牺牲）；没有角色的玩家不受影响。",
            });
        }
    }
}
