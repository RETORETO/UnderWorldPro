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
            // —— 判据⑳ 结构卫生：动作由同卡的其它效果承担（声明式分工）——
        { "JC082|struct|空壳:JC082_B_KeepPledgeActivate", "代价型激活能力（横置＋支付2）：动作由同卡 `JC082_C_DrawOnInvestigationWon` 触发承担（用户口径：不再用 Pending 承诺），本条 Actions 为空是设计" },

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
            // 【2026-10 复核】㉕ 的"规则上确实不满足"例外（逐卡附规则依据）
            { "JC013|selftarget|默认允许自身", "发动**代价＝牺牲貘自身**（入堆叠前支付）⇒ 来源已离场，不可能以自身为目标" },
            { "JC028|selftarget|默认允许自身", "发动**代价＝牺牲鲜血奴仆自身**⇒ 来源已离场，不可能以自身为目标" },
            { "JC056|selftarget|默认允许自身", "目标大类是**暗藏者**，本卡是正面公开角色（触发时不是暗藏者）⇒ 自身不可能是候选" },
            { "JC061|selftarget|默认允许自身", "目标大类是**暗藏者**，本卡是正面角色（非暗藏者）⇒ 自身不可能是候选" },
            { "JC110|selftarget|默认允许自身", "卡文『将闲人的绿地**封印在目标角色上**』——规则上不能把自身封印在自身上（同 JC066 口径）" },
            { "JC121|selftarget|默认允许自身", "卡文『将空鸣御守**结附于**目标本方角色』——附属不能结附在自己身上（同 JC100 口径）" },

            // —— 判据㉑-a：确属"同段两句但必须拆条"的例外（写明机制依据）——
            { "JC062|segs|段数不等", "第二句『抓一张牌』与目标槽无关（FAQ：无目标也能打出、仅为抓牌）；若并入多槽效果，"
                + "动作会按**每个槽位目标各执行一次**（`ExecuteMassActions` 语义）⇒ 会抓多次牌。故保留两条同时间效果"
                + "（声明顺序＝结算顺序）" },

            // —— 判据⑪-b 动词映射：经核对"该短语由别的通道承担"的例外（2026-10 复核）——
            { "JC028|verb|Bounce", "卡文『移回**场上**』＝自身重新进场（`TransformForm{自身}` + `Attach`），不是回手（`Bounce`）" },
            { "JC051|verb|PlaceCounter", "卡文『放置**进场**并结附』是**检索去向**（`Search` 的『附加/去向』参数），非 `PlaceCounter`" },
            { "JC108|verb|Untap", "卡文『于重置步骤中不能被重置』是**规则描述**（`StaticKind.NoUntapWithSealedCards`），非 `Untap` 动作" },

            // —— 判据⑤ 目标语义限定词：经代码核对"该词并非目标限定"的例外（2026-10 复核）——
            { "JC009|semword|我方", "『本方』出现在『你的资产区/本方卡牌』处（条件与代价），非目标限定；目标＝角色" },
            { "JC012|semword|我方", "『本方』指减费受益方（本方暗藏者/本方牌），非目标限定；目标＝角色（横置）" },
            { "JC017|semword|敌方", "『敌方附属』指『结附在目标角色上的附属归属』，目标本身是『角色』（无敌方限定）" },
            { "JC023|semword|敌方", "『敌方暗藏者不参与势力对抗』是地区级静止规则（K21），非目标限定" },
            { "JC055|semword|我方", "『本方』出现在放标志/代价语句，非目标限定" },
            { "JC094|semword|其他", "『另一个』是**牺牲代价**限定（`SacrificeCost.ExcludeSource` 已实现），非消灭目标限定" },
            { "JC105|semword|我方", "『其他本方灵体角色』由**光环的同控制者集合**承担（`ReevaluateAuras` 只遍历来源控制者的卡、并排除自身），无需 `Restrictions=Friendly`" },
            { "JC108|semword|敌方", "『所有其上有牌封印的敌方角色不能被重置』是地区级静止规则（`StaticKind.NoUntapWithSealedCards`，内部按敌方筛选），非目标限定" },

            // —— 判据⑪ 卡名自指：名字作"效果主体/触发条件主语"，来源即本卡 ⇒ 无独立自指字段（2026-10 复核）——
            { "JC054|selfname|蒙古死亡蠕虫|无通道", "卡名是**效果主体**（『蒙古死亡蠕虫对所有位于本地区的其他角色造成1点伤害』），实现＝本卡的群体效果（来源即本卡）" },
            { "JC061|selfname|危机处理特派小组|无通道", "卡名出现在**触发条件**（『当危机处理特派小组进入一个地区时』），实现＝本卡的机动触发（来源即本卡）" },

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
            "DiedWithCounter", "EnemyPowerMarkRemovedThisTurn", "DiscardByEnemy", "RenownMajority", "SourceAbilityUsedThisTurn", "WinnerIsSelfAndAbilityUsed",
            "RenownMajorityInSameArea",
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

        /// <summary>
        /// 【2026-10 复核·校准】剥离卡文中的**圆括号补充说明**（"（将该角色翻面为暗藏者）"/"（检视你牌库顶的三张牌…）"等）。
        /// 括号内是**规则解释**，常含与动作无关的动词与词条（原判定照抄卡文 ⇒ JC104/JC119 的"检视"、
        /// JC014/JC075/JC088/JC097 的"暗藏者"都成了误报）。仅剥离中文全角括号。
        /// </summary>
        private static string StripParens(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            foreach (var ch in s)
            {
                if (ch == '（') { depth++; continue; }
                if (ch == '）') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string UidPrefix(PlayerCardData t, string file)
        {
            string uid = t?.UID;
            if (string.IsNullOrEmpty(uid)) uid = file;
            int i = uid.IndexOf('_');
            return i > 0 ? uid.Substring(0, i) : uid;
        }

        /// <summary>按 EffectId 取效果（自检用）。</summary>
        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
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
        /// <summary>核心前缀词（**不含 `：`**，长词在前）。修饰语（数字/，/横置/牺牲…）由 `FindPrefixHits` 的窗口规则容忍。</summary>
        private static readonly string[] CorePrefixes =
        {
            "准备步骤强制触发", "结束步骤强制触发", "准备步骤触发", "结束步骤触发",
            "潜伏强制触发", "现身强制触发", "进场强制触发", "死亡强制触发",
            "进场触发", "现身触发", "死亡触发",
            "快速暗藏者行动", "快速暗藏行动", "手牌行动", "墓地行动", "快速行动", "行动",
            "触发", "持续", "静止",
        };

        /// <summary>
        /// 【2026-10 复核·重写】前缀命中＝"`：` 左侧 10 字窗口内出现核心前缀词，且窗口内没有另一个 `：` 或句号"。
        /// 这样"快速行动2："/"手牌行动4："/"快速暗藏者行动2："/"快速行动，横置："/"快速行动，牺牲黑猫："
        /// 等**全部**变体都能识别（原实现只认前缀表里的完整串，另有一个**恒为 false** 的数字分支 ⇒ 段数偏少，
        /// 进而 ㉑-a/㉓ 误报）。
        /// </summary>
        private static List<(int pos, int len, string prefix)> FindPrefixHits(string text)
        {
            var hits = new List<(int pos, int len, string prefix)>();
            if (string.IsNullOrEmpty(text)) return hits;
            for (int k = 0; k < text.Length; k++)
            {
                if (text[k] != '：') continue;
                // 【2026-10 复核·校准】窗口上限放宽到 40 字（仍以句界 `。`／另一个 `：` 截断）：
                // JC012/JC025 的"快速行动1，横置，牺牲帷幕守望徽记："中间有 13 字修饰 ⇒ 原 10 字窗口够不到前缀词。
                int stop = System.Math.Max(0, k - 40);
                for (int s = k - 1; s >= stop; s--)
                {
                    char c = text[s];
                    if (c == '：' || c == '。' || c == '.' || c == '；' || c == ';') break;   // 越过句界 ⇒ 不是前缀
                    bool matched = false;
                    foreach (var core in CorePrefixes)          // 长词在前 ⇒ 先命中更长者
                    {
                        int start = s - core.Length + 1;
                        if (start < 0) continue;
                        if (string.CompareOrdinal(text, start, core, 0, core.Length) != 0) continue;
                        if (!hits.Exists(h => h.pos == start)) hits.Add((start, k - start + 1, core));
                        matched = true;
                        break;
                    }
                    if (matched) break;
                }
            }
            hits.Sort((a, b) => a.pos != b.pos ? a.pos.CompareTo(b.pos) : b.len.CompareTo(a.len));
            var cut = new List<(int pos, int len, string prefix)>();
            int lastEnd = -1;
            foreach (var h in hits)
            {
                if (h.pos < lastEnd) continue;      // 重叠命中：保留位置最前（同位置取长词）
                cut.Add(h);
                lastEnd = h.pos + h.len;
            }
            return cut;
        }

        /// <summary>按前缀词把卡文切段（先剥离 FAQ）。返回各段文本（不含前缀词本身）。</summary>
        private static List<string> SplitSegments(string text)
        {
            var hits = FindPrefixHits(text);
            var segs = new List<string>();
            for (int j = 0; j < hits.Count; j++)
            {
                int segStart = hits[j].pos + hits[j].len;
                int segEnd = (j + 1 < hits.Count) ? hits[j + 1].pos : text.Length;
                if (segEnd < segStart) continue;
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
                // 【2026-10 复核·校准】`MassSequential` 只对**群体**（`MassMode != Single`）有意义：
                // 单目标动作链的顺序由"动作数组声明顺序"天然保证（`ExecuteActions` 顺序执行），
                // 不需要该字段。原判定对一切含"然后/则"的卡都报警 ⇒ 20 条误报（JC130"抓两张然后弃一张"类）。
                bool cardHasMass = false;
                foreach (var e in tmpl.EffectDataList)
                    if (e?.Target != null && e.Target.MassMode != MassMode.Single) cardHasMass = true;

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
                if (textHasSequential && cardHasMass)
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

        // ==================== 判据㉑-a 段数 ↔ 效果条数（2026-10 复核补强）====================

        /// <summary>
        /// 前缀关键词本体条的**命名约定**（`<卡号>_<字母>_<英文词条名>`，见判据⑳命名规范与 `KeywordEffects.*`）。
        /// 这些条对应"前缀词条"，不是效果段 ⇒ ㉑-a 段数比对时排除。
        /// </summary>
        private static readonly string[] KeywordBodyTokens =
        {
            "Spirit", "Public", "Leader", "Renown", "WeiMing", "Maneuver", "Strike", "Kill",
            "Stealth", "Barrier", "Withdraw", "Marker", "NonAsset", "Cannot",
        };

        /// <summary>
        /// 判据㉑-a：**一个前缀段 ⇒ 一条 `EffectData`**（原用例只查"段内次序敏感"与"`MassSequential` 空转"，
        /// 段数↔条数自述"交由人工台账判定" ⇒ JC127 这类"一段拆两条"长期漏网）。
        /// 计数口径（可判定部分）：
        ///  · **期望**＝按前缀词切出的段数，但 `墓地行动：` 段不计（那是"从墓地正面打出"的**许可**，由
        ///    `PlayerCardData.PlayableFromGraveyard` 承担，不是效果段）；
        ///  · **实际**＝排除"关键词载体条"（`ContinuousGrants` 含 `Kind="Keyword"`）与
        ///    "声望规则本体条"（`Condition.Predicate == "RenownMajority"`，属前缀词条、不单独成段）后的效果条数。
        /// 两者不等即违规（除非有写明理由的豁免）。
        /// </summary>
        public void TestAudit_JC切分_段数与效果条数一致()
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
                var prefixes = FindSegmentPrefixes(text);
                if (segs.Count == 0) continue;

                int expected = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    string p = i < prefixes.Count ? prefixes[i] : "";
                    if (p.Contains("墓地行动")) continue;   // 打出许可，不是效果段
                    expected++;
                }

                // 【2026-10 复核·口径说明】判据㉑-a 的**可自动判定部分**＝"一段不得拆成多条**动作效果**"：
                //  · 只统计"有动作/模式的效果"（关键词载体、纯持续授予、静止规则条、声望规则本体条都不算
                //    "动作效果"，它们与"段"本就不是一一对应 —— 判据㉑ 明言段数可能＝段数＋关键词条数）；
                //  · 判据原文的完整口径下，`actual > expected` 即"把同一段拆成了多条同时间效果"
                //    （JC127 修复前就是这种：1 段 → 2 条动作效果）。
                int actual = 0;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    bool hasActions = (e.Actions != null && e.Actions.Count > 0)
                        || (e.Modes != null && e.Modes.Count > 0);
                    if (!hasActions) continue;
                    bool renownBody = e.Condition != null && e.Condition.Predicate == "RenownMajority";
                    if (renownBody) continue;
                    // 【2026-10 复核·口径】**前缀关键词本体条**（袭击X＝DealDamage、机动＝Move、威名＝PlaceToken、
                    // 杀伤＝属性授予…由 `KeywordEffects.*` 工厂产出）对应的是**前缀词条**，不是"效果段"
                    //（判据㉑ 明言：段数可能＝段数＋关键词条数）⇒ 按命名约定（`<卡号>_<字母>_<英文词条名>`）排除。
                    bool keywordBody = false;
                    foreach (var tok in KeywordBodyTokens)
                        if (!string.IsNullOrEmpty(e.EffectId) && e.EffectId.Contains(tok)) keywordBody = true;
                    if (keywordBody) continue;
                    actual++;
                }

                if (actual > expected && Exempt(uid, "segs", "段数不等") == null)
                    violations.Add(uid + "（" + file + "）：按前缀词切出 " + expected + " 个效果段，"
                        + "但实现了 " + actual + " 条**动作效果** ⇒ 判据㉑-a『一段一效果』不符"
                        + "（应合并为一条效果、动作按序执行）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 自检：扫描规模 / 关键事实 / 豁免理由（防空转与假绿）====================

        /// <summary>
        /// 【2026-10 复核新增·防空转】本套件的第一版曾在**编译未通过、产物过期**的状态下被误认为"全绿"
        /// （台账据此写了"零违规"）。本用例用三条可判定断言，防止再次出现"看似通过、实则没扫/没跑/放行无理由"：
        ///  ① **扫描规模**：JC 卡 ≥134 张、且 ≥134 张能取到非空 `EffectDataList`（加载/目录路径没坏）；
        ///  ② **关键事实**：若干"判据依赖的事实"必须成立（JC048/JC127 有 `MassSequential`、JC110 有 `HandZone`、
        ///     JC057 有 `AllowInspectLocalCovert`、JC100 有 `SealedZone`）——检查器若被写坏，这些断言会先失败；
        ///  ③ **豁免表**：条数 ≥20 且**每条理由非空**（判据 0.5：无理由豁免一律视为违规）。
        /// </summary>
        public void TestAudit_自检_扫描规模与豁免理由()
        {
            var files = AllCardFiles();
            Assert.IsTrue(files.Count >= 134, "JC 卡数应 ≥134（实际 " + files.Count + "）");

            int loaded = 0, effects = 0;
            foreach (var f in files)
            {
                var tmpl = LoadCard(f);
                if (tmpl == null) continue;
                loaded++;
                if (tmpl.EffectDataList != null && tmpl.EffectDataList.Count > 0) effects++;
            }
            Assert.IsTrue(loaded >= 134, "可加载 JC 模板应 ≥134（实际 " + loaded + "）");
            Assert.IsTrue(effects >= 134, "非空 `EffectDataList` 的 JC 卡应 ≥134（实际 " + effects + "）");

            // ② 关键事实
            var jc048 = FindEffect(LoadCard("JC048_zuzhoushalou.tres"), "JC048_1_PreparePlaceDamageSacrifice");
            Assert.IsNotNull(jc048, "JC048 应有教科书式单效果");
            Assert.IsTrue(jc048.Target.MassSequential, "JC048 必须带 `MassSequential`（㉑-b 范本）");
            var jc127 = FindEffect(LoadCard("JC127_zhongju.tres"), "JC127_1_ClearPowerMarksThenDestroyAll");
            Assert.IsNotNull(jc127, "JC127 应为单条效果（本轮修复）");
            Assert.IsTrue(jc127.Target.MassSequential, "JC127 必须带 `MassSequential`（㉑-b）");
            bool jc127ActionCond = false;
            if (jc127.Actions != null)
                foreach (var a in jc127.Actions)
                    if (a != null && a.RequirePredicate == "AreaMarkerAtLeast") jc127ActionCond = true;
            Assert.IsTrue(jc127ActionCond, "JC127 的『然后若…则…』必须是**动作级** `RequirePredicate`");
            var jc110 = LoadCard("JC110_xianrendelvdi.tres");
            bool jc110HandZone = false;
            foreach (var e in jc110.EffectDataList) if (e != null && e.Scope == ZoneScope.HandZone) jc110HandZone = true;
            Assert.IsTrue(jc110HandZone, "JC110『手牌行动4』必须有 `Scope=HandZone`");
            var jc057 = LoadCard("JC057_jiankongyuan.tres");
            bool jc057Static = false;
            foreach (var e in jc057.EffectDataList)
                if (e?.StaticEffects != null)
                    foreach (var st in e.StaticEffects)
                        if (st != null && st.Kind == StaticKind.AllowInspectLocalCovert) jc057Static = true;
            Assert.IsTrue(jc057Static, "JC057『随时检视暗藏者』必须走静态通道");
            var jc100 = LoadCard("JC100_jianyahuanxiang.tres");
            bool jc100Sealed = false;
            foreach (var e in jc100.EffectDataList) if (e != null && e.Scope == ZoneScope.SealedZone) jc100Sealed = true;
            Assert.IsTrue(jc100Sealed, "JC100『被封印时』必须有 `Scope=SealedZone`");

            // ③ 豁免理由非空
            Assert.IsTrue(Exemptions.Count >= 20, "豁免表条数应 ≥20（实际 " + Exemptions.Count + "）");
            foreach (var kv in Exemptions)
                Assert.IsTrue(!string.IsNullOrEmpty(kv.Value) && kv.Value.Length >= 8,
                    "豁免『" + kv.Key + "』必须写明理由（判据 0.5）");
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
                bool anyPassive = false, anyTrigger = false, anyActivated = false, anyReplacement = false;
                foreach (var e0 in tmpl.EffectDataList)
                {
                    if (e0 == null) continue;
                    if (e0.DamageRedirect != null || e0.TextBlank
                        || e0.IconSuppression != null || e0.DomainSuppression != null) anyReplacement = true;
                    // 【2026-10 复核·校准】K13"运行期授予激活能力"（JC019 镇魔枪 → 宿主获得"造成伤害"能力）
                    // 的卡文段在卡脚本里是 `GrantedAbility` 声明，不是本卡的 `CharacterActivated` 效果
                    // ⇒ 归入"激活能力"实现通道。
                    if (e0.GrantedAbility != null) anyActivated = true;
                }
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
                    // 【2026-10 复核·校准】事务卡（Sorcery）没有"发动能力"通道：卡文写 `行动：`/`快速行动：`
                    // 指的是"打出即结算"（`OnSorceryCast`），不应按"行动＝激活能力"判定
                    //（原判定对全部事务卡报警 ⇒ JC005/JC006/JC008/JC021/JC047/JC050/JC062/JC063/JC091/JC092… 共 15+ 条误报）。
                    if (expected == TimingGroup.Activated && (tmpl.CategoryFlag & CardCategory.Sorcery) != 0) continue;

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
                            // 【2026-10 复核·校准】**替代效应**（"该伤害改为对…造成/改为…"）在本引擎由
                            // `DamageRedirect` 常驻通道实现（JC077 庇佑圣灵 / JC121 空鸣御守），不是"触发类时机"，
                            // 属判据②认可的替代通道 ⇒ 该类段以"存在替代效应"为满足。
                            if (!segHasTrigger && !anyTrigger && !anyReplacement
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
                // 【2026-10 复核·校准】"墓地行动："是**从墓地正面打出**的许可（`PlayableFromGraveyard`），
                // 不是场上的"发动能力"⇒ 归入 Sorcery（跳过类别判定）。
                if (p.Contains("墓地行动")) return TimingGroup.Sorcery;
                if (p == "持续：" || p == "静止：") return TimingGroup.Passive;
                if (p.Contains("行动")) return TimingGroup.Activated;
            }
            // 兜底：按正文首句措辞
            if (seg.StartsWith("当") && seg.Contains("时")) return TimingGroup.Trigger;
            if (seg.Contains("持续") || seg.Contains("静止")) return TimingGroup.Passive;
            return TimingGroup.Sorcery;
        }

        /// <summary>顺序取出全文各段所对应的**前缀词**（与 <see cref="SplitSegments"/> 顺序一致）。</summary>
        /// <summary>顺序取出全文各段所对应的**核心前缀词**（与 <see cref="SplitSegments"/> 同源同序）。</summary>
        private static List<string> FindSegmentPrefixes(string text)
        {
            var found = new List<string>();
            foreach (var h in FindPrefixHits(text)) found.Add(h.prefix);
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
                    // 【2026-10 复核·校准】关键词载体（`ContinuousGrants` 含 `Kind="Keyword"`，如"公开/隐匿/灵体"）
                    // 按 `KeywordEffects` 的既定设计**在手牌区生效**（`KeywordEffects.cs` 注释：由 RegisterContinuous
                    // 按 HandZone 生效；"公开"必须在手牌期就能拦住秘密派遣）⇒ 判据③的"手牌行动"依据不适用。
                    bool kwCarrier = false;
                    if (e.ContinuousGrants != null)
                        foreach (var g in e.ContinuousGrants)
                            if (g != null && g.Kind == "Keyword") kwCarrier = true;
                    if (kwCarrier) continue;
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
                        case ZoneScope.SealedZone:
                            // 【2026-10 复核·校准】"被封印时触发"（JC100 尖牙幻象 / JC110 闲人的绿地）的
                            // 卡文明确写"当本卡被封印在一个角色上时"⇒ `Scope=SealedZone` 有卡文依据。
                            if (!text.Contains("封印") && Exempt(uid, "zone", e.EffectId) == null)
                                violations.Add(uid + "（" + file + "）：" + e.EffectId
                                    + " 声明 `Scope=SealedZone`（封印区触发）但卡文无『封印』依据");
                            continue;
                        case ZoneScope.Graveyard:
                        case ZoneScope.DeckZone:
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

                    // 【2026-10 复核·校准】原判定对 `DeckLevelRegistration` **无条件报警**（JC124 因此被误报）。
                    // 判据④要求的是"卡文写明『离场后仍生效/构筑级』" ⇒ 先查卡文依据，缺依据才报。
                    bool deckJustified = text.Contains("离场后") || text.Contains("仍然生效")
                        || text.Contains("构筑") || text.Contains("仍然有效");
                    if (e.DeckLevelRegistration && !deckJustified && Exempt(uid, "deck", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 声明 `DeckLevelRegistration`（构筑级全局触发）但卡文未写『离场后仍生效/构筑级』依据");
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
                string textForKinds = StripParens(text);
                foreach (var (word, kind) in TargetWordMap)
                    if (textForKinds.Contains(word) && !expectedKinds.Contains(kind)) expectedKinds.Add(kind);
                bool hasAnyKind = false, hasCovert = false;
                var declaredKinds = new List<TargetKind>();

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null) continue;
                    var t = e.Target;

                    bool hasAny = t.Kind != TargetKind.None || t.KindAlt != TargetKind.None || t.SpecAlt != null;
                    // 【2026-10 复核·校准】`EffectData.Target` 在本工程是**非 null 的默认对象**（`TargetSpec` 默认 `Kind=None`），
                    // 卡脚本普遍显式写 `{ Kind=None, NeedsSelection=false }` 表示"本效果不取目标"（合法写法）。
                    // 只有"要求玩家选目标却没写大类"才是不完整（原判定对全部无目标效果报警 ⇒ 约 120 条误报）。
                    // 【2026-10 复核·校准】无动作/无模式的效果（关键词载体、纯持续授予等）**不可能消费目标**：
                    // `KeywordEffects.*` 工厂默认 `NeedsSelection=true`（`TargetSpec` 默认值），但那不代表"要选目标"。
                    bool hasBody = (e.Actions != null && e.Actions.Count > 0) || (e.Modes != null && e.Modes.Count > 0);
                    // 【2026-10 复核·校准】多目标（K15）效果的目标声明在 `TargetSlots[].Spec` 里，
                    // `Target` 本身允许保持默认空对象（JC021 三槽 / JC062 两槽 / JC092 地区槽+墓地目标）。
                    bool hasSlots = e.TargetSlots != null && e.TargetSlots.Count > 0;
                    bool needsSel = hasBody && !hasSlots && t.NeedsSelection;
                    if (!hasAny && needsSel && Exempt(uid, "target", e.EffectId) == null)
                        violations.Add(uid + "（" + file + "）：" + e.EffectId
                            + " 要求选目标（`NeedsSelection`）但 `Kind=None` 且无 `KindAlt`/`SpecAlt` ⇒ 目标声明不完整");

                    // 多目标槽须与 MinChosenSlots 自洽（TargetSlots 是 EffectData 上的字段）
                    if (e.TargetSlots != null && e.TargetSlots.Count > 0)
                    {
                        int min = e.MinChosenSlots;
                        // 【2026-10 复核·校准】卡文写"至多/最多 N 个目标"时**允许一个都不选**（JC062 FAQ："无目标仍可抓牌"）
                        // ⇒ `MinChosenSlots=0` 合法；否则须在 1..槽数 之间。
                        bool allowZero = text.Contains("至多") || text.Contains("最多");
                        if (min < 0 || min > e.TargetSlots.Count || (min == 0 && !allowZero))
                            violations.Add(uid + "（" + file + "）：" + e.EffectId + " 多目标槽 " + e.TargetSlots.Count
                                + " 个但 `MinChosenSlots=" + min + "`（须在 1..槽数 之间；卡文写『至多/最多』时允许 0）");
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
                    // 【2026-10 复核·校准】本工程表达"角色或暗藏者"的**既定写法**是
                    // `Kind=Character + IncludeCovert=true`（JC003/JC050/JC069/JC094…）⇒ 应视为已声明暗藏者。
                    if (t.IncludeCovert) hasCovert = true;

                    // ㉕ 自身目标
                    if (t.Kind == TargetKind.Character || t.Kind == TargetKind.Covert
                        || t.KindAlt == TargetKind.Character || t.KindAlt == TargetKind.Covert)
                    {
                        anyCardTarget = true;
                        if (t.CanTargetSelf) anySelfAllowed = true;
                    }
                }

                // 【2026-10 复核·校准】卡文的目标词可能由**非 `Target.Kind` 的通道**承载：
                //  · "目标地区"：附属/事务由 `TargetRules(Kind=Area)` 或 `NeedsAreaTarget=true` 表达（JC047/JC048/JC118/JC132）；
                //  · "目标角色牌（墓地）"：由 `Kind=Graveyard + Filter="类别:角色"` 表达（JC086/JC092/JC133）。
                bool areaViaRulesOrNeeds = false, charViaZonePileFilter = false;
                if (tmpl.TargetRules != null)
                    foreach (var r in tmpl.TargetRules)
                        if (r != null && r.Kind == TargetKind.Area) areaViaRulesOrNeeds = true;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.NeedsAreaTarget) areaViaRulesOrNeeds = true;
                    if (e.Target != null && (e.Target.Kind == TargetKind.Graveyard || e.Target.Kind == TargetKind.Hand
                        || e.Target.Kind == TargetKind.Deck)
                        && !string.IsNullOrEmpty(e.Target.Filter) && e.Target.Filter.Contains("角色"))
                        charViaZonePileFilter = true;
                }

                // ⑤-3：卡文目标词 ⇒ 必须声明对应大类（无任何声明时 ⛔ 更明显）
                if (hasAnyKind && !exclSelf)
                    foreach (var want in expectedKinds)
                    {
                        if (declaredKinds.Contains(want)) continue;
                        if (want == TargetKind.Area && areaViaRulesOrNeeds) continue;
                        if (want == TargetKind.Character && charViaZonePileFilter) continue;
                        if (Exempt(uid, "targetkind", want.ToString()) != null) continue;
                        violations.Add(uid + "（" + file + "）：卡文含『" + WordOf(want)
                            + "』但未见声明对应 `TargetKind." + want + "`（目标类型与效果说明不一致）");
                    }

                // ⑤-3 复核：卡文写"目标暗藏者"，却完全没有声明 `Covert`
                // 【2026-10 复核·校准】"暗藏者"必须出现在**目标/群体语境**里才算"要求声明 Covert"：
                //  · 括号补充说明（隐匿定义、"（将该角色翻面为暗藏者）"）不算 ⇒ 先剥括号；
                //  · 规则性静止通道也算已承载（"随时检视本地区的任一暗藏者"＝`AllowInspectLocalCovert`；
                //    "所有本地区的敌方暗藏者不能参与势力对抗"＝`ExcludeCovertPower`）。
                string textPlain = StripParens(text);
                bool textMentionsCovertAsTarget = textPlain.Contains("目标暗藏者")
                    || textPlain.Contains("暗藏者为目标")
                    || (textPlain.Contains("所有") && textPlain.Contains("暗藏者"))
                    || (textPlain.Contains("每个") && textPlain.Contains("暗藏者"))
                    || textPlain.Contains("任一暗藏者")
                    || (textPlain.Contains("选择") && textPlain.Contains("暗藏者"));
                bool covertViaStatic = false;
                foreach (var e0 in tmpl.EffectDataList)
                {
                    if (e0?.StaticEffects == null) continue;
                    foreach (var st in e0.StaticEffects)
                        if (st != null && (st.Kind == StaticKind.AllowInspectLocalCovert
                            || st.Kind == StaticKind.ExcludeCovertPower)) covertViaStatic = true;
                }
                // 牺牲/代价类通道也可以承载"暗藏者"（JC065「目标对手牺牲一个位于该地区的**暗藏者**」）
                bool covertViaCostOrAction = hasCovert || covertViaStatic;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Actions == null) continue;
                    foreach (var a in e.Actions)
                    {
                        if (a?.Args == null) continue;
                        foreach (var arg in a.Args)
                            if (!string.IsNullOrEmpty(arg) && arg.Contains("暗藏者")) covertViaCostOrAction = true;
                    }
                }
                if (textMentionsCovertAsTarget && hasAnyKind && !covertViaCostOrAction
                    && Exempt(uid, "targetkind", "Covert") == null)
                    violations.Add(uid + "（" + file + "）：卡文提及『暗藏者』但目标未声明 `TargetKind.Covert`");

                // ㉕：卡文无排除措辞 ⇒ 角色/暗藏者目标必须允许取自身
                // 【2026-10 复核·校准】"自身"只有在**来源卡本身是角色**时才会落进角色候选池
                // （事务/附属/秘社来源的"自身"不是角色 ⇒ 不存在"取自身"问题；原判定对它们也报警 ⇒ 误报）。
                bool sourceIsCharacter = (tmpl.CategoryFlag & CardCategory.Character) != 0;
                if (sourceIsCharacter && anyCardTarget && !anySelfAllowed && !exclSelf
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
                    // 【2026-10 复核·校准】本地区也可由 `Restrictions` 里的 `TargetRestriction.LocalZone` 承载
                    //（JC121 的"位于本地区的目标本方角色"用 `Restrictions={Friendly, LocalZone}` 表达）。
                    if (t.Restrictions != null && t.Restrictions.Contains(TargetRestriction.LocalZone))
                        anyLocal = true;
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
                // 【2026-10 复核·校准】"本地区"也可能由**群体范围**或**牺牲代价的 LocalZone** 承载：
                //  · 群体（`MassMode.AllInArea`）的集合本身就是"该地区"（本地区或选定的目标地区）⇒ 无需 IsLocalZone；
                //  · "牺牲本地区的另一个角色"是**代价**限定 ⇒ `SacrificeCostSpec.LocalZone`（JC094）。
                bool anySacrificeLocal = false;
                foreach (var e in tmpl.EffectDataList)
                    if (e?.SacrificeCost != null && e.SacrificeCost.LocalZone) anySacrificeLocal = true;
                if (textLocal && !anyLocal && !anyMass && !anySacrificeLocal && anyCardTarget
                    && Exempt(uid, "filter", "本地区") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『本地区』，但角色/暗藏者目标未声明 `IsLocalZone`"
                        + "（群体/牺牲代价通道可承载，此处两者皆无）");
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

        // ==================== 判据⑮ 次数限制（一回合一次 + 威名/声望放置去重标签）====================

        /// <summary>
        /// 判据⑮ 次数限制（用户 2026-10-10 补充；2026-10 去重窗口统一后加固）：
        ///  · 卡文含"每回合只能发动一次/每回合限一次/每回合一次" ⇒ 期望 `MaxUsesPerTurn ≥ 1`；
        ///  · 卡文含"每局一次/整局" ⇒ 期望 `MaxUsesPerGame ≥ 1`；
        ///  · **放置去重的时机↔标签必须成对**：`OnCombatWon` 的 `PlaceToken` ⇒ `Args[2]=="威名"`；
        ///    `OnPowerCombatEnded` 的 `PlaceToken` ⇒ `Args[2]=="声望"`（两侧对称，缺一即违规）。
        /// 口径（与代码/行为用例一致）：威名＝**每次战斗对抗获胜**广播一次；声望＝**每次势力对抗结算完毕**广播一次；
        /// 每个广播窗口内同一控制者同一地区只放 1 枚（`BeginPlacementDedupWindow(标签)` **按标签分桶**清空，
        /// 两词条互不干扰）。卡面印的"所有对抗结束后/未横置"被用户 2026-09 第二轮口径覆盖。
        /// 弱判定：命中即报，由裁定/豁免处理。
        /// </summary>
        public void TestAudit_JC次数限制_与卡文一致()
        {
            var violations = new List<string>();
            int weiMingPlacements = 0, renownPlacements = 0;
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

                // 【2026-10·放置去重窗口统一】时机 ↔ 去重标签**必须成对**：
                //  · `OnCombatWon`（威名，每次战斗对抗获胜一次） ⇒ `Args[2]==PlacementDedupTags.WeiMing`；
                //  · `OnPowerCombatEnded`（声望，每次势力对抗结算完毕一次） ⇒ `PlacementDedupTags.Renown`。
                // 缺标签 ⇒ `EffectEngine.PlaceToken` 该条不去重 ⇒ **一个地区内多张同类角色各放一枚势力标志**（实测 BUG1）。
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Actions == null) continue;
                    string expectTag = null;
                    if (e.Timing == EffectTiming.OnCombatWon) expectTag = PlacementDedupTags.WeiMing;
                    else if (e.Timing == EffectTiming.OnPowerCombatEnded) expectTag = PlacementDedupTags.Renown;
                    if (expectTag == null) continue;

                    foreach (var a in e.Actions)
                    {
                        if (a?.Verb != ActionVerb.PlaceToken) continue;
                        if (expectTag == PlacementDedupTags.WeiMing) weiMingPlacements++;
                        else renownPlacements++;

                        string tag = (a.Args != null && a.Args.Count > 2) ? a.Args[2] : "";
                        if (tag != expectTag)
                            violations.Add(uid + "（" + file + "）：判据⑮放置去重｜" + e.Timing
                                + " 的 `PlaceToken` 去重标签应为『" + expectTag + "』（当前 '" + tag + "'）"
                                + "⇒ 缺标签则一个地区内多张同类角色会**各放一枚**势力标志");
                    }
                }
            }
            // 防空转：两侧事实都必须真扫到（否则断言等于没跑）
            if (weiMingPlacements == 0)
                violations.Add("[自检] 未扫到任何『威名放置条』（`OnCombatWon` + `PlaceToken`，期望 ≥1）——扫描路径可能已坏");
            if (renownPlacements == 0)
                violations.Add("[自检] 未扫到任何『声望放置条』（`OnPowerCombatEnded` + `PlaceToken`，期望 ≥1）——扫描路径可能已坏");
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

                // 【2026-10 复核·校准】判据⑪的自动可判定部分＝"卡文出现本卡名时，实现是否存在**自指通道**"。
                // 原判定对 45 张卡一律报警（"仅登记"却计入失败）⇒ 假绿/假红都不可信。现做通道存在性弱校验：
                // 动作对象含 `自身/触发卡/结附宿主/封印宿主`、条件 `TriggerBySelf`、`CanTargetSelf`、
                // 激活条件含 `本角色/本卡`、向宿主/自身的持续授予（`GrantToHost`/`TextBlank`/`DomainSuppression`/`DamageRedirect`）。
                bool hasSelfChannel = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.Condition != null && e.Condition.Predicate == "TriggerBySelf") hasSelfChannel = true;
                    if (e.Target != null && e.Target.CanTargetSelf) hasSelfChannel = true;
                    if (!string.IsNullOrEmpty(e.ActivateCondition)
                        && (e.ActivateCondition.Contains("本角色") || e.ActivateCondition.Contains("本卡")))
                        hasSelfChannel = true;
                    if (e.GrantToHost || e.TextBlank || e.DomainSuppression != null || e.DamageRedirect != null)
                        hasSelfChannel = true;
                    // 【2026-10 复核·校准】补齐本工程实际使用的自指通道：
                    //  · 非光环的持续授予且未 `GrantToHost` ⇒ 授予**来源卡自身**（JC001"通灵猫头鹰获得白眼"、
                    //    JC016/JC022/JC037/JC054/JC081/JC084/JC085 的持续授予）；
                    //  · 静止规则（`StaticEffects`）以来源/其所在地区为作用对象（JC057/JC098）；
                    //  · 自指动词：`PayOrSacrificeSelf`（JC064"否则牺牲戒严"）、`SacrificeSelfOptional`（JC114）、
                    //    `PrepareUpkeep`（JC068"支付1否则横置阿格斯IV"）、`TransformForm`（JC028 自身形态）、
                    //    `RevealSealedAndQueueFreePlay`（JC119 自身封印牌）；
                    //  · `WinAreaDestination`（JC079"将圣婴置入你的计分区"＝自身去处替代）。
                    if (!e.IsAura && e.ContinuousGrants != null && e.ContinuousGrants.Count > 0) hasSelfChannel = true;
                    if (e.StaticEffects != null && e.StaticEffects.Count > 0) hasSelfChannel = true;
                    if (!string.IsNullOrEmpty(e.WinAreaDestination)) hasSelfChannel = true;
                    if (e.Actions == null) continue;
                    foreach (var a in e.Actions)
                    {
                        if (a?.Args == null) continue;
                        if (a.Verb == ActionVerb.PayOrSacrificeSelf || a.Verb == ActionVerb.SacrificeSelfOptional
                            || a.Verb == ActionVerb.PrepareUpkeep || a.Verb == ActionVerb.TransformForm
                            || a.Verb == ActionVerb.RevealSealedAndQueueFreePlay) hasSelfChannel = true;
                        foreach (var arg in a.Args)
                            if (!string.IsNullOrEmpty(arg) && (arg == "自身" || arg.Contains("触发卡")
                                || arg.Contains("结附宿主") || arg.Contains("封印宿主"))) hasSelfChannel = true;
                    }
                }
                if (!hasSelfChannel && Exempt(uid, "selfname", name + "|无通道") == null)
                    violations.Add(uid + "（" + file + "）：卡文出现与本卡同名的『" + name
                        + "』，但全卡**未见任何自指通道**（自身/触发卡/结附宿主/TriggerBySelf/CanTargetSelf/本角色）"
                        + " ⇒ 需人工确认实现未把它当作别处的同名卡");
                else
                    global::Godot.GD.Print($"[JC 自查·登记] {uid} 卡文出现本卡名『{name}』：已检出自指通道（人工复核仍建议）");
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
        /// 【2026-10 复核·校准】原表把"横置/牺牲/支付/移除/放置一个/伤害/抓/弃"等**高频代价与条件词**
        /// 也当作动作要求 ⇒ 92 条误报（这些词大量出现在 `Cost`/`SacrificeCost`/`CounterCost`/`ExcludeSource`、
        /// 触发条件（"每当你抓牌"）与 `Search` 自带洗牌里）。现收窄为**高置信映射**：
        /// 只保留"卡文这个动作一定由某个动作动词实现"的短语，并允许该动作的**等价动词集合**
        /// （如"消灭"可由 `Destroy`/`DestroyOnTarget`/`DestroyWithMinCounters` 实现；"洗牌"由 `Search` 顺带完成）。
        private static readonly (string Phrase, ActionVerb[] Verbs)[] PhraseVerbMap =
        {
            ("造成", new[] { ActionVerb.DealDamage }),
            ("消灭", new[] { ActionVerb.Destroy, ActionVerb.DestroyOnTarget, ActionVerb.DestroyWithMinCounters }),
            ("封印", new[] { ActionVerb.Seal }),
            ("检索", new[] { ActionVerb.Search }),
            ("寻找", new[] { ActionVerb.Search }),
            ("洗牌", new[] { ActionVerb.Shuffle, ActionVerb.Search }),      // `Search` 内部自动洗牌
            ("预测", new[] { ActionVerb.Predict }),
            ("检视", new[] { ActionVerb.Inspect }),
            ("抓一张", new[] { ActionVerb.Draw, ActionVerb.DrawFromBottom }),
            ("抓两张", new[] { ActionVerb.Draw, ActionVerb.DrawFromBottom }),
            ("移回其拥有者手上", new[] { ActionVerb.Bounce, ActionVerb.MoveToZone,
                                          ActionVerb.RevealSealedAndQueueFreePlay }),   // JC119：展示封印牌后回手
            ("潜伏", new[] { ActionVerb.Latent, ActionVerb.TransformForm }),
            ("重置", new[] { ActionVerb.Untap }),
            ("放置", new[] { ActionVerb.PlaceCounter, ActionVerb.PlaceToken,
                              ActionVerb.PlaceHandCardToArea, ActionVerb.UnsealToArea }),
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

                // 【2026-10 复核·校准】① 剥括号（"（检视你牌库顶的三张牌…）"是 `Predict` 的解释，不是 Inspect 动作）；
                // ② 动作集合必须含 `Modes[].Actions`（JC110"横置或重置"落在模式动作里）。
                string text = StripParens(CardText(tmpl));
                var verbs = new System.Collections.Generic.HashSet<ActionVerb>();
                bool hasDamageRedirect = false, sealViaDestroyOnTarget = false, sealViaSearch = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    if (e.DamageRedirect != null) hasDamageRedirect = true;
                    if (e.Actions != null)
                        foreach (var a in e.Actions)
                        {
                            if (a == null) continue;
                            verbs.Add(a.Verb);
                            if (a.Verb == ActionVerb.DestroyOnTarget && a.Args != null && a.Args.Contains("封印牌"))
                                sealViaDestroyOnTarget = true;
                            if (a.Verb == ActionVerb.Search && a.Args != null)
                                foreach (var arg in a.Args)
                                    if (!string.IsNullOrEmpty(arg) && arg.Contains("封印")) sealViaSearch = true;
                        }
                    if (e.Modes != null)
                        foreach (var m in e.Modes)
                        {
                            if (m?.Actions == null) continue;
                            foreach (var a in m.Actions) if (a != null) verbs.Add(a.Verb);
                        }
                }
                if (verbs.Count == 0 && !hasDamageRedirect) continue;   // 纯关键字/持续卡不判

                foreach (var (phrase, expectVerbs) in PhraseVerbMap)
                {
                    if (!text.Contains(phrase)) continue;
                    bool hit = false;
                    foreach (var v in expectVerbs) if (verbs.Contains(v)) hit = true;
                    // 【2026-10 校准】等价通道：
                    //  · "封印"可由 `DestroyOnTarget{"封印牌"}`（JC013 消灭封印牌）或 `Search{…,"封印"}`（JC109 检索即封印）承担；
                    //  · "造成"可由 `DamageRedirect`（"该伤害改为对…造成"＝替代效应，JC121）承担。
                    if (phrase == "封印" && (sealViaDestroyOnTarget || sealViaSearch)) hit = true;
                    if (phrase == "造成" && hasDamageRedirect) hit = true;
                    if (hit) continue;
                    if (Exempt(uid, "verb", expectVerbs[0].ToString()) != null) continue;
                    violations.Add(uid + "（" + file + "）：卡文含『" + phrase + "』⇒ 期望动作之一 "
                        + string.Join("/", System.Array.ConvertAll(expectVerbs, v => "`" + v + "`"))
                        + "，但全卡动作中均未见（卡文↔实现语义不一致，须裁定）");
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
                    // 【2026-10 复核·校准】"一次性待触发承诺"（`Pending`，JC082 收藏模式：先登记承诺、
                    // 后续 `OnInvestigationWon` 效果才动作）与"运行期授予能力"（`GrantedAbility`）都属于
                    // **声明式内容**，动作可以为空 ⇒ 不算空壳（原判定把 JC082 误报为空壳）。
                    bool declarative = e.Pending != null || e.GrantedAbility != null;
                    // 事务结算/触发类效果必须有动作；纯持续/静止可无动作（走 ContinuousGrants 等）
                    if (!purePassive && !declarative && emptyActions && emptyModes
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

                // 【2026-10 复核·校准】"移除…标志"大多数是**效果**（`RemoveToken` 等），不是入堆叠前的代价；
                // 用户 2026-10-10 口径只要求"卡文写『作为代价』⇒ 必须是入堆叠前支付的代价字段"。
                // 故只对"作为代价"措辞报警；其余"移除标志"的卡**登记**（打印）供人工核对，不计违规。
                bool hasCounterCost = false;
                foreach (var e in tmpl.EffectDataList)
                    if (e?.CounterCost != null) hasCounterCost = true;
                if (text.Contains("作为代价") && (text.Contains("移除") || text.Contains("牺牲"))
                    && tmpl.ExtraCost == null && !hasCounterCost && Exempt(uid, "cost", "标志代价") == null)
                {
                    bool anySacCost = false;
                    foreach (var e in tmpl.EffectDataList)
                        if (e?.SacrificeCost != null) anySacCost = true;
                    if (!anySacCost)
                        violations.Add(uid + "（" + file + "）：卡文写『作为代价』但未见 `CounterCost`/`SacrificeCost`/`ExtraCost`"
                            + "（判据⑭：代价必须入堆叠前支付）");
                }
                else if (text.Contains("移除") && text.Contains("标志") && !hasCounterCost)
                    global::Godot.GD.Print($"[JC 自查·登记] {uid} 卡文含『移除…标志』但无 `CounterCost`："
                        + "按效果（`RemoveToken`）还是代价，需人工确认");
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
        // ==================== 回归防线：2026-10 实测 BUG 批（避免同类再漏） ====================

        /// <summary>
        /// 【2026-10 实测 BUG 批的回归防线】把"上一批核查漏掉"的两类事实变成**可自动判定**的断言：
        ///  ① **"选择一项的打出"（`PlayChoice`）**：必须声明 ≥2 条 `TargetRules`（选项＝各目标类别），
        ///     否则打出时推不出"一项"（JC082）；
        ///  ② **`玩家选择` 型动态值**：必须显式声明范围且下限＝0（A31：0 合法，可猜"印刷费用为 0"），
        ///     否则拿到默认 1..9（既漏"选 0"又漏"≤20"）。
        /// 这两条都是"卡面/数据层能判定、但此前没有断言"的事实。
        /// 注：原 ①"声望去重标签"已升级为**时机↔标签对称断言**并归位到判据⑮
        /// （`TestAudit_JC次数限制_与卡文一致`：`OnCombatWon`⇒`威名`、`OnPowerCombatEnded`⇒`声望`），
        /// 不再在本方法内重复。
        /// </summary>
        public void TestAudit_本批BUG回归防线()
        {
            var violations = new List<string>();
            int playChoiceCards = 0, playerChoiceEffects = 0;

            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl?.EffectDataList == null) continue;
                string uid = tmpl.UID ?? file;

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;

                    // ③ 玩家选择型动态值：必须显式 ValueMin=0（默认 1..9 是错的）
                    if (e.ValueExpr == "玩家选择")
                    {
                        playerChoiceEffects++;
                        if (e.ValueMin != 0)
                            violations.Add($"{uid}｜⑱玩家选择｜`ValueExpr=玩家选择` 未声明 ValueMin=0（当前 {e.ValueMin}）"
                                           + "⇒ 拿到默认 1..9，既漏『选 0』又漏上限");
                        if (e.ValueMax <= 0)
                            violations.Add($"{uid}｜⑱玩家选择｜ValueMax={e.ValueMax} 非法（应声明可选上限，如 20）");
                    }
                }

                // ② PlayChoice：选项来自多个目标类别
                if (tmpl.PlayChoice)
                {
                    playChoiceCards++;
                    int kinds = 0;
                    var seen = new System.Collections.Generic.HashSet<TargetKind>();
                    if (tmpl.TargetRules != null)
                        foreach (var r in tmpl.TargetRules)
                            if (r != null && r.Kind != TargetKind.None && seen.Add(r.Kind)) kinds++;
                    if (kinds < 2)
                        violations.Add($"{uid}｜㉑-c选择一项的打出｜声明了 PlayChoice（打出时选择一项）"
                                       + $"但 TargetRules 只有 {kinds} 个不同目标类别 ⇒ 选项不足 2 项");
                }
            }

            // 防空转：两类事实都必须真扫到（否则断言等于没跑）
            if (playChoiceCards == 0) violations.Add("[自检] 未扫到任何 PlayChoice 卡（期望 ≥1，JC082）");
            if (playerChoiceEffects == 0) violations.Add("[自检] 未扫到任何『玩家选择』型效果（期望 ≥1，JC083）");

            AssertNoViolations(violations);
        }

        // ==================== 【判据㉖/㉗/㉘/㉙】承诺落地清单 + 判据覆盖矩阵 ====================

        /// <summary>承诺清单一条：`Name` 承诺名 → `File` 消费点所在源码 → `Needle` 必须出现的关键字
        /// （`MinCount` 次）；`BehaviorTest`＝覆盖该承诺的**行为用例名**（必须真实存在）。</summary>
        private readonly struct Promise(string name, string file, string needle, int minCount, string behaviorTest)
        {
            public readonly string Name = name;
            public readonly string File = file;
            public readonly string Needle = needle;
            public readonly int MinCount = minCount;
            public readonly string BehaviorTest = behaviorTest;
        }

        /// <summary>
        /// 【判据㉖ 承诺必须落地】把"声明侧看起来启用了某行为"的承诺，逐个钉到**引擎消费点**上：
        ///  · 数据层：`OnlyAttachWithSubCategories` 自洽、光环范围字段 ↔ 卡文词、`玩家选择` 范围、
        ///    声望去重标签、`PlayChoice` 选项数（后三条见 `TestAudit_本批BUG回归防线`）；
        ///  · 源码层：每条承诺的消费点关键字必须存在（`Godot.FileAccess` 读 `res://` 源码；先例 `DeckListLoader`），
        ///    且必须有**真实存在的行为用例**（反射校验）。
        /// 这条检查直接对治"注释/参数名承诺了行为、实现却没做"这一类静默失效（BUG1 声望去重、JC073 屏障…）。
        /// </summary>
        public void TestAudit_承诺落地清单()
        {
            var violations = new List<string>();

            // ---- 数据层 ----
            int whitelistCards = 0, auraLocalCards = 0, auraNoDomainCards = 0;
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl?.EffectDataList == null) continue;
                string uid = UidPrefix(tmpl, file);
                string text = CardText(tmpl);

                foreach (var e in tmpl.EffectDataList)
                {
                    if (e == null) continue;
                    // ⑰ 光环范围：声明"限本地区"必须卡文有"本地区"；声明"未印刷领域"必须卡文有"未印刷"
                    if (e.IsAura)
                    {
                        if (e.AuraLocalZoneOnly)
                        {
                            auraLocalCards++;
                            if (!text.Contains("本地区"))
                                violations.Add($"{uid}｜⑰光环范围｜`AuraLocalZoneOnly=true` 但卡文无『本地区』"
                                               + "⇒ 范围声明与卡文不一致（应限本地区却按全场实现或反之）");
                        }
                        if (e.AuraRequiresNoDomain)
                        {
                            auraNoDomainCards++;
                            if (!text.Contains("未印刷"))
                                violations.Add($"{uid}｜⑰光环筛选｜`AuraRequiresNoDomain=true` 但卡文无『未印刷』");
                        }
                    }
                }

                if (tmpl.EffectDataList == null) continue;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.StaticEffects == null) continue;
                    foreach (var s in e.StaticEffects)
                    {
                        if (s?.Kind != StaticKind.OnlyAttachWithSubCategories) continue;
                        whitelistCards++;
                        // 白名单必须给出**有效子类别**，且**不能自己就是附属**（否则把本卡锁死）
                        bool hasEntry = false;
                        foreach (var raw in (s.Param ?? "").Split(','))
                            if (raw.Trim().Length > 0) hasEntry = true;
                        if (!hasEntry)
                            violations.Add($"{uid}｜⑤/⑯附属白名单｜`OnlyAttachWithSubCategories` 的 `Param` 为空/无有效子类别"
                                           + "⇒ 规则失效或语义不明");
                        if ((tmpl.CategoryFlag & CardCategory.Attachment) != 0)
                            violations.Add($"{uid}｜⑤/⑯附属白名单｜本卡**自身是附属**却声明『只受 X 类附属结附』"
                                           + "⇒ 自锁（附属不可能结附到附属上）");
                    }
                }
            }
            if (whitelistCards == 0) violations.Add("[自检] 未扫到任何『附属白名单』卡（期望 ≥1，JC081）");
            if (auraLocalCards == 0) violations.Add("[自检] 未扫到任何『限本地区』光环（期望 ≥1，JC059）");
            if (auraNoDomainCards == 0) violations.Add("[自检] 未扫到任何『未印刷领域』光环（期望 ≥1，JC060）");

            // ---- 源码层：承诺 → 消费点 + 行为用例 ----
            var promises = new[]
            {
                // 【2026-10·放置去重窗口统一】两词条**同形**：广播点按标签开启窗口（只清本桶）+ 引擎按标签查桶。
                new Promise("放置去重窗口·威名（广播点开启）", "Script/Manager/TriggerEmitPoints.cs",
                    "BeginPlacementDedupWindow(PlacementDedupTags.WeiMing)", 1,
                    "TestContract_去重窗口_威名_多张一次战斗只放一枚"),
                new Promise("放置去重窗口·声望（广播点开启）", "Script/Manager/TriggerEmitPoints.cs",
                    "BeginPlacementDedupWindow(PlacementDedupTags.Renown)", 1,
                    "TestBug1_多个声望角色一次势力对抗只放一个标志"),
                new Promise("放置去重按标签分桶（无白名单）", "Script/Manager/EffectEngine.cs",
                    "_placementDedupByTag.TryGetValue(dedupTag", 1,
                    "TestContract_去重窗口_威名与声望同窗不互吞"),
                new Promise("去重标签单一真相源·威名", "Script/Data/Effects/KeywordEffects.cs",
                    "\"势力标志\", \"1\", PlacementDedupTags.WeiMing", 1,
                    "TestContract_去重窗口_威名_多张一次战斗只放一枚"),
                new Promise("去重标签单一真相源·声望", "Script/Data/Effects/KeywordEffects.cs",
                    "\"势力标志\", \"1\", PlacementDedupTags.Renown", 1,
                    "TestBug1_多个声望角色一次势力对抗只放一个标志"),
                new Promise("声望＝地区级（来源须在该对抗地区内）", "Script/Manager/EffectEngine.cs", "RenownMajorityInSameArea", 1,
                    "TestBug3c_声望只由本地区角色触发"),
                new Promise("附属白名单只约束附属", "Script/Manager/EffectEngine.cs",
                    "EffectiveCategoryFlag & CardCategory.Attachment", 1, "TestBug2_JC078可以以JC081为目标"),
                new Promise("多声明目标 OR 复检（D16）", "Script/Manager/TurnManager.cs", "PassesAnyDeclaredTargetDesc", 1,
                    "TestBug3_JC082可结附于本方秘社"),
                new Promise("争夺区候选同过限制判定（含屏障）", "Script/Manager/TurnManager.cs",
                    "PassesAreaTargetRestrictions", 3, "TestBug5_屏障宿主不出现在袭击候选"),
                new Promise("结附落地单点（ZoneManager）", "Script/Manager/ZoneManager.cs",
                    "public void AttachAndRegister(", 1, "TestContract_落地路径一致_生产与PM命令"),
                new Promise("生产结附走单点", "Script/Manager/TurnManager.cs", "AttachAndRegister(card,", 2,
                    "TestContract_落地路径一致_生产与PM命令"),
                new Promise("PM 命令结附走单点", "Script/Manager/PMCommandManager.cs", "AttachAndRegister(", 2,
                    "TestBug5c_PM命令attach后宿主获得屏障"),
                new Promise("交互收尾单点（数值窗回包后必收尾）", "Script/Manager/EffectEngine.cs",
                    "SettleAfterValuePrompt", 3, "TestContract_交互收尾_数值窗单机与联机"),
                new Promise("换局清挂起链（防残留吞收尾）", "Script/Manager/EffectEngine.cs", "_pendingChain = null;", 1,
                    "TestContract_交互收尾_数值窗单机与联机"),
                new Promise("PlayChoice 合成（单一实现）", "Script/Manager/MultiTargetSlots.cs", "PlayChoiceEffect", 1,
                    "TestContract_联机孪生入口_PlayChoice"),
                new Promise("PlayChoice 联机服务端入口", "Script/Manager/PlayerActionService.cs", "PlayChoiceEffect", 1,
                    "TestContract_联机孪生入口_PlayChoice"),
                new Promise("PlayChoice 联机客户端分支", "Script/UI/ArenaOl.cs", "PlayChoice", 1,
                    "TestContract_联机孪生入口_PlayChoice"),
                new Promise("PlayChoice 单机拖拽分支", "Script/UI/Interaction/CardDragController.cs", "PlayChoice", 1,
                    "TestBug3b_JC082选择一项的打出流程"),

                // 【2026-10 实测反馈四批：联机优先，单机同办】
                // ①/② 未横置才有词条（威名/声望）：门禁声明 + 两处收集循环 + 声望计数排除横置者
                new Promise("未横置门禁·声明与消费（卡面固有触发）", "Script/Manager/TriggerRegistry.cs",
                    "data.RequireSourceUntapped && card.IsTapped", 1,
                    "TestB2_1A_威名_横置不触发_未横置触发"),
                new Promise("未横置门禁·运行期授予关键词同一门禁", "Script/Manager/TriggerRegistry.cs",
                    "template.RequireSourceUntapped && card.IsTapped", 1,
                    "TestB2_2B_JC089授予的威名_宿主横置不触发"),
                new Promise("未横置门禁·关键词模板声明", "Script/Data/Effects/KeywordEffects.cs",
                    "RequireSourceUntapped = true", 2, "TestB2_1A_威名_横置不触发_未横置触发"),
                new Promise("声望计数排除横置者", "Script/Manager/RenownRule.cs",
                    "if (c.IsTapped) continue;", 1, "TestB2_2_声望_横置不计入数量且不触发"),
                // ④ 减防 ⇒ 补死亡判定（持续授予路径也在收口内）
                new Promise("减防后补死亡判定（含持续授予路径）", "Script/Manager/EffectEngine.cs",
                    "RecheckDeathsAfterDefenseDrop();", 2, "TestB2_4A_JC089减防致死_防御1未受伤"),
                // ⑤ 选择窗提示文字必须来自数据（不再硬编码"将移动至该地区"）
                new Promise("提示文字数据源（槽 Hint）", "Script/Data/Effects/EffectData.cs",
                    "public string Hint", 1, "TestB2_5_JC092选地区提示为卡文口径"),
                new Promise("提示文字·候选带上槽 Hint", "Script/Manager/MultiTargetSlots.cs",
                    "Hint = slot?.Hint", 1, "TestB2_5_JC092选地区提示为卡文口径"),
                new Promise("提示文字·联机随请求下发", "Script/Manager/TurnManager.cs",
                    "SlotHint = cand.Hint", 1, "TestB2_5_JC092选地区提示为卡文口径"),
                new Promise("提示文字·联机按数据展示", "Script/UI/ArenaOl.cs",
                    "req.SlotHint", 1, "TestB2_5_JC092选地区提示为卡文口径"),
                // ⑥ 次数用尽：同一份判定（候选/置灰/直发）+ 两处 UI 全部用尽即无反馈
                new Promise("次数用尽判定（单一实现）", "Script/Manager/ActivateEligibility.cs",
                    "IsAbilityUsesExhausted", 1, "TestB2_6_JC093次数用尽后不可发动"),
                new Promise("次数用尽·单机静默不开面板", "Script/UI/Arena.cs",
                    "AllAbilitiesUsesExhausted", 1, "TestB2_6_JC093次数用尽后不可发动"),
                new Promise("次数用尽·联机静默不开面板", "Script/UI/ArenaOl.cs",
                    "AllAbilitiesUsesExhausted", 1, "TestB2_6_JC093次数用尽后不可发动"),
                // ③ JC085 墓地行动双击通道（UI 接线；引擎落点由行为用例锁）
                new Promise("墓地行动·双击信号", "Script/UI/CardUI.cs",
                    "GraveyardPlayRequested", 1, "TestB2_3_JC085_墓地行动按所选地区落地"),
                new Promise("墓地行动·单机转发订阅", "Script/UI/Arena.Layout.cs",
                    "GraveyardPlayRequested +=", 1, "TestB2_3_JC085_墓地行动按所选地区落地"),
                new Promise("墓地行动·单机处理（选地区后打出）", "Script/UI/Arena.cs",
                    "private void OnGraveyardPlayRequested", 1, "TestB2_3_JC085_墓地行动按所选地区落地"),
                new Promise("墓地行动·联机处理（选地区后上行）", "Script/UI/ArenaOl.cs",
                    "private void OnGraveyardPlayRequested", 1, "TestB2_3_JC085_墓地行动按所选地区落地"),
                // 【2026-10 二轮】用户口径：墓地中的卡要**高亮**，且双击必须**先弹技能选择窗**（只有一条也弹）
                new Promise("墓地行动·门禁单一实现（高亮与双击同源）", "Script/Manager/TurnManager.cs",
                    "public bool CanPlayFromGraveyardNow(", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·单机高亮", "Script/UI/Arena.cs",
                    "CanPlayFromGraveyardNow(inst, out _)", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·联机高亮", "Script/UI/ArenaOl.cs",
                    "turn.CanPlayFromGraveyardNow(inst, out _)", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·单机技能选择窗", "Script/UI/Arena.cs",
                    "墓地行动：从墓地正面打出", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·联机技能选择窗", "Script/UI/ArenaOl.cs",
                    "墓地行动：从墓地正面打出", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·单机选地区（第二级）", "Script/UI/Arena.cs",
                    "private void OpenGraveyardAreaChoice", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                new Promise("墓地行动·联机选地区（第二级）", "Script/UI/ArenaOl.cs",
                    "private void OpenOnlineGraveyardAreaChoice", 1, "TestB2_3B_墓地行动门禁_高亮与双击同源"),
                // ④ 上限削减 ⇒ 吸收已受伤害（"剩余生命"口径）
                new Promise("上限削减吸收已受伤害（单一实现）", "Script/Game/Entities/CardInstance.cs",
                    "上限 -{cut} 吸收已受伤害", 1, "TestB2_4B_JC089减防吸收已受伤害_不误杀"),

                // 【2026-10 实测反馈五批（单机复现；联机同源缺陷一并修）】
                // ① JC098："略过对抗步骤"＝该步骤**在流程里直接不存在**（阶段推进时选"第一个未被跳过的对抗步骤"）
                new Promise("略过步骤·流程层面跳步（单一实现）", "Script/Manager/TurnManager.cs",
                    "private TurnPhase NextCombatStepOfArea(int fromStep)", 1,
                    "TestB3_1A_JC098在1地区_机动轮后直接进1地区战斗步骤"),
                new Promise("略过步骤·机动轮后／换地区后按判定取下一步骤", "Script/Manager/TurnManager.cs",
                    "nextPhase = NextCombatStepOfArea(0);", 2,
                    "TestB3_1B_JC098在2地区_1地区势力后直接进2地区战斗步骤"),
                new Promise("略过步骤·地区内逐步骤筛选", "Script/Manager/TurnManager.cs",
                    "nextPhase = NextCombatStepOfArea(1);", 1,
                    "TestB3_1C_只略过战斗步骤_调查照常势力直接接续"),
                // ② 减防类效果＝**发动能力时付费**（入堆叠前问是/否）+ 结算动作减防后补死亡判定（UI 随移动事件刷新）
                new Promise("JC100 发动时付费（触发入堆叠前的可选费用）",
                    "Cards/Effects/PlayerCards/JC/JC100_jianyahuanxiang.cs",
                    "OptionalPayCost = 1", 1, "TestB3_2B_JC100支付在入堆叠前_效果尚未结算"),
                new Promise("JC105 同口径：支付2＝发动时费用",
                    "Cards/Effects/PlayerCards/JC/JC105_emenglingzhu.cs",
                    "OptionalPayCost = 2", 1, "TestDeathSeal_支付2则死亡灵体被封印到目标角色上"),
                new Promise("减防动作后补死亡判定（结算路径）", "Script/Manager/EffectEngine.cs",
                    "if (amt < 0) RecheckDeathsAfterDefenseDrop();", 1,
                    "TestB3_2D_JC100减防致死_走死亡流程并广播移动事件"),
                // ③ JC107：资产卡进入"选目标可点"的命中范围（单机命中/高亮 + 联机命中/高亮）
                new Promise("资产卡命中范围·在册 UI 单一枚举", "Script/UI/Arena.cs",
                    "internal IEnumerable<CardUI> AssetCardUIs", 1, "TestB3_3A_JC107目标含附属与资产"),
                new Promise("资产卡命中范围·单机（落点／拖拽高亮／清高亮）",
                    "Script/UI/Interaction/CardDragController.cs",
                    "foreach (var c in _arena.AssetCardUIs)", 3, "TestB3_3A_JC107目标含附属与资产"),
                new Promise("资产卡命中范围·联机（落点／拖拽高亮）", "Script/UI/ArenaOl.cs",
                    "zone != CardZone.AreaZone && zone != CardZone.AttachedZone && zone != CardZone.AssetZone", 2,
                    "TestB3_3A_JC107目标含附属与资产"),
                // ④ JC108：目标含自身（卡数据 + 单机/联机"拖回自身"落点 + 联机投影/客户端候选）
                new Promise("JC108 目标含自身（卡数据）",
                    "Cards/Effects/PlayerCards/JC/JC108_qiaozhipazuozhimengren.cs",
                    "CanTargetSelf = true", 1, "TestB3_4A_JC108自身是合法目标"),
                new Promise("拖回自身＝指定自身·单机", "Script/UI/Interaction/CardDragController.cs",
                    "bool selfTarget = target != null && target == source && dragged;", 1,
                    "TestB3_4B_JC108以自身为目标发动_牌库顶牌封在自身"),
                new Promise("拖回自身＝指定自身·联机", "Script/UI/ArenaOl.cs",
                    "isSelf && dragged", 1, "TestB3_4B_JC108以自身为目标发动_牌库顶牌封在自身"),
                new Promise("联机客户端候选含自身", "Script/UI/ArenaOl.cs",
                    "c == source && ability.Target.CanTargetSelf != true", 1, "TestB3_4C_JC108联机投影含自身"),
                new Promise("服务端合法目标投影含自身", "Script/Manager/LegalityProjection.cs",
                    "t == card && e.Target?.CanTargetSelf != true", 1, "TestB3_4C_JC108联机投影含自身"),

                // 【2026-10 实测反馈六批】
                // ① JC103：一次性费用修正的消费与"是否真的扣费"解耦（费用被减到 0 也要消费 ⇒ UI 才有机会刷新）
                new Promise("一次性减费消费·打出/现身两条路径均按『用到了减免』消费", "Script/Manager/TurnManager.cs",
                    "if (costModApplied > 0)", 2, "TestB4_1A_JC103一次性减费_费用被减到0也必须消费"),
                new Promise("一次性减费消费·打出（实例重载消费点）", "Script/Manager/TurnManager.cs",
                    "EE.ConsumeCostModification(playerId, instance, tmpl.Organization);", 1,
                    "TestB4_1A_JC103一次性减费_费用被减到0也必须消费"),
                new Promise("一次性减费消费·现身（付费通道）", "Script/Manager/TurnManager.cs",
                    "EE.ConsumeCostModification(controller, instance, tmpl.Organization", 1,
                    "TestB4_1A_JC103一次性减费_费用被减到0也必须消费"),
                // ② JC104：预测窗文案（可复用）——窗口标题栏/标题/两区/提示全部按场景给，不再显示成调查对抗 UI
                new Promise("预测窗文案·单一数据源（可复用）", "Script/UI/AllocationWindowTexts.cs",
                    "public static AllocationWindowTexts Prediction(int count)", 1,
                    "TestB4_2A_JC104预测窗文案_不是调查对抗UI"),
                new Promise("预测窗文案·套用入口（含窗口标题栏）", "Script/UI/InvestigationWindow.cs",
                    "public void ApplyTexts(AllocationWindowTexts texts)", 1,
                    "TestB4_2A_JC104预测窗文案_不是调查对抗UI"),
                new Promise("预测窗文案·单机使用", "Script/UI/Arena.cs",
                    "AllocationWindowTexts.Prediction(count)", 1, "TestB4_2A_JC104预测窗文案_不是调查对抗UI"),
                new Promise("预测窗文案·联机使用", "Script/UI/ArenaOl.cs",
                    "AllocationWindowTexts.Prediction(cards.Count)", 1, "TestB4_2A_JC104预测窗文案_不是调查对抗UI"),
                // ③ JC105：关键字授予/撤销后重算光环（运行期获得【灵体】的卡也要被 JC105 补授白星）
                new Promise("关键字变更后重算光环（单一收口）", "Script/Manager/EffectEngine.cs",
                    "ReevaluateAurasAfterKeywordChange();", 2, "TestB4_3A_JC105光环_JC106授予灵体后立刻补授白星"),
                new Promise("光环关键词判定读『运行期授予 ∪ 卡面固有』", "Script/Manager/EffectEngine.cs",
                    "t.HasKeyword(auraEffect.AuraFilterKeyword)", 1,
                    "TestB4_3A_JC105光环_JC106授予灵体后立刻补授白星"),
                // ④ JC106：授予的【灵体】链路（识别 → 判定 → 伤害防止挂接）
                new Promise("灵体识别读『运行期授予 ∪ 卡面固有』", "Script/Manager/DamageRules.cs",
                    "card.HasActiveKeyword(\"灵体\")", 1, "TestB4_4A_JC106授予灵体_判定与伤害防止"),
                new Promise("灵体伤害防止挂接点（唯一收口）", "Script/Manager/GameInitializer.cs",
                    "CardManager.DamagePreventer = (target, amount) =>", 1,
                    "TestB4_4C_JC106正式打出_目标角色获得灵体并免伤"),
                // ⑤ JC108：双击激活（不进拖拽）+ 目标含自身
                new Promise("JC108 双击激活（卡数据）",
                    "Cards/Effects/PlayerCards/JC/JC108_qiaozhipazuozhimengren.cs",
                    "ActivateByDoubleClick = true", 1, "TestB4_5A_JC108双击激活_目标含自身"),
                new Promise("双击激活 ⇒ 不进拖拽（消费点）", "Script/Manager/ActivateEligibility.cs",
                    "e?.ActivateByDoubleClick == true", 1, "TestB4_5A_JC108双击激活_目标含自身"),
                // ⑥ JC110：手牌行动的**手牌双击**（全局输入不再把双击吞给拖拽）
                new Promise("手牌双击激活·全局输入先判双击（含自持时间窗）", "Script/UI/Arena.cs",
                    "TryHandleHandActivationDoubleClick(dragDown.GlobalPosition, dragDown.DoubleClick)", 1,
                    "TestB4_6A_JC110手牌双击激活_来源与门禁"),
                new Promise("手牌双击激活·处理入口", "Script/UI/Arena.cs",
                    "private bool TryHandleHandActivationDoubleClick(Vector2 globalPos, bool eventSaysDoubleClick)", 1,
                    "TestB4_6A_JC110手牌双击激活_来源与门禁"),
                new Promise("手牌双击激活·来源判定单一实现", "Script/Manager/ActivateEligibility.cs",
                    "ZoneScope.HandZone", 1, "TestB4_6A_JC110手牌双击激活_来源与门禁"),
                // ⑦ JC111：防御 1（不是无防御卡）
                new Promise("JC111 印刷防御＝1（卡数据）",
                    "Cards/Data/PlayerCards/JC/JC111_hundunhudie.tres",
                    "Defense = 1", 1, "TestB4_7A_JC111是防御1的角色"),
                // 【2026-10 实测反馈八批·A134】
                // ⑧ JC108/JC110："**需要弹出提示框并且支持取消效果**，选择完目标才横置卡片/支付费用，
                //    没选择目标直接取消不要支付费用效果也不要进入堆叠" ⇒ 取目标开窗 + 取消出口（取消不付费）。
                new Promise("取目标开提示窗（开关入参）", "Script/UI/Interaction/TriggerInteractionCoordinator.cs",
                    "bool hintWindow = true)", 1, "TestHandActivate_手牌双击激活可取消_源码接线"),
                new Promise("取目标开提示窗（激活链路消费点）", "Script/UI/Arena.cs",
                    "hintWindow: true);", 1, "TestHandActivate_手牌双击激活可取消_源码接线"),
                new Promise("取目标取消出口（默认允许取消）", "Script/UI/Arena.cs",
                    "BeginActivateWithTarget(inst, only, allowCancel: true);", 1,
                    "TestHandActivate_手牌双击激活可取消_源码接线"),
                new Promise("取消不付费（引擎侧：缺目标⇒代价未付出）", "Script/Manager/EffectEngine.cs",
                    "缺少目标", 2, "TestHandActivate_取消取目标时无任何副作用"),
                // ⑨ JC110①："依然无法**付费正面打出**" ⇒ 落点判定**只在真的发动了**才截断，
                //    普通打出（JC110 自身 3 费角色卡 → 己方争夺区）必须继续走 PlayCard。
                new Promise("JC110 手牌行动落点判定（单一实现）", "Script/Manager/ActivateEligibility.cs",
                    "public static bool CanHandActivateOn(", 1,
                    "TestHandActivate_JC110仍可普通付费正面打出"),
                new Promise("JC110 落点判定只在发动时截断（不吞普通打出）",
                    "Script/UI/Interaction/CardDragController.cs",
                    "if (TryHandActivateDrop(cardUI, playerId)) return;", 1,
                    "TestHandActivate_JC110仍可普通付费正面打出"),
                // ⑩ JC110②："双击激活 inst_104 效果**可取消**" ⇒ 手牌来源的取目标带取消出口。
                new Promise("JC110 手牌行动取目标可取消（单机接线）", "Script/UI/Arena.cs",
                    "OpenAttachmentActivateChoice(hit, cancelable: true);", 1,
                    "TestHandActivate_手牌双击激活可取消_源码接线"),
                new Promise("JC110 手牌行动取目标可取消（出口入参）",
                    "Script/UI/Interaction/TriggerInteractionCoordinator.cs",
                    "_cancelActivateTargetSelection", 3, "TestHandActivate_手牌双击激活可取消_源码接线"),
                // ⑪ MSJC016："触发效果是第一个准备步骤触发，**需要入堆叠**，现在没有入堆叠直接结算了" ⇒
                //    秘社触发并入触发管线（收集 ≠ 结算），不再于 DoPrepare 直接 ResolveEffect。
                new Promise("MSJC016 秘社触发并入触发管线（收集）", "Script/Manager/TriggerRegistry.cs",
                    "CollectCouncilTurnStartTriggers(timing, ctx, collected);", 1,
                    "TestMSJC016首个准备步骤触发入堆叠而不是直接结算"),
                new Promise("MSJC016 一次性门禁＝第一个回合", "Script/Manager/TurnManager.cs",
                    "registry.ShouldCollectCouncilTurnStart = () => TurnCount == 1;", 1,
                    "TestMSJC016非第一回合不再触发"),
            };
            foreach (var p in promises)
            {
                string src = ReadProjectText(p.File);
                if (src == null)
                {
                    violations.Add($"[自检] 承诺『{p.Name}』的消费点源码不可读（{p.File}）"
                                   + "——请在项目目录内运行测试（`res://` 相对 `UWPro/`）");
                    continue;
                }
                int count = 0, idx = 0;
                while ((idx = src.IndexOf(p.Needle, idx, System.StringComparison.Ordinal)) >= 0) { count++; idx += p.Needle.Length; }
                if (count < p.MinCount)
                    violations.Add($"㉖承诺落地｜『{p.Name}』在 {p.File} 中未找到消费点关键字 `{p.Needle}`"
                                   + $"（出现 {count} 次，要求 ≥{p.MinCount}）⇒ 声明了行为但引擎未消费（静默失效）");
                if (!TestMethodExists(p.BehaviorTest))
                    violations.Add($"㉖承诺落地｜『{p.Name}』缺少行为用例 `{p.BehaviorTest}`（反射未找到）");
            }

            // 【2026-10·JC092 提示文字】**反向**断言（"不得再出现"）：联机"选择地区/卡目标"窗口的提示
            // 必须来自数据（`req.SlotHint`），不得再有硬编码的移动提示——那是移动类效果的说法，
            // 用在 JC092 死灵召唤这类"把墓地角色牌置于该地区"的效果上就是错误提示（用户实测）。
            string arenaOlSrc = ReadProjectText("Script/UI/ArenaOl.cs");
            if (arenaOlSrc == null)
                violations.Add("[自检] 承诺『提示文字·联机按数据展示』的源码不可读（Script/UI/ArenaOl.cs）");
            else if (arenaOlSrc.Contains("将移动至该地区"))
                violations.Add("⑤提示文字｜Script/UI/ArenaOl.cs 仍硬编码『将移动至该地区』"
                               + "⇒ 非移动类效果（如 JC092 死灵召唤）会显示成移动提示（须改用 `req.SlotHint`）");

            // 【2026-10·JC108 用户口径】**反向**断言：单机激活拖拽的落点判定不得再**无条件**拒绝
            // "落点＝来源卡自身"——那正是"无法选择自己为目标、拖回本卡身上被判取消"的成因。
            string dragSrc = ReadProjectText("Script/UI/Interaction/CardDragController.cs");
            if (dragSrc == null)
                violations.Add("[自检] 承诺『拖回自身＝指定自身·单机』的源码不可读（Script/UI/Interaction/CardDragController.cs）");
            else if (dragSrc.Contains("target != null && target != source &&"))
                violations.Add("④JC108｜CardDragController.EndActivateDrag 仍无条件拒绝『落点＝来源自身』"
                               + "⇒ 拖回本卡身上会被判取消（用户实测：无法选择自己为目标）");

            AssertNoViolations(violations);
        }

        /// <summary>
        /// 【判据覆盖矩阵·自检】"判据不得只有文档、没有检查"：
        ///  ① ①–㉚ 全覆盖、无缺号无重复；类型 ∈ {自动,行为,人工,并入}；
        ///  ② "自动/行为"的检查方法必须**真实存在**（反射）；
        ///  ③ "人工"必须写明落点（不得为空）；"并入"必须写明并入到哪条判据；
        ///  ④ 文档《JC效果代码核查项.md》的「判据 ↔ 检查方式 总览」必须含全部编号（避免文档/测试两侧漂移）。
        /// </summary>
        public void TestAudit_自检_判据覆盖矩阵()
        {
            var violations = new List<string>();
            foreach (var row in CoverageMatrix)
            {
                if (string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.Type))
                    violations.Add("[自检] 覆盖矩阵存在空行（编号/类型缺失）");
                switch (row.Type)
                {
                    case "自动":
                    case "行为":
                        if (string.IsNullOrEmpty(row.Check) || !TestMethodExists(row.Check))
                            violations.Add($"㉖覆盖矩阵｜判据{row.Id}（{row.Type}）的检查方法 `{row.Check}` 不存在"
                                           + "⇒ 判据只写在文档里、没有可执行检查");
                        break;
                    case "人工":
                        if (string.IsNullOrEmpty(row.Check))
                            violations.Add($"㉖覆盖矩阵｜判据{row.Id} 标为『人工』但未写明落点");
                        break;
                    case "并入":
                        if (string.IsNullOrEmpty(row.Check))
                            violations.Add($"㉖覆盖矩阵｜判据{row.Id} 标为『并入』但未写明并入哪条判据");
                        break;
                    default:
                        violations.Add($"㉖覆盖矩阵｜判据{row.Id} 的类型 `{row.Type}` 非法（应为 自动/行为/人工/并入）");
                        break;
                }
            }
            // 编号完整性：①–㉚（本文件按 ㉑-a/b/c 细分，矩阵只登记主编号）
            var expect = new System.Collections.Generic.List<string>
            {
                "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫","⑬","⑭","⑮","⑯","⑰","⑱","⑲","⑳",
                "㉑","㉒","㉓","㉔","㉕","㉖","㉗","㉘","㉙","㉚",
            };
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var row in CoverageMatrix)
                if (!string.IsNullOrEmpty(row.Id) && !seen.Add(row.Id))
                    violations.Add($"㉖覆盖矩阵｜判据{row.Id} 重复登记");
            foreach (var id in expect)
                if (!seen.Contains(id)) violations.Add($"㉖覆盖矩阵｜判据{id} 未登记（覆盖矩阵缺号）");

            // 文档一致性：总览表必须列出全部编号（含新增 ㉖–㉙）
            string doc = ReadProjectText("文档/进度与核查/JC效果代码核查项.md");
            if (doc == null)
            {
                violations.Add("[自检] 判据文档 `文档/进度与核查/JC效果代码核查项.md` 不可读 ⇒ 无法校验文档与矩阵一致");
            }
            else
            {
                int from = doc.IndexOf("## 判据 ↔ 检查方式 总览", System.StringComparison.Ordinal);
                int to = from >= 0 ? doc.IndexOf("\n## ", from + 5, System.StringComparison.Ordinal) : -1;
                string section = from >= 0 ? doc.Substring(from, (to > from ? to - from : doc.Length - from)) : "";
                if (section.Length == 0) violations.Add("[自检] 判据文档缺少「判据 ↔ 检查方式 总览」节");
                section = section.Replace("*", "");
                foreach (var id in expect)
                    if (!section.Contains(id))
                        violations.Add($"㉖覆盖矩阵｜文档总览节缺少判据{id}（文档/矩阵漂移）");
            }

            AssertNoViolations(violations);
        }

        /// <summary>覆盖矩阵一行：判据编号 → 类型（自动/行为/人工/并入）→ 检查方法名或落点说明。</summary>
        private readonly struct Coverage(string id, string type, string check)
        {
            public readonly string Id = id;
            public readonly string Type = type;
            public readonly string Check = check;
        }

        /// <summary>
        /// 判据 ↔ 检查方式矩阵（与《JC效果代码核查项.md》「判据 ↔ 检查方式 总览」逐条对应）。
        /// 新增判据时必须同时：① 在文档总览表加行；② 在此登记"真实存在的检查方法"。
        /// </summary>
        private static readonly Coverage[] CoverageMatrix =
        {
            new Coverage("①", "并入", "并入⑫（主体类别 × Timing 自洽）"),
            new Coverage("②", "自动", "TestAudit_JC类别_触发与持续不得误用"),
            new Coverage("③", "自动", "TestAudit_JC区域_Scope仅场上区或需卡文依据"),
            new Coverage("④", "自动", "TestAudit_JC朝向_CovertOnly与构筑级需卡文依据"),
            new Coverage("⑤", "自动", "TestAudit_JC目标_声明自洽"),
            new Coverage("⑥", "自动", "TestAudit_JC目标_声明自洽"),
            new Coverage("⑦", "自动", "TestAudit_JC实现_动作动词与卡文语义一致"),
            new Coverage("⑧", "行为", "TestContract_交互收尾_是窗与持锁"),
            new Coverage("⑨", "自动", "TestAudit_JC目标筛选_限制条件与卡文一致"),
            new Coverage("⑩", "自动", "TestAudit_JC注册_数量对齐"),
            new Coverage("⑪", "自动", "TestAudit_JC卡文_卡名自指登记"),
            new Coverage("⑫", "自动", "TestAudit_JC类别_与Timing自洽"),
            new Coverage("⑬", "自动", "TestAudit_JC白名单_阶段名与取值与谓词已登记"),
            new Coverage("⑭", "自动", "TestAudit_JC代价_关键代价字段存在性"),
            new Coverage("⑮", "自动", "TestAudit_JC次数限制_与卡文一致"),
            new Coverage("⑯", "行为", "TestContract_白名单_只约束附属且仅限结附路径"),
            new Coverage("⑰", "自动", "TestAudit_承诺落地清单"),
            new Coverage("⑱", "自动", "TestAudit_本批BUG回归防线"),
            new Coverage("⑲", "自动", "TestAudit_JC期限_Duration与卡文一致"),
            new Coverage("⑳", "自动", "TestAudit_JC结构_EffectId唯一与非空壳"),
            new Coverage("㉑", "自动", "TestAudit_JC切分_段数与效果条数一致"),
            new Coverage("㉒", "自动", "TestAudit_JC注册_数量对齐"),
            new Coverage("㉓", "并入", "并入②（类别误用＝效果类型判据）"),
            new Coverage("㉔", "人工", "卡文歧义当场回问用户；落点＝《JC效果代码核查项.md》判据㉔、《JC效果核查台账.md》卡文歧义登记区"),
            new Coverage("㉕", "并入", "并入⑤（取目标默认允许自身）"),
            new Coverage("㉖", "自动", "TestAudit_承诺落地清单"),
            new Coverage("㉗", "行为", "TestContract_多声明OR_两类别各自可结算"),
            new Coverage("㉘", "行为", "TestContract_屏障_候选与合法性四路径一致"),
            new Coverage("㉙", "行为", "TestContract_交互收尾_数值窗单机与联机"),
            new Coverage("㉚", "行为", "TestB2_4A_JC089减防致死_防御1未受伤"),
        };

        /// <summary>该测试方法名在本程序集内是否存在（覆盖矩阵"检查必须真实存在"的判定）。</summary>
        private static bool TestMethodExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var t in typeof(JCEffectSelfAuditTests).Assembly.GetTypes())
            {
                if (t == null) continue;
                if (t.GetMethod(name, System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static) != null)
                    return true;
            }
            return false;
        }

        /// <summary>读取项目内文本文件（`res://` 相对 `UWPro/`）；不可读返回 null（由调用方判违规）。</summary>
        private static string ReadProjectText(string resRelative)
        {
            try
            {
                using var f = global::Godot.FileAccess.Open("res://" + resRelative,
                    global::Godot.FileAccess.ModeFlags.Read);
                return f?.GetAsText();
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
