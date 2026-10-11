using System.Collections.Generic;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【判据㉖–㉙ 的共享测试桩】各 JC 测试套件共用的确定性桩：
    ///  · `EnterAllTriggerView`：触发全部按默认顺序入堆栈；取目标取第一个候选；
    ///  · `RecordingTriggerView`：**记录**触发取目标的真实候选集合（用于断言候选范围/旁路同判）；
    ///  · `ConfirmStub`：`IGameUIHooks` 桩（数值窗 / 是-否窗 / 模式窗 / 选卡都用同一套答案，且**记录调用**）；
    ///  · `AutoAnswerGateway`：`IInputGateway` 桩（联机路径：模式/目标/数值/二选一自动应答并记录请求）；
    ///  · `InstantCombatWindow`：对抗窗口桩（立即"关闭窗口"，用于验证"窗口关闭后才广播"）。
    ///
    /// 说明：这些桩原先散落在 `JCBugfixBatchTests` / `P3PendingEffectTests` 等套件里各写一份，
    /// 新增检查时容易口径漂移；集中一处后，"判定 × 路径"矩阵可复用同一构造与同一答案策略。
    /// </summary>
    internal sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
    {
        public void OpenTriggerOrderForPlayer(int ownerPlayerId,
            List<StackEffectInstance> triggers,
            System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
        {
            onEnterStack?.Invoke(triggers);
            onDone?.Invoke();
        }

        public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
            System.Action<string> onPicked)
            => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

        public void RemoveOrderEntries(List<StackEffectInstance> pushed) { }
    }

    /// <summary>触发视图：记录"触发取目标"的**真实候选集合**，并选第一个（用于断言候选范围）。</summary>
    internal sealed class RecordingTriggerView : Script.Interfaces.ITriggerView
    {
        /// <summary>最近一次取目标请求的候选（UID）与显示名（便于失败信息可读）。</summary>
        public readonly List<string> Candidates = new List<string>();
        public readonly List<string> DisplayNames = new List<string>();
        /// <summary>当前对局（取卡名用）；由用例在驱动前赋值。</summary>
        public static GameInitializer LastGame = null;
        /// <summary>若该 UID 在候选里则**优先选它**（用于"先把目标指定给某张卡"的场景，如"先指定、后获屏障"）。</summary>
        public string PreferredUid = null;

        public void OpenTriggerOrderForPlayer(int ownerPlayerId,
            List<StackEffectInstance> triggers,
            System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
        {
            onEnterStack?.Invoke(triggers);
            onDone?.Invoke();
        }

        public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
            System.Action<string> onPicked)
        {
            Candidates.Clear();
            DisplayNames.Clear();
            if (validTargetUids != null)
            {
                foreach (var uid in validTargetUids)
                {
                    Candidates.Add(uid);
                    DisplayNames.Add(LastGame?.CardManager?.GetInstance(uid)?.PlayerTemplate?.CardName ?? uid);
                }
            }
            onPicked?.Invoke(Candidates.Count > 0
                ? (PreferredUid != null && Candidates.Contains(PreferredUid) ? PreferredUid : Candidates[0])
                : null);
        }

        public void RemoveOrderEntries(List<StackEffectInstance> pushed) { }
    }

    /// <summary>
    /// `IGameUIHooks` 桩：所有窗口都给**确定性答案**并记录调用次数/范围
    /// （数值窗记 `LastMin/LastMax`，模式窗记 `LastModeCount`），用于断言"是否弹窗/窗口形态"。
    /// </summary>
    internal sealed class ConfirmStub : Script.Interfaces.IGameUIHooks
    {
        /// <summary>是/否窗的答案（默认"是"）。</summary>
        public bool Answer = true;
        /// <summary>是/否窗被调用的次数。</summary>
        public int Calls;
        /// <summary>数值窗的答案（默认 0）与被调用次数/范围。</summary>
        public int NumberAnswer = 0;
        public int NumberCalls;
        public int LastMin = -1, LastMax = -1;
        /// <summary>模式窗选择的序号（默认第 1 项）与被调用次数/选项数。</summary>
        public int ModeChoice = 0;
        public int ModePrompts;
        public int LastModeCount = -1;

        public void PromptTwoChoice(int chooserPlayerId, string prompt, string yesLabel, string noLabel,
            System.Action<bool> onChosen)
        {
            Calls++;
            onChosen?.Invoke(Answer);
        }

        public void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
        {
            Calls++;
            onChosen?.Invoke(Answer);
        }

        public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
            string confirmLabel, string cancelLabel, System.Action<int> onChosen)
        {
            NumberCalls++;
            LastMin = min;
            LastMax = max;
            onChosen?.Invoke(NumberAnswer);
        }

        public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
            => PromptEffectModeWithAvailability(source, data, null, onChosen);

        public void PromptEffectModeWithAvailability(CardInstance source, EffectData data,
            List<bool> availability, System.Action<int> onChosen)
        {
            ModePrompts++;
            LastModeCount = data?.Modes?.Count ?? -1;
            onChosen?.Invoke(ModeChoice);
        }

        public void PromptPayOrTap(CardInstance source, int payAmount) { }

        public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
            System.Action<CardInstance> onPicked)
            => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);

        public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
            bool allowCancel, System.Action<CardInstance> onPicked)
            => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);

        public void PromptCurbMarks(int curbedPlayerId, int remaining,
            List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }

        public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
            System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }

        public void PromptMultiTargets(CardInstance source, EffectData data,
            System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
            System.Action<List<TargetChoice>> onConfirmed) { }

        public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
            System.Action<int> onChosen)
            => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
    }

    /// <summary>
    /// 联机网关桩：按请求类型给出确定性回包并**记录全部请求**（用于联机路径断言）。
    ///  · `SelectTarget`/`SelectZoneCard`/`SelectHandCard`/`SelectDeckCard`/`ForceChoice` ⇒ 第一个 `OptionUids`
    ///    （`ForceChoice` 的"是/否"哨兵由 `ForceChoiceYes`/`ForceChoiceNo` 控制）；
    ///  · `SelectEffectMode` ⇒ `ModeChoice`；
    ///  · `NumberInput` ⇒ `NumberAnswer`；
    ///  · `SelectPlayer` ⇒ 第一个候选；
    ///  · `SelectTriggerOrder` ⇒ 全部条目（都入堆叠）；
    ///  · `ConfirmTargets` ⇒ 确认。
    /// </summary>
    internal sealed class AutoAnswerGateway : Script.Net.IInputGateway
    {
        public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
        public int ModeChoice = 0;
        public int NumberAnswer = 0;
        /// <summary>`ForceChoice` 的回包：默认选第一个（`false` ⇒ 选第二个）。</summary>
        public bool ForceChoiceFirst = true;

        public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId,
            Script.Net.ChoiceRequest req)
        {
            Requests.Add(req);
            var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
            switch (req?.RequestKind)
            {
                case Script.Net.ChoiceRequest.Kind.SelectEffectMode:
                    r.SelectedIndices.Add(ModeChoice);
                    break;
                case Script.Net.ChoiceRequest.Kind.NumberInput:
                    r.SelectedAmounts.Add(NumberAnswer);
                    break;
                case Script.Net.ChoiceRequest.Kind.SelectPlayer:
                    r.SelectedPlayerId = req.OptionPlayerIds != null && req.OptionPlayerIds.Count > 0
                        ? req.OptionPlayerIds[0] : -1;
                    break;
                case Script.Net.ChoiceRequest.Kind.SelectTriggerOrder:
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                    break;
                case Script.Net.ChoiceRequest.Kind.ConfirmTargets:
                    r.Confirmed = true;
                    break;
                case Script.Net.ChoiceRequest.Kind.PayOrTap:
                    r.Pay = ForceChoiceFirst;
                    break;
                default:
                    if (req?.OptionUids != null && req.OptionUids.Count > 0)
                        r.SelectedUid = ForceChoiceFirst ? req.OptionUids[0] : req.OptionUids[req.OptionUids.Count - 1];
                    break;
            }
            return System.Threading.Tasks.Task.FromResult(r);
        }
    }

    /// <summary>对抗窗口桩：立即"关闭窗口"（模拟 View 处理完毕），用于验证"窗口关闭后才广播"。</summary>
    internal sealed class InstantCombatWindow
    {
        private readonly GameInitializer _game;
        public int Calls;

        public InstantCombatWindow(GameInitializer game)
        {
            _game = game;
            _game.CombatManager.OnCombatWindowRequired += OnWindow;
        }

        private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo,
            System.Action onResolved)
        {
            Calls++;
            onResolved?.Invoke();
        }

        public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
    }
}
