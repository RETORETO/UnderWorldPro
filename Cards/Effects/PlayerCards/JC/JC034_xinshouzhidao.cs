using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC034 心兽之道 效果（附属卡；`SubCategories = ["道途", "吸血鬼"]`，结附于本方秘社）。
    /// 卡文：结附于本方秘社。你只能操控至多一个道途。
    ///      持续：当你赢得一次战斗对抗时，若有本方吸血鬼角色参与了这次战斗对抗，你可以令目标对手弃一张手牌。
    ///
    /// 实现（效果级推进）：
    ///  · **「你只能操控至多一个道途」** ＝ `StaticKind.UniquePathAtMostOne` 静止标记（`Timing = Static`）；
    ///    消费点＝`PathRule`（新建，与 `LeaderRule` 同款——**用户 2026-09 口径：与领袖同一套处理，
    ///    存在多个道途时由该玩家选择一个牺牲**）：任何区域变更后（`ZoneManager.OnPostMove`）若该控制者场上
    ///    道途数 &gt; 1 且场上存在印了本规则的卡 → 立刻让该控制者选一个牺牲（不入堆叠、不可被响应；
    ///    牺牲＝死亡并广播死亡触发）；单机 `PromptPickCard`／联机 `Kind.SelectTarget`／无 UI 确定性兜底。
    ///  · 前缀「结附于本方秘社」由卡数据的目标规则承担（引擎已支持结附限制）。
    ///  · ⛔ **第二条（战斗对抗获胜 → 若本方吸血鬼角色参与该次对抗 → 令目标对手弃一张手牌）尚未实现**：
    ///    "参与了该次对抗"按用户口径（§7.4b A14）＝**近似「位于战斗地区且属于获胜方」**，
    ///    但本条还需要"**本地区存在本方吸血鬼角色**"的复合判据（K24 玩家目标与弃牌原语均已具备）。
    /// </summary>
    public static class JC034_xinshouzhidao_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC034_xinshouzhidao", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC034_A_UniquePathAtMostOne",
                Timing = EffectTiming.Static,          // 静止规则：落在卡上（`CardInstance.StaticFlags`）
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.UniquePathAtMostOne },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "同一玩家至多操控 1 个道途；超出上限时立刻由该玩家选择一个牺牲（规则性结算，不入堆叠）。",
            });
        }
    }
}
