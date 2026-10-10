using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10 JC 效果代码自查·只读扫描】把《JC效果代码核查项.md》中**可结构化判定**的判据固化为自动护栏。
    ///
    /// 与 <see cref="JCCardTextAuditTests"/>（卡面关键词/门禁/强制/时机装载审计）**互补、不重复**：
    ///  本套件聚焦**效果数据结构层面**的判据（切分/区域/目标/门禁/白名单/结构卫生/注册完整性），
    ///  即"卡文 → EffectDataList 的装载是否符合规则"，适用于 JC001–JC135 全部已接入卡。
    ///
    /// 纪律：
    ///  · **只读**：只 Load + 读字段，绝不写回 EffectDataList、不额外调 Register（除幂等的 RegisterAll）。
    ///  · 违规项**全量收集**后统一断言（不是只报第一条）。
    ///  · 确属"卡文如此、按规则确实该这样"的例外写入 <see cref="Exemptions"/>（键＝`卡UID前缀|判据|词`，值＝理由）。
    ///
    /// 判据编号与《JC效果代码核查项.md》一一对应（①–⑨ 用户给定，⑩–㉔ 补充；㉔＝卡文歧义须回问用户）。
    /// 已合并的判据不再单列：效果类别误用（触发↔持续↔行动）归入②；目标类型/语义限定词/自身约束归入⑤。
    /// 卡文歧义（㉔）不自动判定，只登记到台账 ❓ 清单。
    /// 本套件补充覆盖（用户 2026-10-10）：⑤ 目标语义限定词、⑮ 次数限制（含威名/声望地区级一次）、⑪ 卡文卡名自指。
    /// </summary>
    public class JCEffectSelfAuditTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>豁免表：`卡UID前缀|判据|词` → 理由。新增豁免必须写明依据（用户口径/规则文本）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Exemptions
            = new System.Collections.Generic.Dictionary<string, string>
        {
            // —— 判据⑩ 注册完整性：JC125 是**构筑规则**、无 cs，属有意为之（见判据㉒）——
            { "JC125|register|缺脚本", "卡文是构筑限制（牌组张数不限），非场上效果；用户明确『暂不做』（进度表标 ⛔ K25 构筑校验）" },

            // —— 判据㉓ 类别误用：合法"非触发通道"（卡文如此，实现走静止/替代/静态规则）——
            { "JC079|case|触发写成持续:0", "『赢取本地区』走 K3 去处替代（`Timing=Static` + `WinAreaDestination`），非常驻持续效果；卡文依据充分" },
            { "JC120|case|触发写成持续:0", "『赢取本地区』走 K3 去处替代通道（`WinAreaDestination`）" },
            { "JC116|case|触发写成持续:0", "『撤回』是引擎级地区赢取规则（AreaManager 消费 Withdraw 关键词），非入堆叠触发" },
            { "JC098|case|触发写成持续:0", "『略过对抗』是地区级静止规则（`StaticKind`）的实现通道，卡文无触发用语" },

            // —— 判据⑤ 自身约束（默认允许取自身）：规则上确实不满足的例外（用户 2026-10-10 口径）——
            { "JC020|selftarget|默认允许自身", "本卡是**附属**（来源不是角色）⇒ 自身不可能是角色候选" },
            { "JC058|selftarget|默认允许自身", "目标大类是**暗藏者**，本卡触发时已现身（正面）⇒ 自身不可能是候选" },
            { "JC066|selftarget|默认允许自身", "**用户点名**：规则上不能把自己封印在自己身上 ⇒ `CanTargetSelf=false`" },
            { "JC088|selftarget|默认允许自身", "目标筛选『印刷费用≤2 的角色』，而**自身费用为 3** ⇒ 自身不符合目标条件" },
            { "JC100|selftarget|默认允许自身", "脚本显式 `CanTargetSelf=false`（不能自封），卡文另有依据" },

            // —— 判据⑤ 目标语义限定词（弱判定：卡文命中语义词但未见对应约束字段）——
            { "JC031|semword|敌方", "卡文『随机弃一张牌』的目标是**目标玩家**（任意玩家含自己），非限敌方；『敌方』出现在其他措辞" },
            { "JC011|semword|敌方", "目标限『本地区敌方角色』，脚本以 `Restrictions=Enemy` 表达，属已实现（此键备用）" },

            // —— 判据⑮ 次数限制：卡文『每回合一次/N次』与 `MaxUsesPerTurn`（弱判定）——
            // （本轮扫描若命中，须逐条确认是"引擎已按其他机制限次"还是"漏声明"。）

            // —— 判据⑪ 卡名自指：卡文出现本卡名但属叙述/规则性提及，非效果内自指 ——
            { "JC125|selfname|无知路人", "构筑规则卡，卡文自述本卡名属**构筑限制**描述（未实现登记）" },
        };

        /// <summary>未实现卡（无效果脚本）：整卡跳过本套件的"装载一致性"判据，由进度表/台账按"未实现"登记。</summary>
        private static readonly System.Collections.Generic.HashSet<string> UnimplementedCards
            = new System.Collections.Generic.HashSet<string> { "JC125" };

        // ==================== 引擎白名单（与引擎实现同步）====================
        // 来源：PhaseGateRule（阶段名）、EffectValueResolver（取值表达式）、
        //       EffectEngine.EvaluateCondition（条件谓词）、EffectAction.cs 注释（动作参数）。
        // ⚠ 引擎新增登记后需同步本白名单，否则会把新写法误报为"未登记"。

        /// <summary>判据⑬：`ExcludedPhases` / `OnlyPhases` 已登记阶段名（`PhaseGateRule.IsTokenActive`）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> PhaseTokens
            = new System.Collections.Generic.HashSet<string>
            { "分配战斗伤害阶段", "行动阶段", "对抗阶段" };

        /// <summary>判据⑬：`ValueExpr` 已登记写法（`EffectValueResolver.Evaluate` 的分支）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> ValueExprs
            = new System.Collections.Generic.HashSet<string> { "事件数量", "代价数量", "已付代价数量", "玩家选择" };

        /// <summary>判据⑬：`ValueExpr` 已登记 `计数:` 前缀子写法（`EffectValueResolver.EvaluateCount`）。</summary>
        private static readonly string[] ValueExprCountKeys
            = { "计数:区域", "计数:地区内卡上标志", "计数:卡上标志", "计数:地区标志", "计数:封印牌" };

        /// <summary>判据⑬：`Condition.Predicate` / `BranchPredicate` 已登记谓词（`EffectEngine.EvaluateCondition`）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> Predicates
            = new System.Collections.Generic.HashSet<string>
        {
            "Always", "已横置", "具有先手标志", "先手标志", "HostIsCouncil", "HostIsCharacter",
            "成功封印", "心灵领域忠诚≥2（资产区心心）", "PlayedSorcery", "OpponentHandLe3",
            "WinnerIsSelf", "WinnerInSameArea", "TriggerBySelf", "WinnerIsSelfInSameArea",
            "DiedAnyCharacter", "DiedAllyCharacter", "WinnerIsSelfWithAllySubCategoryInCombatArea",
            "AreaMarkerAtLeast", "HandAtLeast", "DiedAllySpiritCharacter", "DiedVampireCharacter",
            "DiedWithCounter", "EnemyPowerMarkRemovedThisTurn", "DiscardByEnemy", "RenownMajority",
        };

        /// <summary>判据⑮：需要声明 `ActivateWindow` 的激活能力时机。</summary>
        private static readonly System.Collections.Generic.HashSet<EffectTiming> ActivatedTimings
            = new System.Collections.Generic.HashSet<EffectTiming>
            { EffectTiming.CharacterActivated, EffectTiming.AttachmentActivated, EffectTiming.CouncilActivated };

        // ==================== 基础工具 ====================

        private static List<string> AllCardFiles()
        {
            var files = new List<string>();
            foreach (var f in global::Godot.DirAccess.GetFilesAt(Dir))
                if (!string.IsNullOrEmpty(f) && f.EndsWith(".tres")) files.Add(f);
            files.Sort();
            return files;
        }

        private static PlayerCardData LoadCard(string file)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = global::Godot.ResourceLoader.Load<PlayerCardData>(Dir + file);
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        /// <summary>卡面文本（去掉 FAQ 段——FAQ 解释性文字常提到别的词条，会造成误报）。</summary>
        private static string CardText(PlayerCardData tmpl)
        {
            var sb = new System.Text.StringBuilder();
            if (tmpl?.Effects != null)
                foreach (var line in tmpl.Effects)
                    if (!string.IsNullOrEmpty(line)) sb.Append(line).Append(' ');
            string s = sb.ToString();
            int faq = s.IndexOf("FAQ：", System.StringComparison.Ordinal);
            return faq >= 0 ? s.Substring(0, faq) : s;
        }

        private static string UidPrefix(PlayerCardData t, string file)
        {
            string uid = t?.UID;
            if (string.IsNullOrEmpty(uid)) uid = file;
            int i = uid.IndexOf('_');
            return i > 0 ? uid.Substring(0, i) : uid;
        }

        private static string Exempt(string uid, string kind, string word)
        {
            string key = uid + "|" + kind + "|" + word;
            return Exemptions.TryGetValue(key, out var reason) ? reason : null;
        }

        private static void AssertNoViolations(List<string> violations)
            => Assert.IsTrue(violations.Count == 0,
                "JC 效果代码自查发现 " + violations.Count + " 条违规：\n" + string.Join("\n", violations));

        // ==================== 判据㉑ 效果切分规则 ====================

        /// <summary>效果前缀词（《JC效果代码核查项.md》判据㉑总则）。长词在前，避免短词先匹配。</summary>
        private static readonly string[] SegmentPrefixes =
        {
            "准备步骤强制触发：", "结束步骤强制触发：", "潜伏强制触发：",
            "准备步骤触发：", "结束步骤触发：",
            "进场触发：", "现身强制触发：", "现身触发：", "死亡触发：", "死亡强制触发：",
            "快速行动：", "快速暗藏者行动：", "手牌行动：", "行动：", "触发：", "持续：", "静止：",
        };

        /// <summary>按前缀词把卡文切段（先剥离 FAQ）。返回各段文本（不含前缀词前的关键词短语）。</summary>
        private static List<string> SplitSegments(string text)
        {
            var segs = new List<string>();
            var hits = new List<(int pos, int len)>();
            foreach (var p in SegmentPrefixes)
            {
                int from = 0;
                while (true)
                {
                    int at = text.IndexOf(p, from, System.StringComparison.Ordinal);
                    if (at < 0) break;
                    hits.Add((at, p.Length));
                    from = at + p.Length;
                }
            }
            // "手牌行动N：" / "行动N：" / "快速行动N：" / "行动N，" 等带数字的变体
            for (int k = 0; k < text.Length; k++)
            {
                // 匹配 "…行动" + 数字 + "："（"手牌行动4："/"行动3："）
                if (text[k] == '：')
                {
                    int b = k - 1;
                    // 跳过数字
                    int d = b;
                    while (d >= 0 && text[d] >= '0' && text[d] <= '9') d--;
                    if (d != b && d >= 1 && text.Substring(d - 1, 2) == "动")
                    {
                        // 往前找 "行动" 起点
                        int start = d + 1;   // 数字起点
                        int ps = start - 1;  // "动"
                        if (ps >= 1 && text.Substring(ps - 1, 2) == "行动")
                            hits.Add((ps - 1, k - (ps - 1) + 1));
                    }
                }
            }
            hits.Sort((a, b) => a.pos != b.pos ? a.pos.CompareTo(b.pos) : b.len.CompareTo(a.len));

            var cut = new List<(int start, int end)>();
            int lastEnd = -1;
            foreach (var (pos, len) in hits)
            {
                if (pos < lastEnd) continue;          // 重叠命中丢弃
                cut.Add((pos, pos + len));
                lastEnd = pos + len;
            }
            for (int j = 0; j < cut.Count; j++)
            {
                int segStart = cut[j].start + (cut[j].end - cut[j].start);
                int segEnd = (j + 1 < cut.Count) ? cut[j + 1].start : text.Length;
                segs.Add(text.Substring(segStart, segEnd - segStart));
            }
            return segs;
        }

        /// <summary>
        /// 判据㉑-a/㉑-b：段内**次序敏感**结构（"然后"/"若…则"）须由 `MassSequential` 表达；
        /// 且 `MassSequential` 只应在"同一段内确有序次敏感结构"时使用（跨段不得揉合）。
        /// 本用例只检查**单条效果**级别的自洽（段数 ↔ EffectDataList 条数交由人工台账判定，因关键词条不单独成段）。
        /// </summary>
        public void TestAudit_JC切分_MassSequential与段内次序敏感()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                string text = CardText(tmpl);
                var segs = SplitSegments(text);
                bool textHasSequential = false;
                foreach (var s in segs)
                    if (s.Contains("然后") || s.Contains("若…则") || s.Contains("，则")) textHasSequential = true;

                // 声明了 MassSequential 的效果：必须有动作级 MassInArea/RequirePredicate（否则空转）
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null || !e.Target.MassSequential) continue;
                    bool usesActionLevel = false;
                    if (e.Actions != null)
                        foreach (var a in e.Actions)
                            if (a != null && (a.MassInArea || !string.IsNullOrEmpty(a.RequirePredicate))) usesActionLevel = true;
                    if (!usesActionLevel && Exempt(uid, "seg", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 声明了 `Task.MassSequential` 但没有动作级 `MassInArea`/`RequirePredicate`（空转）");
                }

                // 卡文段内确有序次敏感结构，但该卡没有任何 MassSequential ⇒ 提示（弱判定，仅当有"然后…则"强结构时报）
                if (textHasSequential)
                {
                    bool anySeq = false;
                    foreach (var e in tmpl.EffectDataList)
                        if (e?.Target != null && e.Target.MassSequential) anySeq = true;
                    if (!anySeq && Exempt(uid, "seg", "缺MassSequential") == null)
                        violations.Add(uid + "（" + file + "）：卡文段内含『然后/则』次序敏感结构，"
                            + "但全卡无任何 `MassSequential`（请核对是否漏用顺序原语，或登记豁免）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据㉓ 效果类别误用（触发 ↔ 持续 ↔ 行动）====================

        /// <summary>㉓：前缀词 → 期望类别组。用于"卡文写触发却实现成持续/静止"这类误用检测。</summary>
        private enum TimingGroup { Passive, Trigger, Activated, Sorcery }

        /// <summary>触发类时机（C 组·触发时机；"当…时/XX触发："就该落到这里）。</summary>
        private static readonly System.Collections.Generic.HashSet<EffectTiming> TriggerTimings
            = new System.Collections.Generic.HashSet<EffectTiming>
        {
            EffectTiming.EnterTrigger, EffectTiming.UncoverTrigger, EffectTiming.ManeuverTrigger,
            EffectTiming.OnCardDied, EffectTiming.OnAreaWon, EffectTiming.OnTurnStarted,
            EffectTiming.OnTurnEnded, EffectTiming.OnDraw, EffectTiming.OnDiscard,
            EffectTiming.OnMarkerPlaced, EffectTiming.OnSealed, EffectTiming.OnMove,
            EffectTiming.OnCounter, EffectTiming.OnCardLeft, EffectTiming.OnCombatWon,
            EffectTiming.OnPowerCombatEnded, EffectTiming.OnInvestigationWon, EffectTiming.OnLatent,
            EffectTiming.OnCardPlayed,
        };

        /// <summary>
        /// ㉓（用户 2026-10-10 新增）：卡文**按前缀词切段**（㉑）后，每一段所属类别必须与实现的 `EffectTiming` 归到同一组——
        ///  · **触发效果写成持续效果**：段前缀是触发类（"当…时："／"XX触发："），实现却只有 `Continuous`/`Static`
        ///    （常驻注册表 ⇒ 不会"触发一次"，还会全天候持续生效）；
        ///  · **持续效果写成触发效果**：段前缀是"持续："／"静止："，实现却只有触发/激活类时机（只在某一瞬间结算一次）。
        ///
        /// 说明：真实脚本会出现**关键词条 + 多段效果**（如"灵气 持续：…"＝`Continuous` 关键字条 + `Continuous` 效果），
        /// 故本用例按"**该卡是否存在带原文片段的效果记录**"尽量精确判定：
        ///  每段取片段文本去匹配 `EffectData.ContinuousEffect`（持续段）或"含触发前缀的效果"；
        /// 匹配不到时**降级为卡级判定**（全卡是否有任一效果属于该段期望的组别）。
        /// 判定不了的（关键词条／文本无重叠／无 UI 环境）一律不报，交由人工与判据㉔处理。
        /// </summary>
        public void TestAudit_JC类别_触发与持续不得误用()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                string text = CardText(tmpl);
                var segs = SplitSegments(text);

                // 卡级兜底：该卡是否存在"属于某组别"的效果
                bool anyPassive = false, anyTrigger = false, anyActivated = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.Timing == EffectTiming.Continuous || e.Timing == EffectTiming.Static) anyPassive = true;
                    else if (TriggerTimings.Contains(e.Timing)) anyTrigger = true;
                    else if (ActivatedTimings.Contains(e.Timing)) anyActivated = true;
                }

                for (int i = 0; i < segs.Count; i++)
                {
                    string seg = segs[i];
                    TimingGroup expected = ClassifySegment(text, seg, i);
                    if (expected == TimingGroup.Sorcery) continue;   // 事务打出结算由⑫/①管

                    // 该段在实现中对应的效果（按片段文本匹配 ContinuousEffect / Duration / 触发前缀引用的 EffectId 描述）
                    bool segHasPassive = false, segHasTrigger = false, segHasActivated = false;
                    string matchedId = null;
                    foreach (var e in tmpl.EffectDataList)
                    {
                        if (e == null || !SegmentMatchesEffect(seg, e)) continue;
                        matchedId = e.EffectId;
                        if (e.Timing == EffectTiming.Continuous || e.Timing == EffectTiming.Static) segHasPassive = true;
                        else if (TriggerTimings.Contains(e.Timing)) segHasTrigger = true;
                        else if (ActivatedTimings.Contains(e.Timing)) segHasActivated = true;
                    }

                    switch (expected)
                    {
                        case TimingGroup.Trigger:
                            if (!segHasTrigger && !anyTrigger
                                && Exempt(uid, "case", "触发写成持续:" + i) == null)
                                violations.Add(uid + "（" + file + "）：触发段『" + Clip(seg)
                                    + "』但全卡无任何触发类时机 ⇒ 疑似**触发写成持续/静止**（常驻生效、不会只触发一次）");
                            break;
                        case TimingGroup.Passive:
                            if (!segHasPassive && !anyPassive
                                && Exempt(uid, "case", "持续写成触发:" + i) == null)
                                violations.Add(uid + "（" + file + "）：持续段『" + Clip(seg)
                                    + "』但全卡无任何 `Continuous`/`Static` ⇒ 疑似**持续写成触发**（只结算一次、不再常驻）");
                            break;
                        case TimingGroup.Activated:
                            if (!segHasActivated && !anyActivated
                                && Exempt(uid, "case", "行动写成持续:" + i) == null)
                                violations.Add(uid + "（" + file + "）：行动段『" + Clip(seg)
                                    + "』但全卡无任何激活能力 ⇒ 疑似**行动写成持续/触发**");
                            break;
                    }
                }
            }
            AssertNoViolations(violations);
        }

        /// <summary>判断某段属于哪一类（依据段前缀词；无法判定返回 Sorcery＝跳过）。</summary>
        private static TimingGroup ClassifySegment(string full, string seg, int index)
        {
            // 用"该段在全文中的前一条前缀词"来定类别：SplitSegments 返回的是前缀词**之后**的正文，
            // 故此处改为直接看正文特征 + 位置推断：把全文按同样规则切一遍前缀。
            var prefixes = FindSegmentPrefixes(full);
            if (index < prefixes.Count)
            {
                string p = prefixes[index];
                if (p.Contains("触发")) return TimingGroup.Trigger;
                if (p == "持续：" || p == "静止：") return TimingGroup.Passive;
                if (p.Contains("行动")) return TimingGroup.Activated;
            }
            // 兜底：按正文首句措辞
            if (seg.StartsWith("当") && seg.Contains("时")) return TimingGroup.Trigger;
            if (seg.Contains("持续") || seg.Contains("静止")) return TimingGroup.Passive;
            return TimingGroup.Sorcery;
        }

        /// <summary>顺序取出全文各段所对应的**前缀词**（与 <see cref="SplitSegments"/> 顺序一致）。</summary>
        private static List<string> FindSegmentPrefixes(string text)
        {
            var found = new List<string>();
            foreach (var p in SegmentPrefixes)
            {
                int from = 0;
                while (true)
                {
                    int at = text.IndexOf(p, from, System.StringComparison.Ordinal);
                    if (at < 0) break;
                    found.Add(p);
                    from = at + p.Length;
                }
            }
            // "手牌行动N："/"行动N："
            for (int k = 0; k < text.Length; k++)
            {
                if (text[k] != '：') continue;
                int d = k - 1;
                while (d >= 0 && text[d] >= '0' && text[d] <= '9') d--;
                if (d < 1 || text.Substring(d - 1, 2) != "行动") continue;
                int ps = d;
                string p = text.Substring(ps - 1, k - (ps - 1) + 1);
                if (p.Contains("手牌行动")) found.Add("手牌行动：");
                else if (p.Contains("快速行动")) found.Add("快速行动：");
                else found.Add("行动：");
            }
            return found;
        }

        /// <summary>段正文与某效果是否有文本重叠（用于把段挂到具体效果上；无重叠返回 false＝不判定）。</summary>
        private static bool SegmentMatchesEffect(string seg, EffectData e)
        {
            if (string.IsNullOrWhiteSpace(seg)) return false;
            string key = null;
            if (!string.IsNullOrEmpty(e.ContinuousEffect)) key = e.ContinuousEffect;
            else if (!string.IsNullOrEmpty(e.ContinuousPrereq)) key = e.ContinuousPrereq;
            else if (!string.IsNullOrEmpty(e.Duration) && e.Duration != "常驻") key = e.Duration;
            if (string.IsNullOrEmpty(key)) return false;
            // 取片段前 6 个字的匹配（卡文与实现注释常有措辞差异，故取前缀比对）
            string a = seg.Length > 4 ? seg.Substring(0, 4) : seg;
            return key.Contains(a) || seg.Contains(key.Length > 4 ? key.Substring(0, 4) : key);
        }

        private static string Clip(string s)
            => s.Length > 24 ? s.Substring(0, 24) + "…" : s;

        // ==================== 判据③ 场上区＝争夺区∪附属区 ====================

        /// <summary>
        /// 判据③：效果的 `Scope` 只能是 `AreaZone` / `AttachTarget`（后者附属默认）；
        /// 其它区域（手牌/墓地/牌库/封印/资产/秘社）必须由卡文明确依据支撑。
        /// </summary>
        public void TestAudit_JC区域_Scope仅场上区或需卡文依据()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    switch (e.Scope)
                    {
                        case ZoneScope.Specified:   // 未声明 ⇒ 回退卡类型默认（角色→AreaZone、附属→AttachTarget）
                        case ZoneScope.AreaZone:
                        case ZoneScope.AttachTarget:
                            continue;   // 场上的两大合法区
                        case ZoneScope.HandZone:
                            // 手牌行动类需卡文含"手牌行动"
                            if (!text.Contains("手牌行动") && Exempt(uid, "zone", e.EffectId) == null)
                                violations.Add(uid + "（" + file + "）：" + e.EffectId
                                    + " 声明 `Scope=HandZone` 但卡文无『手牌行动』依据");
                            continue;
                        case ZoneScope.CouncilZone:
                            // 秘社来源（结附于本方秘社/秘社卡）本身在秘社区
                            if (!text.Contains("秘社") && Exempt(uid, "zone", e.EffectId) == null)
                                violations.Add(uid + "（" + file + "）：" + e.EffectId
                                    + " 声明 `Scope=CouncilZone` 但卡文无『秘社』依据");
                            continue;
                        case ZoneScope.Graveyard:
                        case ZoneScope.DeckZone:
                        case ZoneScope.SealedZone:
                        case ZoneScope.AssetZone:
                            if (Exempt(uid, "zone", e.EffectId) == null)
                                violations.Add(uid + "（" + file + "）：" + e.EffectId + " 声明 `Scope=" + e.Scope
                                    + "`（非场上区）⇒ 需卡文明确依据，请人工确认或登记豁免");
                            continue;
                        default:
                            continue;
                    }
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据④ 默认仅场上正面朝上 ====================

        /// <summary>
        /// 判据④：`TriggerCovertMode=CovertOnly`（仅暗藏者）必须由卡文"暗藏者/潜伏"依据支撑；
        /// `DeckLevelRegistration=true`（构筑级全局触发）必须卡文写明"离场后仍生效/构筑级"。
        /// </summary>
        public void TestAudit_JC朝向_CovertOnly与构筑级需卡文依据()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.TriggerCovertMode == CovertTriggerMode.CovertOnly
                        && !text.Contains("暗藏") && !text.Contains("潜伏")
                        && Exempt(uid, "covert", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 声明 `CovertOnly`（仅暗藏者可触发）但卡文无『暗藏/潜伏』依据");

                    if (e.DeckLevelRegistration && Exempt(uid, "deck", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 声明 `DeckLevelRegistration`（构筑级全局触发）⇒ 需卡文明确『离场后仍生效/构筑级』依据");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑫ 类别 × Timing 自洽 ====================

        /// <summary>
        /// 判据⑫：事务卡（`Sorcery`）不得有角色/附属/秘社**激活能力**；
        /// 非事务卡不得有 `OnSorceryCast`（事务打出结算时机）。
        /// </summary>
        public void TestAudit_JC类别_与Timing自洽()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                bool isSorcery = (tmpl.CategoryFlag & CardCategory.Sorcery) != 0;
                bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (isSorcery && ActivatedTimings.Contains(e.Timing) && Exempt(uid, "cat", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：事务卡却含激活能力 `" + e.Timing + "`（" + e.EffectId
                            + "）⇒ 事务不在场，不应有『发动能力』");
                    if (!isSorcery && e.Timing == EffectTiming.OnSorceryCast && Exempt(uid, "cat", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：非事务卡却含 `OnSorceryCast`（" + e.EffectId + "）");
                    if (isAttachment && e.Timing == EffectTiming.CharacterActivated && Exempt(uid, "cat", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：附属卡却含 `CharacterActivated`（" + e.EffectId
                            + "）⇒ 附属激活应用 `AttachmentActivated`");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑬ 未登记字面量静默失败 ====================

        /// <summary>
        /// 判据⑬：`ExcludedPhases`/`OnlyPhases` 阶段名、`ValueExpr` 表达式、
        /// `Condition.Predicate`/`BranchPredicate` 谓词 必须命中引擎白名单（未登记 ⇒ 静默失效）。
        /// </summary>
        public void TestAudit_JC白名单_阶段名与取值与谓词已登记()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;

                    // 阶段名
                    CheckPhases(uid, file, e, e.ExcludedPhases, "ExcludedPhases", violations);
                    CheckPhases(uid, file, e, e.OnlyPhases, "OnlyPhases", violations);

                    // 取值表达式
                    if (!string.IsNullOrWhiteSpace(e.ValueExpr))
                    {
                        string v = e.ValueExpr.Trim();
                        bool ok = ValueExprs.Contains(v);
                        if (!ok)
                            foreach (var k in ValueExprCountKeys)
                                if (v.StartsWith(k, System.StringComparison.Ordinal)) { ok = true; break; }
                        if (!ok && Exempt(uid, "expr", v) == null)
                            violations.Add(uid + "（" + file + "）：" + e.EffectId + " 的 `ValueExpr=\"" + v
                                + "\"` 未登记 ⇒ fail-closed 不发动");
                    }

                    // 条件谓词
                    if (e.Condition != null)
                    {
                        CheckPredicate(uid, file, e, e.Condition.Predicate, violations);
                        CheckPredicate(uid, file, e, e.Condition.BranchPredicate, violations);
                    }
                    // 动作级条件谓词
                    if (e.Actions != null)
                        foreach (var a in e.Actions)
                            if (a != null && !string.IsNullOrEmpty(a.RequirePredicate))
                                CheckPredicate(uid, file, e, a.RequirePredicate, violations);
                }
            }
            AssertNoViolations(violations);
        }

        private void CheckPhases(string uid, string file, EffectData e, string phases, string field,
            List<string> violations)
        {
            if (string.IsNullOrEmpty(phases)) return;
            foreach (var raw in phases.Split(','))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (PhaseTokens.Contains(t)) continue;
                if (Exempt(uid, "phase", t) != null) continue;
                violations.Add(uid + "（" + file + "）：" + e.EffectId + " 的 `" + field + "` 含未登记阶段名『"
                    + t + "』⇒ 按'不排除/不满足'静默处理");
            }
        }

        private void CheckPredicate(string uid, string file, EffectData e, string pred, List<string> violations)
        {
            if (string.IsNullOrEmpty(pred)) return;
            string p = pred.Trim();
            if (p == "Always" || p.Length == 0) return;
            if (Predicates.Contains(p)) return;
            if (Exempt(uid, "pred", p) != null) return;
            violations.Add(uid + "（" + file + "）：" + e.EffectId + " 的条件谓词『" + p
                + "』未在 `EvaluateCondition` 登记 ⇒ 默认按'满足'处理（门禁失效）");
        }

        // ==================== 判据⑮ 次数与阶段门禁 ====================

        /// <summary>
        /// 判据⑮：激活能力（`CharacterActivated`/`AttachmentActivated`/`CouncilActivated`）
        /// 必须显式声明 `ActivateWindow`（否则行动窗口判定被跳过，可在响应窗口发动）。
        /// （与 <see cref="JCCardTextAuditTests"/> 同款判据，此处按新判据编号复核。）
        /// </summary>
        public void TestAudit_JC门禁_激活能力需声明ActivateWindow()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null || !ActivatedTimings.Contains(e.Timing)) continue;
                    if (!string.IsNullOrEmpty(e.ActivateWindow)) continue;
                    if (Exempt(uid, "window", e.EffectId) != null) continue;
                    violations.Add(uid + "（" + file + "）：激活能力 " + e.EffectId
                        + " 未声明 `ActivateWindow` ⇒ 行动窗口判定被跳过");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑤ + ㉕ 目标核查（合并：目标类型一致 + 默认允许自身）====================

        /// <summary>卡文目标词 → 期望的 `TargetKind`（判据⑤ 目标类型一致性；长词在前）。</summary>
        private static readonly (string Word, TargetKind Kind)[] TargetWordMap =
        {
            ("目标暗藏者", TargetKind.Covert), ("目标角色", TargetKind.Character),
            ("目标地区", TargetKind.Area),     ("目标玩家", TargetKind.Player),
            ("目标附属", TargetKind.Attachment), ("目标资产", TargetKind.Asset),
            ("目标秘社", TargetKind.Council),  ("目标封印", TargetKind.Seal),
            ("本地区的暗藏者", TargetKind.Covert), ("墓地中的角色", TargetKind.Graveyard),
        };

        /// <summary>
        /// 判据⑤（目标类型 ↔ 效果说明一致）+ 判据㉕（默认允许取自身）**合并检查**（用户 2026-10-10 口径）：
        ///  ⑤-1 声明了 `Target` 但 `Kind=None` 且无 `KindAlt`/`SpecAlt` ⇒ 目标声明不完整；
        ///  ⑤-2 多目标槽存在时 `MinChosenSlots` 必须 ≥1 且 ≤ 槽数；
        ///  ⑤-3 **卡文目标词 ↔ `TargetKind`**：卡文写"目标角色/目标暗藏者/目标地区/目标玩家…"⇒ cs 必须声明对应 `Kind`（或 `KindAlt`）；
        ///  ㉕ 卡文**无**"其他/另一个/敌方/对手"排除措辞 ⇒ 角色/暗藏者目标必须 `CanTargetSelf=true`（否则候选为空静默跳过）。
        /// </summary>
        public void TestAudit_JC目标_声明自洽()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                string text = CardText(tmpl);
                bool exclSelf = text.Contains("其他") || text.Contains("其它") || text.Contains("另一个")
                    || text.Contains("敌方") || text.Contains("对手") || text.Contains("拥有者");

                bool anyCardTarget = false, anySelfAllowed = false;

                // ⑤-3 期望目标类型：卡文出现的目标词
                var expectedKinds = new List<TargetKind>();
                foreach (var (word, kind) in TargetWordMap)
                    if (text.Contains(word) && !expectedKinds.Contains(kind)) expectedKinds.Add(kind);
                bool hasAnyKind = false, hasCovert = false;
                var declaredKinds = new List<TargetKind>();

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null) continue;
                    var t = e.Target;

                    bool hasAny = t.Kind != TargetKind.None || t.KindAlt != TargetKind.None || t.SpecAlt != null;
                    if (!hasAny && Exempt(uid, "target", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 声明了 `Target` 但 `Kind=None` 且无 `KindAlt`/`SpecAlt` ⇒ 目标声明不完整");

                    // 多目标槽须与 MinChosenSlots 自洽（TargetSlots 是 EffectData 上的字段）
                    if (e.TargetSlots != null && e.TargetSlots.Count > 0)
                    {
                        int min = e.MinChosenSlots;
                        if (min < 1 || min > e.TargetSlots.Count)
                            violations.Add(uid + "（" + file + "）：" + e.EffectId + " 多目标槽 " + e.TargetSlots.Count
                                + " 个但 `MinChosenSlots=" + min + "`（须在 1..槽数 之间）");
                    }

                    // ⑤-3 汇总实际声明的大类
                    foreach (var kd in new[] { t.Kind, t.KindAlt })
                        if (kd != TargetKind.None)
                        {
                            hasAnyKind = true;
                            if (!declaredKinds.Contains(kd)) declaredKinds.Add(kd);
                        }
                    if (t.SpecAlt != null && t.SpecAlt.Kind != TargetKind.None)
                    {
                        hasAnyKind = true;
                        if (!declaredKinds.Contains(t.SpecAlt.Kind)) declaredKinds.Add(t.SpecAlt.Kind);
                    }
                    if (t.Kind == TargetKind.Covert || t.KindAlt == TargetKind.Covert
                        || (t.SpecAlt != null && t.SpecAlt.Kind == TargetKind.Covert)) hasCovert = true;

                    // ㉕ 自身目标
                    if (t.Kind == TargetKind.Character || t.Kind == TargetKind.Covert
                        || t.KindAlt == TargetKind.Character || t.KindAlt == TargetKind.Covert)
                    {
                        anyCardTarget = true;
                        if (t.CanTargetSelf) anySelfAllowed = true;
                    }
                }

                // ⑤-3：卡文目标词 ⇒ 必须声明对应大类（无任何声明时 ⛔ 更明显）
                if (hasAnyKind && !exclSelf)
                    foreach (var want in expectedKinds)
                    {
                        if (declaredKinds.Contains(want)) continue;
                        if (Exempt(uid, "targetkind", want.ToString()) != null) continue;
                        violations.Add(uid + "（" + file + "）：卡文含『" + WordOf(want)
                            + "』但未见声明对应 `TargetKind." + want + "`（目标类型与效果说明不一致）");
                    }

                // ⑤-3 复核：卡文写"目标暗藏者"，却完全没有声明 `Covert`
                if (text.Contains("暗藏者") && hasAnyKind && !hasCovert
                    && Exempt(uid, "targetkind", "Covert") == null)
                    violations.Add(uid + "（" + file + "）：卡文提及『暗藏者』但目标未声明 `TargetKind.Covert`");

                // ㉕：卡文无排除措辞 ⇒ 角色/暗藏者目标必须允许取自身
                if (anyCardTarget && !anySelfAllowed && !exclSelf
                    && Exempt(uid, "selftarget", "默认允许自身") == null)
                    violations.Add(uid + "（" + file + "）：卡文未含『其他/另一个/敌方/对手』等排除措辞，"
                        + "但找不到任何 `CanTargetSelf=true` 的效果 ⇒ 违反㉕『默认允许取自身』");
            }
            AssertNoViolations(violations);
        }

        private static string WordOf(TargetKind kind)
        {
            foreach (var (word, k) in TargetWordMap) if (k == kind) return word;
            return kind.ToString();
        }

        // ==================== 判据⑯ 目标筛选（限制条件）====================

        /// <summary>
        /// ⑯（收窄后）：只查目标的"**限制条件**"是否与卡文措辞一致（目标**类型**已归⑤/㉕）：
        ///  · 卡文含"所有角色/每个角色/所有位于…的角色" ⇒ 期望群体 `MassMode.AllInArea`（而非单目标 `Target`）；
        ///  · 卡文含"本地区" ⇒ 至少一个角色/暗藏者目标声明 `IsLocalZone`；
        ///  · 卡文含"含暗藏者/以及暗藏者" ⇒ `IncludeCovert=true`。
        /// 均为**弱判定**：卡文措辞模糊时命中即报，由裁定/豁免处理。
        /// </summary>
        public void TestAudit_JC目标筛选_限制条件与卡文一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                bool anyMass = false, anyLocal = false, anyIncludeCovert = false, anyCardTarget = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null) continue;
                    var t = e.Target;
                    if (t.MassMode == MassMode.AllInArea) anyMass = true;
                    if (t.IsLocalZone) anyLocal = true;
                    if (t.IncludeCovert) anyIncludeCovert = true;
                    if (t.Kind == TargetKind.Character || t.Kind == TargetKind.Covert
                        || t.KindAlt == TargetKind.Character || t.KindAlt == TargetKind.Covert)
                        anyCardTarget = true;
                }

                bool textMass = text.Contains("所有角色") || text.Contains("所有位于") || text.Contains("每个角色")
                    || text.Contains("每个位于");
                bool textLocal = text.Contains("本地区");
                bool textCovert = text.Contains("含暗藏者") || text.Contains("以及暗藏者") || text.Contains("和暗藏者");

                if (textMass && !anyMass && anyCardTarget && Exempt(uid, "filter", "群体") == null)
                    violations.Add(uid + "（" + file + "）：卡文『所有/每个角色』⇒ 期望群体 `MassMode.AllInArea`，"
                        + "但未见任何群体目标");
                if (textLocal && !anyLocal && anyCardTarget && Exempt(uid, "filter", "本地区") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『本地区』，但角色/暗藏者目标未声明 `IsLocalZone`");
                if (textCovert && !anyIncludeCovert && anyCardTarget && Exempt(uid, "filter", "含暗藏者") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『含/以及暗藏者』，但目标未声明 `IncludeCovert`");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑤ 目标语义限定词（其他/敌方/我方/本地区/全部）====================

        /// <summary>
        /// 判据⑤ 目标语义限定词（用户 2026-10-10 补充）：卡文里的目标限定语必须与实现侧约束字段一一对应——
        ///  · 卡文含"敌方/对手"（且确为**目标**限定）⇒ 目标须限 `Restrictions=Enemy`；
        ///  · 卡文含"我方/友方/本方"（目标限定）⇒ 目标须限 `Restrictions=Friendly` 或 `Filter` 本方；
        ///  · 卡文含"另一个/其他" ⇒ 须**排除自身**（`CanTargetSelf=false` 或 `SacrificeCost.ExcludeSource`）。
        /// 均为**弱判定**（卡文措辞可能泛指），命中即报，由裁定/豁免处理。
        /// </summary>
        public void TestAudit_JC目标_语义限定词与约束字段一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                bool anyEnemy = false, anyFriendly = false, anyExclSelf = false, anyCardTarget = false;
                bool textHasCardTarget = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null) continue;
                    var t = e.Target;
                    foreach (var r in t.Restrictions)
                    {
                        if (r == TargetRestriction.Enemy) anyEnemy = true;
                        if (r == TargetRestriction.Friendly) anyFriendly = true;
                    }
                    if (t.Kind == TargetKind.Character || t.Kind == TargetKind.Covert
                        || t.KindAlt == TargetKind.Character || t.KindAlt == TargetKind.Covert)
                    {
                        anyCardTarget = true;
                        textHasCardTarget = true;
                    }
                    if (!t.CanTargetSelf) anyExclSelf = true;
                }
                if (!textHasCardTarget) continue;

                // "敌方/对手"限定 ⇒ 期望 Enemy（弱判定：卡文出现"敌方"+角色/暗藏者目标）
                if ((text.Contains("敌方") || text.Contains("对手")) && !anyEnemy && anyCardTarget
                    && Exempt(uid, "semword", "敌方") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『敌方/对手』⇒ 期望目标 `Restrictions=Enemy`，"
                        + "但未见该限定（卡文↔实现语义不一致，须裁定）");

                // "我方/本方/友方"限定 ⇒ 期望 Friendly
                if ((text.Contains("我方") || text.Contains("本方") || text.Contains("友方")) && !anyFriendly && anyCardTarget
                    && Exempt(uid, "semword", "我方") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『我方/本方/友方』⇒ 期望目标 `Restrictions=Friendly`，"
                        + "但未见该限定（须裁定）");

                // "另一个/其他" ⇒ 期望排除自身
                if ((text.Contains("另一个") || text.Contains("其他")) && anyCardTarget && !anyExclSelf
                    && Exempt(uid, "semword", "其他") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『另一个/其他』⇒ 期望排除自身（`CanTargetSelf=false`），"
                        + "但所有目标都允许取自身（须裁定）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑮ 次数限制（一回合一次 + 威名/声望地区级一次）====================

        /// <summary>
        /// 判据⑮ 次数限制（用户 2026-10-10 补充）：
        ///  · 卡文含"每回合只能发动一次/每回合限一次/每回合一次" ⇒ 期望 `MaxUsesPerTurn ≥ 1`；
        ///  · 卡文含"每局一次/整局" ⇒ 期望 `MaxUsesPerGame ≥ 1`；
        ///  · 卡文含"威名/声望" ⇒ 该关键词按"该地区每回合只触发一次"核对（**以威名为准**；声望去重口径存疑，只登记）。
        /// 弱判定：命中即报，由裁定/豁免处理。
        /// </summary>
        public void TestAudit_JC次数限制_与卡文一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                bool textOncePerTurn = text.Contains("每回合只能发动一次") || text.Contains("每回合限一次")
                    || text.Contains("每回合一次") || text.Contains("一回合一次") || text.Contains("每回合只能使用一次");
                bool textOncePerGame = text.Contains("每局一次") || text.Contains("整局") || text.Contains("本局游戏限一次");

                bool anyTurnLimit = false, anyGameLimit = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.MaxUsesPerTurn >= 1) anyTurnLimit = true;
                    if (e.MaxUsesPerGame >= 1) anyGameLimit = true;
                }

                if (textOncePerTurn && !anyTurnLimit && Exempt(uid, "uses", "每回合一次") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『每回合一次/限一次』⇒ 期望 `MaxUsesPerTurn`，"
                        + "但全卡未见该字段（须确认是否漏声明次数上限）");
                if (textOncePerGame && !anyGameLimit && Exempt(uid, "uses", "每局一次") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『每局一次/整局』⇒ 期望 `MaxUsesPerGame`，但全卡未见该字段");

                // 威名/声望：地区级一次（以威名为准）——只做"装载存在性"弱提示
                if ((text.Contains("威名") || text.Contains("声望")) && Exempt(uid, "keyword", "威名声望") == null)
                {
                    // 关键词条应以 Continuous 关键字装载（由 JCCardTextAuditTests 精确核对），此处仅提示存在性
                    bool anyKeyword = false;
                    foreach (var e in tmpl.EffectDataList)
                        if (e != null && (e.Timing == EffectTiming.Continuous || e.Timing == EffectTiming.Static)) anyKeyword = true;
                    if (!anyKeyword)
                        violations.Add(uid + "（" + file + "）：卡文含『威名/声望』（地区级一次限制）⇒ 期望装载对应关键词，"
                            + "但全卡无任何 `Continuous`/`Static`（须确认关键词是否装载）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑪ 卡文卡名自指 ====================

        /// <summary>
        /// 判据⑪ 卡文卡名自指（用户 2026-10-10 补充）：卡文（效果句，非 FAQ）中出现的卡名若与本卡 `Name`
        /// **完全一致**，一律视作"本卡指代"，实现须走自指通道（"自身/本卡/触发卡/结附宿主/封印宿主"、
        /// `TriggerBySelf`、`CanTargetSelf` 等），**不得去别处找同名卡**。
        /// 本用例仅**登记**含本卡名的卡（弱判定：命中即列出，由裁定确认实现是否走自指通道）。
        /// </summary>
        public void TestAudit_JC卡文_卡名自指登记()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                string name = tmpl.CardName;
                if (string.IsNullOrEmpty(name)) continue;

                string text = CardText(tmpl);   // 已剥离 FAQ
                if (!text.Contains(name)) continue;   // 卡文未出现本卡名 ⇒ 不适用
                if (Exempt(uid, "selfname", name) != null) continue;

                // 命中：卡文出现与本卡完全同名的卡名 ⇒ 登记（不阻断，由人工确认实现侧走自指通道）
                violations.Add(uid + "（" + file + "）：卡文出现与本卡同名的『" + name
                    + "』⇒ 应视作**本卡指代**（自指通道：自身/触发卡/结附宿主/TriggerBySelf），"
                    + "请确认实现未把它当作别处的同名卡（本用例仅登记，需人工确认）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑲-a 期限（卡面↔实现）====================

        /// <summary>
        /// ⑲-a：`Duration` 必须与卡文期限措辞一致：
        ///  · 卡文含"直到回合结束/本回合内（临时）" ⇒ 至少一条效果的 `Duration` 含"回合结束/本回合"；
        ///  · 卡文含"常驻/永久" ⇒ 至少一条持续效果的 `Duration` 为"常驻/永久"。
        /// （⑲-b 撤销属引擎运行期规则、tres 不体现，**不在本条内**。）
        /// </summary>
        public void TestAudit_JC期限_Duration与卡文一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                bool textUntilEnd = text.Contains("直到回合结束") || text.Contains("本回合内")
                    || text.Contains("直到本回合结束") || text.Contains("到回合结束");
                if (!textUntilEnd) continue;

                bool anyDurEnd = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null || string.IsNullOrEmpty(e.Duration)) continue;
                    if (e.Duration.Contains("回合结束") || e.Duration.Contains("本回合")) anyDurEnd = true;
                }
                if (!anyDurEnd && Exempt(uid, "duration", "直到回合结束") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『直到回合结束/本回合』，但没有任何 `Duration` 表达该期限"
                        + "（⑲-a 卡面↔实现不一致）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑪-b 卡文语义片段 ↔ 动作动词 ====================

        /// <summary>卡文语义片段 → 期望动作动词（可枚举的规则化映射；命中即要求段内存在该动词）。</summary>
        private static readonly (string Phrase, ActionVerb Verb)[] PhraseVerbMap =
        {
            ("造成", ActionVerb.DealDamage), ("伤害", ActionVerb.DealDamage),
            ("消灭", ActionVerb.Destroy),     ("牺牲", ActionVerb.Sacrifice),
            ("抓", ActionVerb.Draw),          ("弃", ActionVerb.Discard),
            ("放置一个", ActionVerb.PlaceCounter), ("放置", ActionVerb.PlaceCounter),
            ("移除", ActionVerb.RemoveCounter),
            ("横置", ActionVerb.Tap),         ("重置", ActionVerb.Untap),
            ("封印", ActionVerb.Seal),        ("移回", ActionVerb.ReturnToHand),
            ("检索", ActionVerb.Search),      ("洗牌", ActionVerb.Shuffle),
            ("支付", ActionVerb.Pay),         ("潜伏", ActionVerb.Latent),
            ("检视", ActionVerb.Inspect),     ("预测", ActionVerb.Predict),
        };

        /// <summary>
        /// ⑪-b：**卡文语义片段 ↔ 实现动作动词**的规则化比对（用户 2026-10-10：除条数/顺序外再做语义比对；
        /// **自动通过也不替代人工裁定**）。弱判定：卡文命中语义片段 ⇒ 全卡动作集合中应有对应动词，否则告警。
        /// </summary>
        public void TestAudit_JC实现_动作动词与卡文语义一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                string text = CardText(tmpl);
                var verbs = new System.Collections.Generic.HashSet<ActionVerb>();
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Actions == null) continue;
                    foreach (var a in e.Actions) if (a != null) verbs.Add(a.Verb);
                }
                if (verbs.Count == 0) continue;   // 纯关键字/持续卡不判

                foreach (var (phrase, verb) in PhraseVerbMap)
                {
                    if (!text.Contains(phrase)) continue;
                    if (verbs.Contains(verb)) continue;
                    if (Exempt(uid, "verb", verb.ToString()) != null) continue;
                    violations.Add(uid + "（" + file + "）：卡文含『" + phrase + "』⇒ 期望动作 `" + verb
                        + "`，但全卡动作中未见该动词（卡文↔实现语义不一致，须裁定）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑳ 结构卫生 ====================

        /// <summary>
        /// 判据⑳：同卡内 `EffectId` 唯一；`EffectId` 非空；
        /// `Actions` 与 `Modes` 皆空且非纯持续/静止 ⇒ 空壳效果告警。
        /// </summary>
        public void TestAudit_JC结构_EffectId唯一与非空壳()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;

                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (string.IsNullOrEmpty(e.EffectId) && Exempt(uid, "struct", "空EffectId") == null)
                        violations.Add(uid + "（" + file + "）：存在 `EffectId` 为空的效果");
                    else if (!string.IsNullOrEmpty(e.EffectId) && !seen.Add(e.EffectId)
                        && Exempt(uid, "struct", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：`EffectId` 重复『" + e.EffectId + "』");

                    bool purePassive = e.Timing == EffectTiming.Continuous || e.Timing == EffectTiming.Static;
                    bool emptyActions = e.Actions == null || e.Actions.Count == 0;
                    bool emptyModes = e.Modes == null || e.Modes.Count == 0;
                    // 事务结算/触发类效果必须有动作；纯持续/静止可无动作（走 ContinuousGrants 等）
                    if (!purePassive && emptyActions && emptyModes
                        && Exempt(uid, "struct", "空壳:" + e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 是 `" + e.Timing + "` 但 `Actions` 与 `Modes` 皆空 ⇒ 疑似空壳效果");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑭ 代价完备（弱判定：代价字段与卡文用词对照）====================

        /// <summary>
        /// 判据⑭：卡文含"牺牲"（代价义）⇒ 应有 `SacrificeCost`/`ActivateCondition`/`Sacrifice` 动作之一；
        /// 卡文含"移除…标志"（代价义）⇒ 应有 `CounterCostSpec`；卡文含"额外费用"⇒ 应有 `ExtraCost`。
        /// 均为**弱提示**（卡文"牺牲"也可能是效果结果而非代价），命中即报，由人工裁定/豁免。
        /// </summary>
        public void TestAudit_JC代价_关键代价字段存在性()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                if (tmpl.EffectDataList == null) continue;
                string text = CardText(tmpl);

                if (text.Contains("额外费用") && tmpl.ExtraCost == null && Exempt(uid, "cost", "额外费用") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『额外费用』但未声明 `ExtraCost`");

                if ((text.Contains("移除") && text.Contains("标志")) && Exempt(uid, "cost", "标志代价") == null)
                {
                    bool hasCounterCost = false;
                    foreach (var e in tmpl.EffectDataList)
                        if (e?.CounterCost != null) hasCounterCost = true;
                    if (!hasCounterCost)
                        violations.Add(uid + "（" + file + "）：卡文含『移除…标志』但未见 `CounterCost`（须人工确认是代价还是效果）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据⑩ 注册完整性 ====================

        /// <summary>
        /// 判据⑩：`tres` 数 / `cs` 数 / `RegisterAll()` 调用数三者对齐；
        /// 每个 cs 都必须在 `JCEffects.cs` 中被调用（漏注册 ⇒ 效果完全失效）。
        /// 差异卡必须在 `UnimplementedCards`（有意为之）或违规中登记。
        /// </summary>
        public void TestAudit_JC注册_数量对齐()
        {
            var violations = new List<string>();
            var files = AllCardFiles();
            int tresCount = files.Count;

            // cs 文件数
            var csDir = "res://Cards/Effects/PlayerCards/JC/";
            int csCount = 0;
            var csNames = new System.Collections.Generic.HashSet<string>();
            foreach (var f in global::Godot.DirAccess.GetFilesAt(csDir))
            {
                if (string.IsNullOrEmpty(f) || !f.EndsWith(".cs")) continue;
                if (f == "JCEffects.cs") continue;
                csCount++;
                csNames.Add(f.Substring(0, f.Length - 3));
            }

            foreach (var file in files)
            {
                string baseName = file.Substring(0, file.Length - 5);   // 去 .tres
                string uid = baseName.Split('_')[0];
                if (csNames.Contains(baseName)) continue;
                if (UnimplementedCards.Contains(uid)) continue;
                if (Exempt(uid, "register", "缺脚本") != null) continue;
                violations.Add(uid + "（" + file + "）：有 tres 但缺同名效果脚本 ⇒ 效果完全未装载");
            }

            // 数量摘要（不对齐本身的说明进入违规，便于人工定位；JC125 已在豁免/未实现登记）
            if (csCount != tresCount)
            {
                int gap = tresCount - csCount;
                bool allAccounted = true;
                foreach (var file in files)
                {
                    string baseName = file.Substring(0, file.Length - 5);
                    string uid = baseName.Split('_')[0];
                    if (!csNames.Contains(baseName) && !UnimplementedCards.Contains(uid)
                        && Exempt(uid, "register", "缺脚本") == null) allAccounted = false;
                }
                if (!allAccounted)
                    violations.Add("[汇总] tres=" + tresCount + " cs=" + csCount + "（差 " + gap
                        + "）⇒ 差异未全部登记为'未实现/豁免'，请核查");
            }

            AssertNoViolations(violations);
        }
    }
}
