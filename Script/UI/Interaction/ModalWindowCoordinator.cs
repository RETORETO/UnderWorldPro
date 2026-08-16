using Godot;
using Script.Data.Cards;
using Script.Game.Entities;
using Script.Interfaces;
using Script.UI;
using Script.Manager;
using System;
using System.Collections.Generic;

namespace Script.UI.Interaction
{
    /// <summary>
    /// 【MVC 修复 M-08】模态对抗窗口协调器。
    /// 原战斗/调查对抗窗口的打开、配置、关闭与模态锁协同散落在 Arena（约 2014–2140 行），
    /// 使 Arena 同时承担"渲染编排根"与"对抗窗口生命周期管理"双重职责。
    ///
    /// 本类统一负责：
    /// - 订阅 CombatManager.OnCombatWindowRequired，按类型路由到对应窗口（事件驱动，非 Arena 命令式转发）；
    /// - 调查窗口 / 战斗窗口的实例化、配置、模态锁、关闭回调；
    /// - 持有当前战斗窗口引用（ActiveBattleWindow），供 Arena 全局输入路由做伤害分配点击转发。
    ///
    /// Arena 仅保留：输入路由中读取 ActiveBattleWindow 做点击转发、PickBattleCardAt 命中测试。
    /// 依赖方向仍保持：UI → 接口/Manager，协调器引用 CombatManager/CardManager/PlayerActionService 均属 Controller 层，
    /// 未引入 Controller → 具体 View 的反向引用。
    /// </summary>
    public partial class ModalWindowCoordinator : Node
    {
        // ---- 由 Arena 注入的依赖 ----
        private PackedScene _investigationWindowScene;
        private PackedScene _battleWindowScene;
        private PackedScene _tieBreakWindowScene;
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();
        private CombatManager _combatManager;
        private CardManager _cardManager;
        private PlayerActionService _actionService;
        private Func<string, CardUI> _findCardUIByUID;
        private Action<ModalWindow> _lockModal;
        private Action<ModalWindow> _unlockModal;

        /// <summary>当前打开的战斗窗口（供 Arena 输入路由转发伤害分配点击）。</summary>
        public BattleWindow ActiveBattleWindow { get; private set; }

        /// <summary>装配：注入窗口预制体、地区槽位、Controller 引用、命中委托与模态锁委托。</summary>
        public void Configure(
            PackedScene investigationWindowScene,
            PackedScene battleWindowScene,
            PackedScene tieBreakWindowScene,
            List<AreaSlot> areaSlots,
            CombatManager combatManager,
            CardManager cardManager,
            PlayerActionService actionService,
            Func<string, CardUI> findCardUIByUID,
            Action<ModalWindow> lockModal,
            Action<ModalWindow> unlockModal)
        {
            _investigationWindowScene = investigationWindowScene;
            _battleWindowScene = battleWindowScene;
            _tieBreakWindowScene = tieBreakWindowScene;
            _areaSlots = areaSlots ?? new List<AreaSlot>();
            _combatManager = combatManager;
            _cardManager = cardManager;
            _actionService = actionService;
            _findCardUIByUID = findCardUIByUID;
            _lockModal = lockModal;
            _unlockModal = unlockModal;
        }

        /// <summary>订阅 CombatManager 的对抗窗口请求事件 + 先手破平确认事件（替代 Arena 此前手动订阅/转发）。</summary>
        public void BindToCombatManager(CombatManager combatManager)
        {
            _combatManager = combatManager;
            if (combatManager != null)
            {
                combatManager.OnCombatWindowRequired += OnCombatWindowRequiredHandler;
                combatManager.OnTieBreakRequired += OnTieBreakRequiredHandler;
            }
        }

        /// <summary>
        /// 退出场景树时反订阅跨局常驻的 CombatManager 事件。
        /// CombatManager 是 GameInitializer 的常驻子节点（跨局复用），而本协调器随 Arena 场景销毁；
        /// 若不在退出时反订阅，旧协调器实例的回调会残留在 CombatManager 上，重开第二局触发对抗窗口时会访问已释放节点。
        /// （订阅/反订阅成对，2026-08-16）
        /// </summary>
        public override void _ExitTree()
        {
            if (_combatManager != null)
            {
                _combatManager.OnCombatWindowRequired -= OnCombatWindowRequiredHandler;
                _combatManager.OnTieBreakRequired -= OnTieBreakRequiredHandler;
            }
            base._ExitTree();
        }

        // ============================================================
        // 先手破平确认窗口（2026-08-05）
        // ============================================================

        /// <summary>收到 CombatManager"平局可破平"请求后实例化破平确认窗口，把玩家点击结果回传逻辑层。</summary>
        private void OnTieBreakRequiredHandler(CombatManager.CombatType type, int areaNo, string areaUid, System.Action<bool> onResolved)
        {
            if (_tieBreakWindowScene == null)
            {
                GD.PrintErr("❌ [ModalCoordinator] 未注入破平窗口预制体，按取消处理");
                onResolved?.Invoke(false);
                return;
            }
            var window = _tieBreakWindowScene.Instantiate<TieBreakWindow>();
            AddChild(window);
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vpSize = GetViewport().GetVisibleRect().Size;
            window.Position = new Vector2I((int)((vpSize.X - window.Size.X) / 2), (int)(vpSize.Y / 2) + 150);
            window.Popup();
            _lockModal?.Invoke(ModalWindow.TieBreak);
            window.TreeExiting += () => _unlockModal?.Invoke(ModalWindow.TieBreak);
            window.Configure(areaNo, type, resolved =>
            {
                onResolved?.Invoke(resolved);   // true=确认破平 / false=取消
            });
            GD.Print($"【ModalCoordinator】先手破平确认窗口已打开（地区area{areaNo}，对抗{type}）");
        }

        // ============================================================
        // 对抗窗口事件路由
        // ============================================================

        /// <summary>收到 CombatManager"打开对抗窗口"请求后实例化对应 UI 窗口（将逻辑层事件转译为 UI 操作）。</summary>
        private void OnCombatWindowRequiredHandler(CombatManager.CombatType type, int winnerPlayer, int x, int areaNo, System.Action onResolved)
        {
            if (areaNo < 1 || areaNo > _areaSlots.Count)
            {
                GD.PrintErr($"❌ [ModalCoordinator] 收到非法地区编号 area{areaNo}，跳过对抗窗口");
                onResolved?.Invoke();
                return;
            }
            switch (type)
            {
                case CombatManager.CombatType.Investigation:
                    OpenInvestigationWindow(winnerPlayer, x, areaNo, onResolved);
                    break;
                case CombatManager.CombatType.Battle:
                    OpenBattleWindow(winnerPlayer, x, areaNo, onResolved);
                    break;
                default:
                    GD.PrintErr($"⚠️ [ModalCoordinator] 未知对抗类型 {type}，跳过");
                    onResolved?.Invoke();
                    break;
            }
        }

        // ============================================================
        // 调查对抗窗口
        // ============================================================

        /// <summary>打开调查对抗窗口：胜者从自己牌库顶查看 X 张，分配放回顶/底，结束后胜者抽 1 张。</summary>
        public void OpenInvestigationWindow(int winnerPlayer, int x, int areaNo, System.Action onResolved = null)
        {
            if (_investigationWindowScene == null) return;
            if (areaNo < 1 || areaNo > _areaSlots.Count)
            {
                GD.PrintErr($"❌ 无效地区编号 area{areaNo}（当前共 {_areaSlots.Count} 个）");
                onResolved?.Invoke();
                return;
            }

            // 数据层取出（脱离牌库列表，状态保持 Deck 临时态），由 ZoneManager 统一封装
            var cards = _actionService.TakeTopForInvestigation(winnerPlayer, x);

            var window = _investigationWindowScene.Instantiate<InvestigationWindow>();
            AddChild(window);
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vpSize = GetViewport().GetVisibleRect().Size;
            window.Position = new Vector2I((int)((vpSize.X - window.Size.X) / 2), (int)(vpSize.Y / 2) + 150);
            window.Popup();
            _lockModal?.Invoke(ModalWindow.Investigation);
            window.TreeExiting += () => _unlockModal?.Invoke(ModalWindow.Investigation);
            window.OnResolved = () =>
            {
                onResolved?.Invoke();   // 本区确认后通知 CombatManager 推进
            };
            // 仅回传纯数据分配结果（toTop/toBottom），牌库最终顺序由 ZoneManager 统一编排，
            // Arena 通过 OnDeckReordered 事件按序纯重绘，不再在 View 侧计算谁去顶/底。
            window.Configure(winnerPlayer, areaNo, cards, OnInvestigationConfirmed, (pid, count) => _actionService.DrawCards(pid, count));
            GD.Print($"【ModalCoordinator】调查对抗窗口已打开（胜者P{winnerPlayer}，X={x}，地区area{areaNo}）");
        }

        /// <summary>调查对抗确认回调：仅把纯数据分配结果交回 Controller（ZoneManager）。</summary>
        private void OnInvestigationConfirmed(int winnerPlayer, List<CardInstance> toTop, List<CardInstance> toBottom)
        {
            _actionService.ReturnAfterInvestigation(winnerPlayer, toTop, toBottom);
        }

        // ============================================================
        // 战斗对抗窗口
        // ============================================================

        /// <summary>打开战斗对抗窗口：败方在指定地区的角色可被点击分配 X 点伤害。</summary>
        public void OpenBattleWindow(int winnerPlayer, int x, int areaNo, System.Action onResolved = null)
        {
            if (_battleWindowScene == null) return;
            if (areaNo < 1 || areaNo > _areaSlots.Count)
            {
                GD.PrintErr($"❌ 无效地区编号 area{areaNo}（当前共 {_areaSlots.Count} 个）");
                onResolved?.Invoke();
                return;
            }
            var areaUid = _areaSlots[areaNo - 1].AreaUID;

            // 若该地区内败方没有任何可承伤对象，则不弹窗，伤害直接流失
            // 合法目标判定交由 CombatManager（规则层）
            if (_combatManager != null && _combatManager.CountLegalBattleTargets(winnerPlayer, areaUid) == 0)
            {
                int loser = winnerPlayer == 1 ? 2 : 1;
                GD.Print($"【ModalCoordinator】地区 area{areaNo} 内玩家P{loser} 无可分配伤害的对象，{x} 点伤害流失，跳过战斗对抗窗口");
                onResolved?.Invoke();
                return;
            }

            var window = _battleWindowScene.Instantiate<BattleWindow>();
            AddChild(window);
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vpSize = GetViewport().GetVisibleRect().Size;
            window.Position = new Vector2I((int)((vpSize.X - window.Size.X) / 2), (int)(vpSize.Y / 2) + 150);
            window.Popup();
            _lockModal?.Invoke(ModalWindow.Battle);
            window.TreeExiting += () => _unlockModal?.Invoke(ModalWindow.Battle);
            window.Configure(winnerPlayer, x, areaUid, areaNo);
            window.FindCardUI = (inst) => _findCardUIByUID?.Invoke(inst.UID);
            window.CardManager = _cardManager;   // 注入伤害结算入口，避免窗口直接改 Model
            window.CombatManager = _combatManager;   // 注入合法目标判定（规则层）
            window.OnResolved = () =>
            {
                ActiveBattleWindow = null;
                _onBattleResolved?.Invoke();
                onResolved?.Invoke();   // 本区结算完成，通知 CombatManager 推进
            };
            ActiveBattleWindow = window;
            window.CloseRequested += () => { ActiveBattleWindow = null; };
            GD.Print($"【ModalCoordinator】战斗对抗窗口已打开（胜者P{winnerPlayer}，X={x}，地区 {areaUid}）");
        }

        // ---- 战斗结算后的 UI 刷新钩子（由 Arena 注入：刷新伤害显示 + 全局统计）----
        private Action _onBattleResolved;
        public void SetBattleResolvedCallback(Action onResolved) => _onBattleResolved = onResolved;
    }
}
