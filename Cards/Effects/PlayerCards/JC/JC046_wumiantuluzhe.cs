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
    /// ⛔ 未做「现身强制触发：每位玩家各牺牲一个角色」：缺"每位玩家各自选择牺牲"的多玩家决策编排（牺牲他人角色也尚未具备）。
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
        }
    }
}
