using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-09 整体复查】JC 包**卡面自检**（把人工复查的判据固化为自动护栏）。
    ///
    /// 背景（见 `人工检查进度.md` 与清单 A117）：已检查的 69 张卡正确率较高，未检查的 JC070–JC135
    /// 靠人工逐条核对代价高、易漏；而**历次高频缺陷**几乎都落在"卡面文本 ↔ 脚本装载"不一致上：
    ///  ① 效果门禁（快速行动误写成限行动阶段）；
    ///  ② 强制/非强制（卡文无"强制"却 `Forced=true`）；
    ///  ③ 是/否型支付却弹数值输入框；
    ///  ④ 关键字没装载（`HasInherentKeyword` **只读 `EffectDataList`**，全仓没有任何"按卡面文本自动装载
    ///     关键字"的机制 —— 已 grep 确认；JC059/JC079/JC080/JC098/JC121 的"公开"、JC080 的"声望"因此失效）；
    ///  ⑤ 触发时机与卡文短语不符（效果永不触发）。
    ///
    /// 本套件按上述判据遍历 `res://Cards/Data/PlayerCards/JC/` 下**全部**卡：违反判据即失败，
    /// 并输出全部违规项（不是只报第一条）。确属"文本出现该词但确实不是该词条"的例外写入
    /// <see cref="Exemptions"/>（键＝`卡UID前缀|判据|词`，值＝理由），保证测试全绿且例外可审计。
    ///
    /// 纪律：**新增/修改卡脚本后本套件必须仍然全绿**；若判据本身需要放宽，先在此处登记例外并写明理由。
    /// </summary>
    public class JCCardTextAuditTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>
        /// 豁免表：`卡UID前缀|判据|词` → 理由。用于"卡文出现该词，但按规则确实不要求该装载"的情形。
        /// （判据取值：`keyword` / `gate` / `forced` / `timing`；词＝触发判据的关键词/短语）
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Exemptions
            = new System.Collections.Generic.Dictionary<string, string>
        {
            // —— 关键字判据的例外 ——
            { "JC109|keyword|灵体", "『灵体』是检索筛选条件（寻找一张灵体角色牌），不是本卡自身词条" },
            { "JC092|keyword|公开", "『公开』只出现在 FAQ（死灵召唤可指定墓地中带公开关键词的角色）" },
            // —— 触发时机判据的例外 ——
            { "JC018|timing|战斗获胜", "威名关键词由 `KeywordEffects.WeiMing()` 内建 OnCombatWon（脚本只声明关键词）" },
            { "JC079|timing|赢取", "走 K3『去处替代』（Timing=Static + WinAreaDestination），与 OnAreaWon 同一谓词实现" },
            { "JC116|timing|赢取", "『撤回』是引擎级地区赢取规则（AreaManager 消费 Withdraw 关键词），非入堆叠触发" },
            { "JC120|timing|赢取", "『赢取本地区』的就地改判走 K3 去处替代通道（见 P3ZoneConvertTests）" },
            { "JC063|timing|被潜伏", "『将该角色翻面为暗藏者』是**动作**（Latent），不是 OnLatent 触发" },
            { "JC066|timing|被封印", "『封印于瑟琳娜上』是**动作**（Seal），不是 OnSealed 触发" },
            { "JC088|timing|被潜伏", "『快速行动2：潜伏』是**动作**（Latent），不是 OnLatent 触发" },
            // —— 门禁判据的例外 ——（当前无：JC117 已用 `OnlyPhases="行动阶段"` 真正落地，由判据①覆盖）
            // —— 判据⑥（本地区目标是否允许自身）的例外：脚本已显式声明 false，或需用户裁定 ——
            { "JC020|selftarget|本地区目标",
              "本卡是**附属**（来源不是角色）⇒ 『自身』永远不可能是角色候选，`CanTargetSelf` 无意义" },
            { "JC058|selftarget|本地区目标",
              "目标大类是**暗藏者**，而本卡触发时已现身（正面）⇒ 自身不可能是候选" },
            { "JC100|selftarget|本地区目标",
              "脚本**已显式**声明 `CanTargetSelf = false`（『自己不能封在自己身上』），属有意为之" },
            { "JC066|selftarget|本地区目标",
              "用户 2026-09 裁定：『规则上瑟琳娜不能把自己封印在自己身上』⇒ 目标**排除自身**是正确的；"
              + "卡脚本已显式 `CanTargetSelf=false`（注释写明意图）" },
            { "JC088|selftarget|本地区目标",
              "用户 2026-09 裁定：目标是『印刷费用 ≤2 的角色』，而**自身印刷费用为 3** ⇒ 自身本就不符合目标条件；"
              + "卡脚本已显式 `CanTargetSelf=false`（注释写明意图）" },
        };

        /// <summary>
        /// **未实现卡**（无效果脚本）：本套件只审"已实现卡"的**装载一致性**；未实现卡整卡跳过，
        /// 由进度表/台账按"未实现"登记（当前仅 JC070 浅心思士：`Cards/Effects/PlayerCards/JC/JC070_*.cs` 不存在）。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> UnimplementedCards
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// 该卡是否含"**标志词条**"（`KeywordEffects.Marker()`）的强制进场触发：
        /// "N标志X"词条本体定义为**强制**进场触发（`Marker()` 自带 `Forced=true` + FaqNote"强制"），
        /// 故这类卡的 `Forced=true` 与卡面是否印"强制触发"无关（判据②的合法例外）。
        /// </summary>
        private static bool HasMarkerKeywordForced(PlayerCardData t)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
            {
                if (e == null || e.Timing != EffectTiming.EnterTrigger || e.Actions == null) continue;
                foreach (var a in e.Actions)
                {
                    if (a == null || a.Verb != ActionVerb.PlaceCounter || a.Args == null) continue;
                    if (a.Args.Count > 2 && a.Args[2] == "自身") return true;
                }
            }
            return false;
        }

        // ==================== 判据实现 ====================

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

        /// <summary>卡面文本（去掉 FAQ 段——FAQ 里的解释性文字常提到别的词条，会造成误报）。</summary>
        private static string CardText(PlayerCardData tmpl)
        {
            var sb = new System.Text.StringBuilder();
            if (tmpl?.Effects != null) foreach (var line in tmpl.Effects) if (!string.IsNullOrEmpty(line)) sb.Append(line).Append(' ');
            string s = sb.ToString();
            int faq = s.IndexOf("FAQ：", System.StringComparison.Ordinal);
            return faq >= 0 ? s.Substring(0, faq) : s;
        }

        /// <summary>卡面"前缀区"＝第一个『：』之前的部分（词条前缀都写在这里，效果描述里的同名词不算词条）。</summary>
        private static string PrefixZone(PlayerCardData tmpl)
        {
            string s = CardText(tmpl);
            int i = s.IndexOf('：');
            return i >= 0 ? s.Substring(0, i) : s;
        }

        private static bool HasKeyword(PlayerCardData t, params string[] kws)
        {
            if (t == null) return false;
            foreach (var k in kws) if (!string.IsNullOrEmpty(k) && t.HasInherentKeyword(k)) return true;
            return false;
        }

        /// <summary>按 EffectId 取效果（本套件内部小工具）。</summary>
        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static bool HasTiming(PlayerCardData t, EffectTiming timing)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList) if (e != null && e.Timing == timing) return true;
            return false;
        }

        private static bool HasGrant(PlayerCardData t, string kind, string value)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
            {
                if (e?.ContinuousGrants == null) continue;
                foreach (var g in e.ContinuousGrants)
                    if (g != null && g.Kind == kind && g.Value == value) return true;
            }
            return false;
        }

        private static bool HasStatic(PlayerCardData t, StaticKind kind)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
            {
                if (e?.StaticEffects == null) continue;
                foreach (var s in e.StaticEffects) if (s != null && s.Kind == kind) return true;
            }
            return false;
        }

        private static bool AnyForced(PlayerCardData t)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList) if (e != null && e.Forced) return true;
            return false;
        }

        private static bool HasWinAreaDestination(PlayerCardData t)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
                if (e != null && !string.IsNullOrEmpty(e.WinAreaDestination)) return true;
            return false;
        }

        private static bool HasActionVerb(PlayerCardData t, ActionVerb verb)
        {
            if (t?.EffectDataList == null) return false;
            foreach (var e in t.EffectDataList)
            {
                if (e?.Actions == null) continue;
                foreach (var a in e.Actions) if (a != null && a.Verb == verb) return true;
            }
            return false;
        }

        private static string Exempt(string uid, string kind, string word)
        {
            string key = uid + "|" + kind + "|" + word;
            return Exemptions.TryGetValue(key, out var reason) ? reason : null;
        }

        private static string UidPrefix(PlayerCardData t, string file)
        {
            string uid = t?.UID;
            if (string.IsNullOrEmpty(uid)) uid = file;
            int i = uid.IndexOf('_');
            return i > 0 ? uid.Substring(0, i) : uid;
        }

        private static void AssertNoViolations(List<string> violations)
            => Assert.IsTrue(violations.Count == 0,
                "卡面自检发现 " + violations.Count + " 条违规：\n" + string.Join("\n", violations));

        // ==================== 判据 4：关键字装载 ====================

        /// <summary>
        /// 判据④：卡面**前缀区**印了某词条 ⇒ 脚本必须真的装载它（关键字/触发/静止规则）。
        /// 依据：`PlayerCardData.HasInherentKeyword` 只读 `EffectDataList`；全仓没有"按卡面文本自动装载"的机制。
        /// </summary>
        public void TestAudit_JC包_关键字装载()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;   // 未实现卡整卡跳过（见字段注释）
                string pre = PrefixZone(tmpl);

                void Check(string word, bool loaded, string requirement)
                {
                    if (pre.IndexOf(word, System.StringComparison.Ordinal) < 0) return;
                    if (loaded) return;
                    if (Exempt(uid, "keyword", word) != null) return;
                    violations.Add(uid + "（" + file + "）：卡面前缀含『" + word + "』但" + requirement);
                }

                Check("公开", HasKeyword(tmpl, "公开"), "未装载关键字（`KeywordEffects.Public()`/`ContinuousGrants{Keyword:公开}`）"
                    + " ⇒ `HasPublicKeyword=false`，『不能被秘密派遣为暗藏者』门禁失效");
                Check("公開", HasKeyword(tmpl, "公开"), "同上");
                Check("隐匿", HasKeyword(tmpl, "隐匿"), "未装载『隐匿』关键字");
                Check("隐秘", HasKeyword(tmpl, "隐匿", "隐秘"), "未装载『隐匿/隐秘』关键字");
                Check("迟缓", HasKeyword(tmpl, "迟缓"), "未装载『迟缓』关键字");
                Check("传闻", HasKeyword(tmpl, "传闻"), "未装载『传闻』关键字");
                Check("暴露", HasKeyword(tmpl, "暴露"), "未装载『暴露』关键字");
                Check("撤回", HasKeyword(tmpl, "撤回"), "未装载『撤回』关键字（`KeywordEffects.Withdraw()`）");
                Check("灵体", HasKeyword(tmpl, "灵体"), "未装载『灵体』关键字（灵体免伤由该关键字驱动）");
                Check("领袖", HasKeyword(tmpl, "领袖"), "未装载『领袖』关键字（`KeywordEffects.Leader()`）");
                Check("非资产", HasKeyword(tmpl, "非资产"), "未装载『非资产』关键字（`KeywordEffects.NonAsset()`）");
                Check("不能被装备结附", HasStatic(tmpl, StaticKind.CannotBeAttached), "未声明静止规则 `StaticKind.CannotBeAttached`");
                Check("机动", HasTiming(tmpl, EffectTiming.ManeuverTrigger), "未声明 `EffectTiming.ManeuverTrigger`（`KeywordEffects.Maneuver()`）");
                Check("杀伤", HasGrant(tmpl, "Attribute", "杀伤"), "未装载『杀伤』属性（`KeywordEffects.Kill(n)`）");
                // 声望：卡面固有 ⇒ 模板固有判定为真时，`RuntimeTriggerTemplates` 通道会**跳过**（避免重复），
                // 因此固有声望的卡必须**显式**声明声望触发（`KeywordEffects.Renown()`）。
                Check("声望", HasKeyword(tmpl, "声望"), "未装载『声望』关键字（`KeywordEffects.RenownKeyword()`）");
                if (pre.IndexOf("声望", System.StringComparison.Ordinal) >= 0
                    && HasKeyword(tmpl, "声望") && !HasTiming(tmpl, EffectTiming.OnPowerCombatEnded)
                    && Exempt(uid, "keyword", "声望触发") == null)
                    violations.Add(uid + "（" + file + "）：卡面固有『声望』但未显式声明声望触发"
                        + "（`KeywordEffects.Renown()`＝OnPowerCombatEnded；固有关键字不会走模板合成通道）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 1：门禁 ====================

        /// <summary>
        /// 判据①：卡文"只能在行动阶段打出" ⇒ `LimitToActionPhaseOnly=true`；
        /// 只印"快速行动："（**不同时**印前一短语） ⇒ C 档（`PlayTiming.Response`、**不限**行动阶段）。
        /// 依据：A115①（JC063 反例）；同时印两条的卡（JC021/JC091/JC102/JC118）与已验收的 JC021 同款。
        /// </summary>
        public void TestAudit_JC包_门禁()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;   // 未实现卡整卡跳过
                string text = CardText(tmpl);
                bool textActionOnly = text.Contains("只能在行动阶段打出");
                bool textQuickPlay = text.Contains("快速行动：") || text.Contains("快速行动:");

                if (textActionOnly && !tmpl.LimitToActionPhaseOnly && Exempt(uid, "gate", "只能在行动阶段打出") == null)
                    violations.Add(uid + "（" + file + "）：卡文『只能在行动阶段打出』但未设 `LimitToActionPhaseOnly`");

                // 【2026-09·JC117】"该能力只能在行动阶段发动"：激活能力需声明**阶段白名单**（`OnlyPhases`）
                // 或至少一个"行动"窗口（`ActivateWindow` 含"行动"）——两者都消费 `PhaseGateRule`/窗口门禁。
                if (text.Contains("该能力只能在行动阶段发动") && Exempt(uid, "gate", "该能力只能在行动阶段发动") == null)
                {
                    bool gated = false;
                    if (tmpl.EffectDataList != null)
                        foreach (var e in tmpl.EffectDataList)
                        {
                            if (e == null) continue;
                            if (!string.IsNullOrEmpty(e.OnlyPhases)) gated = true;
                            if (!string.IsNullOrEmpty(e.ActivateWindow) && e.ActivateWindow.Contains("行动")) gated = true;
                        }
                    if (!gated)
                        violations.Add(uid + "（" + file + "）：卡文『该能力只能在行动阶段发动』但既无 `OnlyPhases` 白名单、"
                            + "也无行动窗口（`ActivateWindow` 含『行动』）");
                }

                if (textQuickPlay && !textActionOnly && Exempt(uid, "gate", "快速行动") == null)
                {
                    bool response = false;
                    if (tmpl.EffectDataList != null)
                        foreach (var e in tmpl.EffectDataList)
                            if (e != null && e.PlayTiming == PlayTiming.Response) response = true;
                    if (!response)
                        violations.Add(uid + "（" + file + "）：卡文『快速行动：』但既非 C 档（`PlayTiming.Response`）"
                            + "也未声明 `LimitToActionPhaseOnly`");
                    if (tmpl.LimitToActionPhaseOnly)
                        violations.Add(uid + "（" + file + "）：卡文只印『快速行动：』却被限行动阶段"
                            + "（`LimitToActionPhaseOnly=true` ⇒ 非行动阶段打不出来；见 JC063/JC074）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 2：强制 / 非强制 ====================

        /// <summary>
        /// 判据②：卡文含"强制触发" ⇒ 至少一条效果 `Forced=true`；卡文**无**"强制" ⇒ 不得有 `Forced=true`。
        /// 依据：JC056/JC104 口径（只有卡文印"强制触发"的才强制）。
        /// </summary>
        public void TestAudit_JC包_强制与非强制()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;   // 未实现卡整卡跳过
                string text = CardText(tmpl);
                bool textForced = text.Contains("强制触发") || text.Contains("强制：");
                bool forced = AnyForced(tmpl);

                if (textForced && !forced && Exempt(uid, "forced", "应强制") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『强制触发』但没有任何 `Forced=true` 的效果");
                if (!textForced && forced && !HasMarkerKeywordForced(tmpl) && Exempt(uid, "forced", "不应强制") == null)
                    violations.Add(uid + "（" + file + "）：卡文无『强制』却存在 `Forced=true`（过度强制；见 JC056/JC104）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 5：触发时机 ====================

        /// <summary>
        /// 判据⑤：卡文短语 ⇒ 期望的 `EffectTiming` 至少出现一次（时机错＝效果永不触发）。
        /// 只覆盖**语义明确**的触发短语；"直到回合结束"等时效用语不在内。
        /// </summary>
        public void TestAudit_JC包_触发时机()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;   // 未实现卡整卡跳过
                string text = CardText(tmpl);

                void Check(string phrase, bool ok, string requirement)
                {
                    if (!text.Contains(phrase)) return;
                    if (ok) return;
                    if (Exempt(uid, "timing", phrase) != null) return;
                    violations.Add(uid + "（" + file + "）：卡文含『" + phrase + "』但" + requirement);
                }

                Check("进场触发", HasTiming(tmpl, EffectTiming.EnterTrigger), "未见 `EffectTiming.EnterTrigger`");
                Check("现身触发", HasTiming(tmpl, EffectTiming.UncoverTrigger), "未见 `EffectTiming.UncoverTrigger`");
                Check("死亡触发", HasTiming(tmpl, EffectTiming.OnCardDied), "未见 `EffectTiming.OnCardDied`");
                Check("结束步骤触发", HasTiming(tmpl, EffectTiming.OnTurnEnded), "未见 `EffectTiming.OnTurnEnded`");
                Check("每当你抓牌", HasTiming(tmpl, EffectTiming.OnDraw), "未见 `EffectTiming.OnDraw`");
                Check("当你抓牌", HasTiming(tmpl, EffectTiming.OnDraw), "未见 `EffectTiming.OnDraw`");
                Check("赢取他人地区", HasTiming(tmpl, EffectTiming.OnAreaWon), "未见 `EffectTiming.OnAreaWon`");
                Check("被潜伏时", HasTiming(tmpl, EffectTiming.OnLatent), "未见 `EffectTiming.OnLatent`");
                Check("势力对抗结束", HasTiming(tmpl, EffectTiming.OnPowerCombatEnded), "未见 `EffectTiming.OnPowerCombatEnded`");

                // 赢取本地区：允许"入堆叠触发"或 K3"去处替代"通道
                if (text.Contains("赢取本地区")
                    && !HasTiming(tmpl, EffectTiming.OnAreaWon) && !HasWinAreaDestination(tmpl)
                    && Exempt(uid, "timing", "赢取") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『赢取本地区』但既无 `OnAreaWon` 触发、也无 K3 去处替代");

                // 战斗对抗获胜：允许 OnCombatWon，或该卡带『威名』（关键词内建 OnCombatWon）
                if (text.Contains("战斗对抗获胜")
                    && !HasTiming(tmpl, EffectTiming.OnCombatWon) && !HasKeyword(tmpl, "威名")
                    && Exempt(uid, "timing", "战斗获胜") == null)
                    violations.Add(uid + "（" + file + "）：卡文含『战斗对抗获胜』但无 `OnCombatWon` 且非威名关键词");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 3：是/否型支付不得依赖数值输入框 ====================

        /// <summary>
        /// 判据③（数据侧）："你可以支付N"（固定金额）型效果用的是 `ActionVerb.PayOptional`——
        /// 引擎现对**固定金额**（min==max）走二选一窗 `PromptOptionalPay`（不再弹数值框，见 A116/A117）。
        /// 本用例锁定"该卡的支付是固定金额"这一前提（若哪天写成区间 min&lt;max，就会重新退回数值框，需显式复核）。
        /// </summary>
        public void TestAudit_JC包_固定金额可选支付不得是区间()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl?.EffectDataList == null) continue;
                string uid = UidPrefix(tmpl, file);
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Actions == null) continue;
                    foreach (var a in e.Actions)
                    {
                        if (a == null || a.Verb != ActionVerb.PayOptional) continue;
                        int def = ArgInt(a, 0, 1);
                        int min = ArgInt(a, 1, 0);
                        int max = ArgInt(a, 2, def);
                        // 【2026-09 复查口径】`PayOptional` 只应对"你可以支付N"（是/否）使用 ⇒ 合法值域是
                        // `min==max`（固定金额，走二选一窗）或 0..1（是/否）；**max>1 的区间**意味着
                        // "选一个数字"，会退回数值输入框，必须复核是否该改用真数值通道（如 `ValueExpr=玩家选择`）。
                        if (max > 1 && min != max)
                            violations.Add(uid + "（" + file + "）：`PayOptional` 值域 " + min + ".." + max
                                + "（max>1）⇒ 会退回**数值输入框**；卡文若是『你可以支付N』应改为 `min==max`（二选一窗）");
                    }
                }
            }
            AssertNoViolations(violations);
        }

        private static int ArgInt(EffectAction a, int idx, int fallback)
            => (a.Args != null && a.Args.Count > idx && int.TryParse(a.Args[idx], out var v)) ? v : fallback;

        // ==================== 判据 6：本地区目标是否允许"自身"必须显式 ====================

        /// <summary>
        /// 判据⑥（JC004 同款）：卡文写"**本地区的目标角色/暗藏者**"且**未排除自身**（没有 其他/另一个/敌方/对手）
        /// ⇒ 按 JC004 的既定口径应允许取自身（`CanTargetSelf = true`）；`TargetSpec.CanTargetSelf` 默认 **false**，
        /// 漏声明会让"本地区只有它自己"时**候选为空 ⇒ 触发被静默跳过**（JC004 实测踩过）。
        /// 例外见 <see cref="Exemptions"/>（本卡自身不是角色候选、或需用户裁定）。
        /// </summary>
        public void TestAudit_JC包_本地区目标需显式声明自身可否()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                string text = CardText(tmpl);
                if (!text.Contains("本地区")) continue;
                if (!text.Contains("目标角色") && !text.Contains("目标暗藏者") && !text.Contains("选择一个角色")) continue;
                if (text.Contains("其他") || text.Contains("另一个") || text.Contains("其它")
                    || text.Contains("敌方") || text.Contains("对手") || text.Contains("拥有者")) continue;

                bool localCardTarget = false, selfAllowed = false;
                if (tmpl.EffectDataList != null)
                    foreach (var e in tmpl.EffectDataList)
                    {
                        if (e?.Target == null || !e.Target.IsLocalZone) continue;
                        if (e.Target.Kind != TargetKind.Character && e.Target.Kind != TargetKind.Covert) continue;
                        localCardTarget = true;
                        if (e.Target.CanTargetSelf) selfAllowed = true;
                    }
                if (!localCardTarget || selfAllowed) continue;
                if (Exempt(uid, "selftarget", "本地区目标") != null) continue;
                violations.Add(uid + "（" + file + "）：卡文『本地区的目标角色/暗藏者』未排除自身，"
                    + "但未声明 `CanTargetSelf = true` ⇒ 本地区只有它自己时候选为空、触发静默跳过（JC004 同款）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 10：玩家目标范围必须与卡文一致 ====================

        /// <summary>
        /// 判据⑩（2026-09 用户裁定·JC111）：`Kind=Player` 的目标范围必须与卡文用词一致——
        ///  · 卡文写"**目标敌方玩家 / 目标对手**" ⇒ `Restrictions` 必须含 `Enemy`；
        ///  · 卡文只写"**目标玩家**" ⇒ "**任意玩家，含自己**" ⇒ **不得**限制为 `Enemy`
        ///    （本轮据此修正 JC111／JC031／JC057 三张：原先被收窄成"只能选对手"）。
        /// </summary>
        public void TestAudit_JC包_玩家目标范围需与卡文一致()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null || tmpl.EffectDataList == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;

                bool hasPlayerSpec = false, hasEnemy = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e?.Target == null) continue;
                    if (e.Target.Kind != TargetKind.Player && e.Target.KindAlt != TargetKind.Player) continue;
                    hasPlayerSpec = true;
                    if (e.Target.Restrictions != null && e.Target.Restrictions.Contains(TargetRestriction.Enemy))
                        hasEnemy = true;
                }
                if (!hasPlayerSpec) continue;

                string text = CardText(tmpl);
                bool textEnemy = text.Contains("目标敌方玩家") || text.Contains("目标对手")
                    || text.Contains("敌方玩家") || text.Contains("目标对手玩家");
                bool textAnyPlayer = text.Contains("目标玩家");

                if (textAnyPlayer && !textEnemy && hasEnemy && Exempt(uid, "playertarget", "任意") == null)
                    violations.Add(uid + "（" + file + "）：卡文只写『目标玩家』（＝任意玩家，含自己）"
                        + "却把玩家目标限制为 `Enemy` ⇒ 收窄了合法目标");
                if (textEnemy && !hasEnemy && Exempt(uid, "playertarget", "敌方") == null)
                    violations.Add(uid + "（" + file + "）：卡文写『目标敌方玩家/目标对手』却未限制 `Enemy`");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 9：激活能力必须显式声明发动窗口 ====================

        /// <summary>
        /// 判据⑨（2026-09 整体复查新增）：**激活能力**（`CharacterActivated` / `AttachmentActivated` /
        /// `CouncilActivated` 等）必须显式声明 `ActivateWindow`（"行动"/"快速"，可含数字费用另用 `Cost`）。
        /// 依据：`ActivateEligibility` 用 `ActivateWindow.Contains("行动")` 判定"行动窗口"（自己行动步骤 + 空堆叠 + 有行动权）；
        /// **默认空串会让该判定整个被跳过** ⇒ 卡文写"行动N/行动"的能力被错当成"只需优先权"（响应窗口也能发动）。
        /// 本轮实测仅 JC124 一处（"行动3" 漏声明），已修。
        /// </summary>
        public void TestAudit_JC包_激活能力必须声明发动窗口()
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
                    bool activated = e.Timing == EffectTiming.CharacterActivated
                        || e.Timing == EffectTiming.AttachmentActivated
                        || e.Timing == EffectTiming.CouncilActivated;
                    if (!activated) continue;
                    if (!string.IsNullOrEmpty(e.ActivateWindow)) continue;
                    if (Exempt(uid, "window", e.EffectId) != null) continue;
                    violations.Add(uid + "（" + file + "）：激活能力 " + e.EffectId + "**未声明 `ActivateWindow`**"
                        + " ⇒ 行动窗口判定被跳过（会被当成只需优先权，可在响应窗口发动）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 8：事务卡"行动："前缀不得做成 C 档 ====================

        /// <summary>
        /// 判据⑧（2026-09 整体复查新增）：事务卡卡文以"**行动：**"开头 ⇒ 它是**普通行动**（A 档：行动步骤 + 堆叠为空），
        /// **不得**声明 `PlayTiming.Response`（那是 C 档＝快速行动/响应窗口，门禁被取消）。
        /// 依据：`TurnManager` 的 A/B/C 档注释——A 档缺省＝限行动步骤；C 档仅当卡含 `PlayTiming.Response` 效果时进入。
        /// （"快速行动："前缀的卡另由判据①管；B 档＝"只能在行动阶段打出"由 `LimitToActionPhaseOnly` 表达。）
        /// </summary>
        public void TestAudit_JC包_事务卡行动前缀不得为C档()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                string text = CardText(tmpl);
                if (!text.StartsWith("行动：") && !text.StartsWith("行动:")) continue;
                bool response = false;
                if (tmpl.EffectDataList != null)
                    foreach (var e in tmpl.EffectDataList)
                        if (e != null && e.PlayTiming == PlayTiming.Response) response = true;
                if (response && Exempt(uid, "gate", "行动前缀C档") == null)
                    violations.Add(uid + "（" + file + "）：卡文以『行动：』开头（普通行动）却含 `PlayTiming.Response` 效果"
                        + " ⇒ 被打成 C 档（响应窗口也能打出，门禁被取消）");
            }
            AssertNoViolations(violations);
        }

        // ==================== 判据 7：附属"结附于X"必须声明打出目标大类 ====================

        /// <summary>
        /// 判据⑦（2026-09 整体复查新增）：附属卡卡文写"**结附于X**" ⇒ 必须用 `TargetRules`（或 `AllowedTargets`）
        /// 声明匹配的打出目标大类。缺声明时 `PlayerCardData.GetAllowedTargets()` 走**类别默认**——
        /// 附属默认允许 `角色/附属/秘社/地区/暗藏者` **全部**目标 ⇒ 会出现"本该结附于地区的卡能结附到角色身上"
        /// 这类过宽缺陷（本轮实测 JC027/JC048/JC073/JC115/JC116 五张）。
        /// 映射：`结附于本方秘社` → Council(+Friendly)；`结附于目标地区` → Area；`结附于…角色` → Character。
        /// </summary>
        public void TestAudit_JC包_附属结附子句需声明目标大类()
        {
            var violations = new List<string>();
            foreach (var file in AllCardFiles())
            {
                var tmpl = LoadCard(file);
                if (tmpl == null) continue;
                string uid = UidPrefix(tmpl, file);
                if (UnimplementedCards.Contains(uid)) continue;
                string text = CardText(tmpl);
                if (!text.Contains("结附于")) continue;

                var declared = new List<TargetKind>();
                if (tmpl.TargetRules != null)
                    foreach (var r in tmpl.TargetRules)
                        if (r != null && r.Kind != TargetKind.None && !declared.Contains(r.Kind)) declared.Add(r.Kind);
                if (tmpl.AllowedTargets != null)
                    foreach (var k in tmpl.AllowedTargets) if (!declared.Contains(k)) declared.Add(k);

                // 从句 → 期望大类（同一张卡的多个从句都要满足，如 JC082 的"借阅/收藏"两种）
                var need = new List<TargetKind>();
                if (text.Contains("结附于本方秘社") && !need.Contains(TargetKind.Council)) need.Add(TargetKind.Council);
                if (text.Contains("结附于目标地区") && !need.Contains(TargetKind.Area)) need.Add(TargetKind.Area);
                if (text.Contains("结附于目标角色") && !need.Contains(TargetKind.Character)) need.Add(TargetKind.Character);
                if (text.Contains("结附于目标印刷类别为人类的角色") && !need.Contains(TargetKind.Character)) need.Add(TargetKind.Character);

                foreach (var k in need)
                {
                    if (declared.Contains(k)) continue;
                    if (Exempt(uid, "attach", k.ToString()) != null) continue;
                    violations.Add(uid + "（" + file + "）：卡文『结附于…』要求目标大类 " + k
                        + "，但 `TargetRules/AllowedTargets` 未声明 ⇒ 附属默认允许全部目标（过宽）");
                }
            }
            AssertNoViolations(violations);
        }

        // ==================== 本次修复卡的行为断言 ====================

        /// <summary>
        /// 【2026-09 修复】被漏装载"公开"的 5 张卡 + JC080 的"声望"：现在必须真的装载，
        /// 且"公开"要能挡住派遣暗藏（`DeployCovertEligibility`／`HasPublicKeyword`）。
        /// </summary>
        public void TestAudit_修复卡_公开关键字生效_与JC080声望装载()
        {
            foreach (var name in new[] { "JC059_anquanbaoweibumen", "JC079_shengying",
                                         "JC080_kailagediyajiaoshou", "JC098_xianrufengkuangderenqun",
                                         "JC121_kongmingyushou" })
            {
                var tmpl = LoadCard(name + ".tres");
                Assert.IsNotNull(tmpl, name + " 模板应可加载");
                Assert.IsTrue(tmpl.HasPublicKeyword(), name + " 应装载『公开』关键字（`HasPublicKeyword`）");

                var inst = new Script.Game.Entities.CardInstance(tmpl, "audit_" + name)
                {
                    OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.Hand,
                };
                Assert.IsTrue(inst.HasPublicKeyword, name + " 实例层 `HasPublicKeyword` 应为 true");
                Assert.IsFalse(Script.Manager.DeployCovertEligibility.IsEligible(inst),
                    name + " 带【公开】⇒ 不可被秘密派遣为暗藏者");
            }

            var jc080 = LoadCard("JC080_kailagediyajiaoshou.tres");
            Assert.IsTrue(jc080.HasInherentKeyword("声望"), "JC080 应装载『声望』关键字（旧实现缺失）");
            bool renownTrigger = false;
            foreach (var e in jc080.EffectDataList)
                if (e != null && e.Timing == EffectTiming.OnPowerCombatEnded) renownTrigger = true;
            Assert.IsTrue(renownTrigger, "JC080 固有声望 ⇒ 应显式声明声望触发（`KeywordEffects.Renown()`）");
        }

        /// <summary>
        /// 【2026-09 补充·用户 2026-09 裁定】JC070 补齐效果脚本（公开 + 声望关键字与触发）；
        /// JC117 激活能力加"仅行动阶段"白名单；JC066/JC088 显式排除自身（规则上不可自封 / 自身费用 3 &gt; 筛选 ≤2）。
        /// </summary>
        public void TestAudit_增量修复_JC070脚本_JC117阶段白名单_JC066与088排除自身()
        {
            // ① JC070：此前无脚本 ⇒ 两个前缀都没装载
            var jc070 = LoadCard("JC070_qianxinxiushi.tres");
            Assert.IsNotNull(jc070, "JC070 模板应可加载");
            Assert.IsTrue(jc070.HasPublicKeyword(), "JC070『公开』应装载（`HasPublicKeyword`）");
            Assert.IsTrue(jc070.HasInherentKeyword("声望"), "JC070『声望』关键字应装载");
            bool renownTrigger = false;
            foreach (var e in jc070.EffectDataList)
                if (e != null && e.Timing == EffectTiming.OnPowerCombatEnded) renownTrigger = true;
            Assert.IsTrue(renownTrigger, "JC070 固有声望 ⇒ 应显式声明声望触发（`KeywordEffects.Renown()`）");
            var inst070 = new Script.Game.Entities.CardInstance(jc070, "audit_jc070")
            {
                OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.Hand,
            };
            Assert.IsFalse(Script.Manager.DeployCovertEligibility.IsEligible(inst070),
                "JC070 带【公开】⇒ 不可被秘密派遣为暗藏者");

            // ② JC117：激活能力必须带"仅行动阶段"白名单
            var jc117 = LoadCard("JC117_hongyanhangban.tres");
            var e117 = FindEffect(jc117, "JC117_1_MoveFriendlyToTargetArea");
            Assert.IsNotNull(e117, "JC117 应有移动能力");
            Assert.AreEqual("行动阶段", e117.OnlyPhases, "卡文『该能力只能在行动阶段发动』⇒ `OnlyPhases=行动阶段`");

            // ③ JC066 / JC088：显式排除自身（用户裁定）
            var e066 = FindEffect(LoadCard("JC066_fengmopingselinna.tres"), "JC066_1_SealTarget");
            Assert.IsNotNull(e066, "JC066 应有封印触发");
            Assert.IsFalse(e066.Target.CanTargetSelf, "JC066 规则上不能把自己封印在自己身上 ⇒ 排除自身");
            var e088 = FindEffect(LoadCard("JC088_zhiyeshashou.tres"), "JC088_1_Exterminate");
            if (e088 == null)
                foreach (var e in LoadCard("JC088_zhiyeshashou.tres").EffectDataList)
                    if (e != null && e.Timing == EffectTiming.UncoverTrigger) e088 = e;
            Assert.IsNotNull(e088, "JC088 应有现身触发");
            Assert.IsFalse(e088.Target.CanTargetSelf, "JC088 自身印刷费用 3 >『≤2』筛选 ⇒ 目标排除自身");
        }

        /// <summary>
        /// 【2026-09 补充·JC117 阶段白名单的引擎行为】`EffectData.OnlyPhases` 由 `PhaseGateRule` 统一判定：
        ///  · 行动阶段（先手/后手行动步骤）→ **不排除**（可发动）；
        ///  · 其它阶段（如抓牌步骤）→ **排除**（`ActivateEligibility` 不亮、`ActivateAbility` 拒绝）；
        ///  · `OnlyPhases` **不影响卡牌打出**（打出侧只有 `ExcludedPhases`；JC117 的结附打出不受限）。
        /// </summary>
        public void TestAudit_JC117阶段白名单_仅在行动阶段可发动()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);

            var tmpl = LoadCard("JC117_hongyanhangban.tres");
            var e = FindEffect(tmpl, "JC117_1_MoveFriendlyToTargetArea");
            Assert.IsNotNull(e, "JC117 应有移动能力");
            var tm = game.TurnManager;

            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            Assert.IsFalse(Script.Manager.PhaseGateRule.IsExcluded(e, tm), "先手行动步骤 ⇒ 可发动（白名单内）");
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionSecond;
            Assert.IsFalse(Script.Manager.PhaseGateRule.IsExcluded(e, tm), "后手行动步骤 ⇒ 可发动（白名单内）");

            tm.CurrentPhase = global::TurnManager.TurnPhase.StartDraw;
            Assert.IsTrue(Script.Manager.PhaseGateRule.IsExcluded(e, tm), "抓牌步骤 ⇒ **排除**（仅行动阶段可发动）");
            tm.CurrentPhase = global::TurnManager.TurnPhase.EndCleanup;
            Assert.IsTrue(Script.Manager.PhaseGateRule.IsExcluded(e, tm), "结束步骤 ⇒ **排除**");

            // 打出侧不受 `OnlyPhases` 影响（卡文只限制"发动该能力"，不限制"结附于本方秘社"打出）
            tm.CurrentPhase = global::TurnManager.TurnPhase.StartDraw;
            Assert.IsFalse(Script.Manager.PhaseGateRule.IsCardPlayExcluded(tmpl, tm),
                "`OnlyPhases` 只作用于发动/激活，不影响卡牌打出");
        }

        /// <summary>
        /// 【2026-09 整体复查·修复】5 张附属卡此前缺 `TargetRules` ⇒ 沿用附属默认（允许全部大类）；
        /// 现按卡文"结附于X"收紧到单一（或本方秘社）目标大类。
        /// </summary>
        public void TestAudit_附属结附目标_按卡文收紧()
        {
            var expect = new (string File, TargetKind Kind)[]
            {
                ("JC027_huayuandijingxiang.tres", TargetKind.Area),
                ("JC048_zuzhoushalou.tres", TargetKind.Area),
                ("JC073_suzubumengwang.tres", TargetKind.Character),
                ("JC115_beiyongzijin.tres", TargetKind.Council),
                ("JC116_keertejumangzuolun.tres", TargetKind.Character),
            };
            foreach (var (file, kind) in expect)
            {
                var tmpl = LoadCard(file);
                Assert.IsNotNull(tmpl, file + " 应可加载");
                var allowed = tmpl.GetAllowedTargets();
                Assert.AreEqual(1, allowed.Count, file + " 应只允许一个目标大类（卡文『结附于…』）");
                Assert.AreEqual(kind, allowed[0], file + " 目标大类应为 " + kind);
                Assert.IsTrue(tmpl.GetRequiresTarget(), file + " 附属仍需选目标");
            }
        }

        /// <summary>
        /// 【2026-09·Pass B 修复】JC124 的"行动3"能力补行动窗口；JC094/JC122/JC123/JC124 的目标
        /// 卡文未排除自身 ⇒ 允许取自身（JC004 口径）。
        /// </summary>
        public void TestAudit_PassB_JC124行动窗口_与自身目标批()
        {
            var jc124 = LoadCard("JC124_nigulasileimei.tres");
            var e124 = FindEffect(jc124, "JC124_1_PlacePhilosopherStone");
            Assert.IsNotNull(e124, "JC124 应有『行动3：放置哲人石标志』能力");
            Assert.IsTrue(e124.ActivateWindow != null && e124.ActivateWindow.Contains("行动"),
                "卡文『行动3』⇒ `ActivateWindow` 必须为行动窗口");
            Assert.IsTrue(e124.Target.CanTargetSelf, "JC124『目标角色』未排除自身 ⇒ 允许取自身");

            var e122 = FindEffect(LoadCard("JC122_hakeyadangsi.tres"), "JC122_1_PlaceLockMarker");
            Assert.IsNotNull(e122, "JC122 应有放置锁定标记能力");
            Assert.IsTrue(e122.Target.CanTargetSelf, "JC122『目标角色或暗藏者』未排除自身 ⇒ 允许取自身");

            var e123 = FindEffect(LoadCard("JC123_yinhunzhezhudi.tres"), "JC123_1_PlaceLockMarker");
            Assert.IsNotNull(e123, "JC123 应有放置锁定标记能力");
            Assert.IsTrue(e123.Target.CanTargetSelf, "JC123『目标角色或暗藏者』未排除自身 ⇒ 允许取自身");

            var e094 = FindEffect(LoadCard("JC094_weidadetingbabadi.tres"), "JC094_1_SacrificeAllyDestroyTarget");
            Assert.IsNotNull(e094, "JC094 应有『牺牲→消灭目标』能力");
            Assert.IsTrue(e094.Target.CanTargetSelf, "JC094『消灭目标角色或暗藏者』未排除自身 ⇒ 允许取自身");
            Assert.IsTrue(e094.SacrificeCost.ExcludeSource, "代价侧『另一个角色』仍须排除自身（不变）");
        }

        /// <summary>【2026-09 修复】JC074 快速行动＝C 档；JC104 无"强制"⇒ 非强制。</summary>
        public void TestAudit_修复卡_JC074为C档_JC104非强制()
        {
            var jc074 = LoadCard("JC074_tiankongdejiaohui.tres");
            Assert.IsFalse(jc074.LimitToActionPhaseOnly, "JC074 快速行动 ⇒ **不限**行动阶段");
            bool response = false;
            foreach (var e in jc074.EffectDataList)
                if (e != null && e.PlayTiming == PlayTiming.Response) response = true;
            Assert.IsTrue(response, "JC074 应为 C 档（`PlayTiming.Response`）");

            var jc104 = LoadCard("JC104_zhanbushi.tres");
            Assert.IsFalse(AnyForced(jc104), "JC104 卡文无『强制触发』⇒ 不得有 `Forced=true`");
        }
    }
}
