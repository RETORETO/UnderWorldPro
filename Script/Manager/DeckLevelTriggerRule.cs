using System.Collections.Generic;
using Godot;
using Script.Data.Effects;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【JC124·构筑级（卡组级）全局触发】——"**只要这张牌一开始加入玩家卡组就能触发这个效果**"。
    ///
    /// 卡文（JC124 尼古拉斯·勒梅）：「强制触发：当**具有哲人石标志的角色**死亡时，将其从墓地移回场上，
    /// 并由其拥有者操控。**该能力在尼古拉斯·勒梅离场后仍然生效**。」
    ///
    /// **用户 2026-09 口径**：可以做"一个有效果的指示物"，也可以做"**一个全局触发效果**"——
    /// 只要 JC124 一开始加入玩家卡组就能触发即可；并要求 **加入多个 JC124 也只能有一个效果生效**、
    /// **效果加入堆叠时用 JC124 的卡图**。
    ///
    /// 落地（＝全局触发路线）：
    ///  · **登记**（`RegisterFromDeck`，由 `DeckManager` 装载卡组时调用）：
    ///    扫描该玩家卡组，命中"带 `EffectData.DeckLevelRegistration = true` 声明的牌"即**按玩家登记一次**
    ///    （同一玩家**多张同名/同效果只登记第一份** ⇒ "多个 JC124 只有一个生效"）；
    ///  · **持久**：登记记录只依赖"**开局卡组里有没有**"，与那张卡此后身处何处（牌库/手牌/场上/墓地）无关
    ///    ⇒ 卡文"该能力在尼古拉斯·勒梅离场后仍然生效"天然成立
    ///    （刻意**不**走持续效果登记：那条路会在来源离场时被 `UnregisterPersistent` 撤销）；
    ///  · **收集**（`Collect`，由 `TriggerRegistry.DeckLevelCollector` 在事件发生时调用）：
    ///    按"已登记玩家"合成触发实例，**来源卡＝该玩家卡组里的那张实例**
    ///    ⇒ 堆叠条目**显示登记的这张牌的卡图**（JC124 的卡图）；随后走**与普通触发完全相同**的管线
    ///    （排序窗口 → 入堆叠 → 让过 → 结算）；
    ///  · **不重复**：带该声明的效果在 `TriggerRegistry` 的"扫描场上实例"路径中被**跳过**，
    ///    否则那张牌在场时会与构筑级登记各收集一次（同一死亡角色被回场两次）。
    ///
    /// 纪律：本类只做"登记 + 合成触发"，条件的判定一律复用 `EffectEngine.EvaluateCondition`
    /// （谓词如 `DiedWithCounter`），不复制任何判定逻辑。
    /// </summary>
    public static class DeckLevelTriggerRule
    {
        /// <summary>一个"已登记"的构筑级触发：来源卡实例（用于卡图/归属）+ 效果声明 + 归属玩家。</summary>
        private sealed class Registration
        {
            public int PlayerId;
            public CardInstance Source;
            public EffectData Data;
        }

        /// <summary>玩家 → 已登记记录（每玩家至多一条：多张同名只生效一份）。</summary>
        private static readonly Dictionary<int, Registration> _byPlayer = new Dictionary<int, Registration>();

        /// <summary>【每局清空】按玩家清除登记（重开对局/重载卡组时调用）。</summary>
        public static void Reset(int playerId)
        {
            _byPlayer.Remove(playerId);
        }

        /// <summary>【每局清空】所有玩家的登记一并清除。</summary>
        public static void ResetAll() => _byPlayer.Clear();

        /// <summary>该玩家是否已登记构筑级触发（调试/测试用）。</summary>
        public static bool IsRegistered(int playerId) => _byPlayer.ContainsKey(playerId);

        /// <summary>该玩家已登记的来源卡 UID（未登记返回 null）。</summary>
        public static string SourceUidOf(int playerId)
            => _byPlayer.TryGetValue(playerId, out var r) ? r.Source?.UID : null;

        /// <summary>该玩家已登记的效果 Id（未登记返回 null）。</summary>
        public static string EffectIdOf(int playerId)
            => _byPlayer.TryGetValue(playerId, out var r) ? r.Data?.EffectId : null;

        /// <summary>
        /// 从某玩家的**开局卡组**登记构筑级触发（由 `DeckManager` 在装载卡组后调用；**幂等**：
        /// 每次装载先清该玩家旧登记，再按卡组内容重新登记）。
        /// 遍历顺序＝卡组当前顺序；**命中第一张即登记并停止** ⇒ 多张同名只生效一份。
        /// </summary>
        public static void RegisterFromDeck(int playerId, IList<CardInstance> deck)
        {
            Reset(playerId);
            if (playerId <= 0 || deck == null) return;
            foreach (var card in deck)
            {
                var tmpl = card?.PlayerTemplate;
                if (tmpl?.EffectDataList == null) continue;
                foreach (var data in tmpl.EffectDataList)
                {
                    if (data == null || !data.DeckLevelRegistration) continue;
                    _byPlayer[playerId] = new Registration { PlayerId = playerId, Source = card, Data = data };
                    GD.Print($"[构筑级触发] 玩家{playerId} 从卡组登记：{tmpl.CardName} 的 {data.EffectId}"
                             + $"（来源实例 {card.UID}；同名多张只生效这一份，且该牌离场后仍然生效）");
                    return;
                }
            }
        }

        /// <summary>
        /// 【触发收集】按"已登记玩家"合成构筑级触发实例（由 `TriggerRegistry` 在**常规场上扫描之后**调用，
        /// 收集结果与普通触发**合并**走同一条管线）。
        /// 判定：
        ///  · 时机一致（`EffectData.Timing == timing`）；
        ///  · **不做生效区判定**（这正是"离场后仍然生效"的实现）；
        ///  · 条件复用 `EffectEngine.EvaluateCondition`（如 `DiedWithCounter(哲人石标志)`），
        ///    上下文卡取"事件主体卡"（死亡的卡）；
        ///  · 归属＝登记玩家（排序窗口按控制者分组时用）。
        /// </summary>
        public static List<StackEffectInstance> Collect(EffectTiming timing, TriggerContext ctx,
            EffectEngine ee)
        {
            var result = new List<StackEffectInstance>();
            if (_byPlayer.Count == 0) return result;
            ctx ??= new TriggerContext();
            var condCard = ctx.EffectiveContextCard;

            foreach (var kv in _byPlayer)
            {
                var reg = kv.Value;
                var data = reg?.Data;
                if (data == null || data.Timing != timing) continue;
                var src = reg.Source;
                if (src == null) continue;

                if (ee != null && !ee.EvaluateCondition(data.Condition?.Predicate, src, condCard, condCard,
                        ctx.TriggeringPlayerId, ctx.TargetAreaId, data.Condition?.Param))
                {
                    GD.Print($"[构筑级触发] 玩家{reg.PlayerId} 的条件不满足（{data.EffectId}，事件主体={condCard?.PlayerTemplate?.CardName}），跳过");
                    continue;
                }

                result.Add(new StackEffectInstance
                {
                    UID = System.Guid.NewGuid().ToString("N"),
                    Source = src,                       // ⇒ 堆叠条目用**登记的这张牌**的卡图（JC124）
                    OwnerPlayerId = reg.PlayerId,
                    ControllerId = reg.PlayerId,
                    EffectDataRef = data,
                    TargetCardUid = ctx.ContextCard?.UID ?? "",
                    HasValidTarget = true,
                    IsAreaTarget = false,
                    EventAmount = ctx.ExtraInt,
                    WinnerId = ctx.TriggeringPlayerId,
                    TargetAreaId = ctx.TargetAreaId,
                    // "触发时所在地区"（死亡＝死亡时所处地区）：JC124 据此把角色移回**它死亡时所在的地区**
                    SourceAreaAtTrigger = ctx.SourceAreaAtTrigger,
                });
                GD.Print($"[构筑级触发] 玩家{reg.PlayerId} 收集：{data.EffectId}（来源={src.PlayerTemplate?.CardName}，"
                         + $"事件主体={condCard?.PlayerTemplate?.CardName}）");
            }
            return result;
        }
    }
}
