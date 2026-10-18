using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Script.Manager;

/// <summary>
/// Arena 的"视觉排版 / 卡牌归位"子系统（布局引擎）。
/// 职责：把逻辑层的卡牌状态（在哪个 Zone、是否横置、有无附属/封印）翻译成
/// Godot 场景树上的物理挂载关系与位置/旋转/ZIndex。
/// 与 Arena 主文件共享同一 partial 类（全局命名空间），不新增任何 internal 访问器。
/// </summary>
public partial class Arena
{
    // ============================================================
    // 常量
    // ============================================================
    private const float CARD_WIDTH = 100;
    private const float CARD_HEIGHT = 140;
    private const float HAND_SPACING = 20;

    // 场上/手牌/资产卡 ZIndex 基准：高于争夺区背景槽位(AreaSlot)，避免被背景遮挡
    // 【2026-10】层号统一收口到 Script.UI.Layers（禁止再写魔数；新增 UI 请从 Layers 取层号）
    private const int ZINDEX_CARD_BASE = Layers.CardBase;

    // ============================================================
    // 分流辅助方法（双玩家双容器的查询收口）
    // ============================================================
    // View 不再直接比较 Model 字段做布局分流，将来可平滑替换为
    // 按 playerId 索引的字典或 ViewModel 驱动。
    // ============================================================
    internal Control GetHandContainer(int ownerId) => ownerId == 2 ? _handContainerRef2 : _handContainerRef;   // M-08：提升 internal 供 CardDragController 访问
    private List<CardUI> GetHandList(int ownerId) => ownerId == 2 ? _handCardUIs2 : _handCardUIs;
    private DeckZone GetDeckZone(int ownerId) => ownerId == 2 ? _deckZone2 : _deckZone;
    private GraveyardZone GetGraveyardZone(int ownerId) => ownerId == 2 ? _graveyardZone2 : _graveyardZone;

    // ============================================================
    // 触发排序 / 选目标交互（MVC 修复 M-08）
    // 原逻辑已迁移至 TriggerInteractionCoordinator（实现 ITriggerView），
    // Arena 仅在输入路由中转发选目标点击到协调器。
    // ============================================================

    private Control GetScoreContainer(int ownerId) => ownerId == 2 ? _scoreContainer2 : _scoreContainer1;
    private Control GetAssetContainer(int ownerId) => ownerId == 2 ? _assetContainer2 : _assetContainer1;
    private Control GetAreaContainer(AreaSlot slot, int ownerId)
        => ownerId == 2 ? slot.Player2Area : slot.Player1Area;

    // ============================================================
    // 排列卡牌
    // ============================================================
    /// <summary>
    /// 从列表中移除已被 QueueFree 的 CardUI 残留引用。Godot 节点释放后引用仍非 null，
    /// 但 IsInstanceValid==false，访问其属性/方法会抛 ObjectDisposedException。
    /// </summary>
    private static void RemoveDestroyed(List<CardUI> list)
    {
        if (list == null) return;
        list.RemoveAll(c => c == null || !GodotObject.IsInstanceValid(c));
    }

    /// <summary>
    /// 从字典中移除已被 QueueFree 的 CardUI 残留引用。
    /// </summary>
    private void RemoveDestroyedFromCardUIMap()
    {
        if (_cardUIMap == null) return;
        var deadKeys = _cardUIMap
            .Where(kv => kv.Value == null || !GodotObject.IsInstanceValid(kv.Value))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in deadKeys)
            _cardUIMap.Remove(key);
    }

    private void ArrangeAllCards()
    {
        // 赢地区等路径会 QueueFree AreaCardUI 但可能未从缓存列表移除，先清理避免 ObjectDisposedException。
        RemoveDestroyed(_handCardUIs);
        RemoveDestroyed(_handCardUIs2);
        RemoveDestroyed(_areaCardUIs);
        RemoveDestroyed(_areaAttachSealUIs);
        RemoveDestroyedFromCardUIMap();

        ArrangeHandCards(_handContainerRef, _handCardUIs);
        ArrangeHandCards(_handContainerRef2, _handCardUIs2);
        ArrangeAreaCards();
        // 节点释放竞态：容器/区节点可能已 QueueFree（引用非 null 但 Godot 对象已 disposed），
        // 用 IsInstanceValid 判定，避免 MoveChild/GetChildren 抛 ObjectDisposedException。
        if (GodotObject.IsInstanceValid(_deckZone)) _deckZone.ArrangeCards();
        if (GodotObject.IsInstanceValid(_deckZone2)) _deckZone2.ArrangeCards();
        if (GodotObject.IsInstanceValid(_graveyardZone)) _graveyardZone.ArrangeCards();
        if (GodotObject.IsInstanceValid(_graveyardZone2)) _graveyardZone2.ArrangeCards();
        if (GodotObject.IsInstanceValid(_scoreContainer1)) ArrangeContainerCards(_scoreContainer1);
        if (GodotObject.IsInstanceValid(_scoreContainer2)) ArrangeContainerCards(_scoreContainer2);
        if (GodotObject.IsInstanceValid(_assetContainer1)) ArrangeContainerCards(_assetContainer1);
        if (GodotObject.IsInstanceValid(_assetContainer2)) ArrangeContainerCards(_assetContainer2);

        // 争夺区内：角色 + 附属于本地区 + 封印于本地区，统一均匀平铺（按进入先后，右后压左前）
        // 【2026-10-16】此为 cat-1 的落地路径，`UI美化与空间重排方案.md` 决策 3 明确"不折叠、保持不动"。
        ArrangeAreaAttachmentsAndSeals();
        // 争夺区内结附卡（附属挂在目标同容器）的偏移布局：必须在角色排布后执行
        // 【2026-10-16】下面两个偏移调用只服务 cat-2（结附/封印于**卡或秘社**），
        // 将随决策 3 改为宿主卡角标；cat-1 的卡已由上一行平铺处理完毕，不在其中。
        ArrangeAllAttachments();
        // 封印卡偏移布局：封印于角色时 Y 向上偏移；封印于地区时不偏移（挂地区容器）
        ArrangeAllSeals();
    }

    /// <summary>
    /// 【2026-09 修复·用户实测"附属 UI 部分情况下显示在宿主卡上方"】
    /// 取宿主 UI 在**其容器本地坐标**下的**真实视觉中心**与**视觉半高**（兼容宿主横置）。
    ///
    /// 原实现用 `Position + PivotOffset.Rotated(角度)` 估算视觉中心 —— **错**：Godot 的旋转是**绕 PivotOffset**
    /// 进行的，旋转后视觉中心**仍在** `Position + PivotOffset`（实测 `GetGlobalTransform()` 四角 AABB：
    /// `Position=(0,0)` / `PivotOffset=(50,70)` / `RotationDegrees=90` → 视觉中心 **(50,70)**，
    /// 而 `PivotOffset.Rotated(90°)` 给出 (-70,50)，误差 **(120,20)**）。
    /// 于是**宿主一旦横置**，附属/封印就被摆到宿主的左上方（未横置时公式恰好等价，故表现为"部分情况"）。
    /// 视觉半高：横置卡视觉高＝卡宽 ⇒ `CARD_WIDTH/2`（这一点原实现是对的）。
    /// </summary>
    private static void HostVisualCenter(Control hostUi, bool hostTapped,
        out float centerX, out float centerY, out float visHalfH)
    {
        centerX = hostUi.Position.X + hostUi.PivotOffset.X;
        centerY = hostUi.Position.Y + hostUi.PivotOffset.Y;
        visHalfH = hostTapped ? CARD_WIDTH / 2f : CARD_HEIGHT / 2f;
    }

    /// <summary>
    /// 重排所有结附关系：对每个带有附属的目标卡，将其附属 UI（挂在同一 PlayerXArea 容器内）
    /// 按 X 对齐目标卡视觉中心、Y 从目标卡视觉下缘向上露出 2/5 张起、多张依次下延；
    /// 正确处理横置卡（旋转 90° 后视觉下缘变为原右侧），目标卡 ZIndex 最高，
    /// 附属越靠后 ZIndex 越低（视觉上越远）。
    /// 【2026-10-16 计划·未实施】按 `UI美化与空间重排方案.md` 决策 3，本函数**只服务 cat-2**
    /// （结附于卡 / 结附于秘社）；cat-2 将改为**宿主卡角标**（角标层 380~399），届时本偏移算法相应简化。
    /// **cat-1（结附于地区）不走本函数**（走 `ArrangeAreaAttachmentsAndSeals` 平铺），不受影响。
    /// 另：A71（`视觉中心 = Position + PivotOffset`）在未折叠/展开态仍须成立，勿因简化而删断言。
    /// </summary>
    private void ArrangeAllAttachments()
    {
        foreach (var targetUI in _areaCardUIs)
        {
            if (targetUI == null || !GodotObject.IsInstanceValid(targetUI)) continue;
            var targetInst = targetUI?.Instance;
            if (targetInst == null || targetInst.AttachedCards == null || targetInst.AttachedCards.Count == 0)
                continue;

            var host = targetUI.GetParent() as Control;
            if (host == null) continue;

            // 目标卡真实视觉中心与视觉下缘（本地坐标，兼容横置；见 HostVisualCenter 注释）
            HostVisualCenter(targetUI, targetUI.IsTapped, out float centerX, out float centerY, out float visHalfH);
            float bottomY = centerY + visHalfH;

            for (int i = 0; i < targetInst.AttachedCards.Count; i++)
            {
                var attInst = targetInst.AttachedCards[i];
                var attUI = FindCardUIByUID(attInst.UID);
                if (attUI == null || attUI.GetParent() != host) continue;

                // 附属自身可能横置：同步旋转与 Pivot（附属不走 PlaceCardInContainer，需在此显式维护）
                // 注意：布局上统一按未横置占位计算，使横置附属仍竖直堆叠在目标下方，
                // 仅卡牌本身旋转 90°，避免视觉尺寸变化导致附属飞到目标右侧。
                attUI.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
                attUI.RotationDegrees = attUI.IsTapped ? 90f : 0f;

                // X 对齐目标视觉中心；Y 从目标视觉下缘向上压住 3/5（露出 2/5=56px）起依次下延 2/5 张
                float attTopLeftX = centerX - CARD_WIDTH / 2f;
                float attTopLeftY = bottomY - CARD_HEIGHT + (i + 1) * (CARD_HEIGHT * 0.4f);
                attUI.Position = new Vector2(attTopLeftX, attTopLeftY);
                // 同步平滑目标（避免 _Process 把附属拉回旧位置）
                attUI.TargetPosition = attUI.GlobalPosition;
                // 目标卡 ZIndex 最高，附属依次递减（越后越靠下越远）
                attUI.ZIndex = targetUI.ZIndex - (i + 1);
            }
        }
    }

    /// <summary>
    /// 【秘社批次】重排**封印在秘社上**的牌（MSJC010 貘组 / MSJC016 颂亡者）。
    /// 与 `ArrangeCouncilAttachments`（附属，向下）对称：封印牌 X 对齐秘社视觉中心、
    /// Y 从秘社视觉**上缘向上**露出 2/5 张起、多张依次再上延 2/5 张；ZIndex 比秘社低但高于其它卡。
    /// 必须在 `ArrangeCouncilCards`（已排好秘社 UI.Position）之后调用。
    /// 【2026-10-16 计划·未实施】属 cat-2，将随 `ArrangeAllAttachments` 一并改为秘社卡角标（决策 3）。
    /// </summary>
    private void ArrangeCouncilSeals()
    {
        for (int playerId = 1; playerId <= 2; playerId++)
        {
            var councilInst = GetCouncilInstance(playerId);
            if (councilInst?.SealedCards == null || councilInst.SealedCards.Count == 0) continue;
            var councilUI = FindCouncilUIByUID(councilInst.UID);
            if (councilUI == null || !GodotObject.IsInstanceValid(councilUI)) continue;
            var host = councilUI.GetParent() as Control;
            if (host == null) continue;

            // 秘社真实视觉中心（兼容横置，见 HostVisualCenter）
            HostVisualCenter(councilUI, councilUI.IsTapped, out float centerX, out float centerY, out float visHalfH);
            float topY = centerY - visHalfH;

            int i = 0;
            foreach (var sealInst in councilInst.SealedCards)
            {
                // 只处理"封印于本秘社"的封印牌（SealedBy 指向秘社），与 ArrangeAllSeals 同款护栏。
                if (sealInst == null || !ReferenceEquals(sealInst.SealedBy, councilInst)) continue;
                var sealUI = FindCardUIByUID(sealInst.UID);
                if (sealUI == null || sealUI.GetParent() != host) continue;

                sealUI.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
                sealUI.RotationDegrees = sealUI.IsTapped ? 90f : 0f;
                float sealTopLeftX = centerX - CARD_WIDTH / 2f;
                float sealTopLeftY = topY - CARD_HEIGHT + (i + 1) * (CARD_HEIGHT * 0.4f);
                sealUI.Position = new Vector2(sealTopLeftX, sealTopLeftY);
                sealUI.TargetPosition = sealUI.GlobalPosition;
                sealUI.ZIndex = councilUI.ZIndex - 1 - i;
                i++;
            }
        }
    }

    /// <summary>
    /// 重排结附于秘社的附属 UI：与 ArrangeAllAttachments（结附于角色的附属）完全对称实现。
    /// 差异仅在于：宿主 UI 是 CouncilCardUI（非 CardUI 子类），故用 FindCouncilUIByUID 而非 FindCardUIByUID。
    /// 偏移逻辑一致：X 对齐宿主视觉中心、Y 从宿主视觉下缘向下露 3/5 起、多张依次下延 2/5 张、ZIndex 递减。
    /// 必须在 ArrangeCouncilCards（已排好秘社 UI.Position）之后调用。
    /// 【2026-10-16 计划·未实施】属 cat-2，将随 `ArrangeAllAttachments` 一并改为**秘社卡角标**（决策 3）。
    /// </summary>
    private void ArrangeCouncilAttachments()
    {
        for (int playerId = 1; playerId <= 2; playerId++)
        {
            var councilInst = GetCouncilInstance(playerId);
            if (councilInst == null || councilInst.AttachedCards == null || councilInst.AttachedCards.Count == 0)
                continue;
            var councilUI = FindCouncilUIByUID(councilInst.UID);
            if (councilUI == null || !GodotObject.IsInstanceValid(councilUI))
                continue;
            var host = councilUI.GetParent() as Control;
            if (host == null)
                continue;

            // 与 ArrangeAllAttachments 完全对称：用宿主真实视觉中心（兼容横置，见 HostVisualCenter）
            HostVisualCenter(councilUI, councilUI.IsTapped, out float centerX, out float centerY, out float visHalfH);
            float bottomY = centerY + visHalfH;

            for (int i = 0; i < councilInst.AttachedCards.Count; i++)
            {
                var attInst = councilInst.AttachedCards[i];
                var attUI = FindCardUIByUID(attInst.UID);
                if (attUI == null)
                    continue;
                if (attUI.GetParent() != host)
                    continue;

                // 附属自身可能横置：同步旋转与 Pivot（与角色附属完全一致）
                attUI.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
                attUI.RotationDegrees = attUI.IsTapped ? 90f : 0f;

                // X 对齐宿主视觉中心；Y 从宿主视觉下缘向上压住 3/5（露出 2/5=56px）起依次下延 2/5 张
                float attTopLeftX = centerX - CARD_WIDTH / 2f;
                float attTopLeftY = bottomY - CARD_HEIGHT + (i + 1) * (CARD_HEIGHT * 0.4f);
                attUI.Position = new Vector2(attTopLeftX, attTopLeftY);
                // 同步平滑目标（避免 _Process 把附属拉回旧位置）
                attUI.TargetPosition = attUI.GlobalPosition;
                // 宿主卡 ZIndex 最高，附属依次递减（越后越靠下越远）
                attUI.ZIndex = councilUI.ZIndex - (i + 1);
            }
        }
    }

    /// <summary>
    /// 重排所有封印关系：对每个被封印的宿主卡（SealedCards 非空，且封印卡 SealedBy 指向宿主，
    /// 即"封印于角色"），将其封印卡 UI（挂在同一 PlayerXArea 容器内）按 X 对齐目标视觉中心、
    /// Y 从目标视觉【上缘】向上露出 2/5 张起、多张依次上延；与附属（向下）对称。
    /// 封印卡 ZIndex 低于目标卡（目标卡层级最高），但高于其他卡，保证封印卡压在目标上方可见。
    /// 注：封印于地区（SealedBy==null 且 ZoneDetailId 非空）的封印卡由 SyncCardUIToZone 挂到地区容器、
    /// 不参与偏移布局（同 attacharea 行为），此处不处理。
    /// 【2026-10-16 计划·未实施】本函数处理的是 cat-2（封印于角色/秘社/附属），将改为宿主卡角标（决策 3）；
    /// **cat-1（封印于地区）本来就不进本函数**（走地区容器平铺），故其"可见可点"契约不受影响。
    /// </summary>
    private void ArrangeAllSeals()
    {
        foreach (var hostUI in _areaCardUIs)
        {
            if (hostUI == null || !GodotObject.IsInstanceValid(hostUI)) continue;
            var hostInst = hostUI?.Instance;
            if (hostInst == null || hostInst.SealedCards == null || hostInst.SealedCards.Count == 0)
                continue;

            var container = hostUI.GetParent() as Control;
            if (container == null) continue;

            // 宿主真实视觉中心与视觉上缘（兼容横置，见 HostVisualCenter）
            HostVisualCenter(hostUI, hostUI.IsTapped, out float centerX, out float centerY, out float visHalfH);
            float topY = centerY - visHalfH;   // 目标视觉上缘（向上偏移基准）

            // 仅处理"封印于角色"的封印卡（SealedBy == hostInst）
            var sealsOnRole = hostInst.SealedCards.Where(s => s.SealedBy == hostInst).ToList();
            for (int i = 0; i < sealsOnRole.Count; i++)
            {
                var sealInst = sealsOnRole[i];
                var sealUI = FindCardUIByUID(sealInst.UID);
                if (sealUI == null || sealUI.GetParent() != container) continue;

                // 封印卡自身可能横置：同步旋转与 Pivot（与附属一致）
                sealUI.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
                sealUI.RotationDegrees = sealUI.IsTapped ? 90f : 0f;

                // X 对齐目标视觉中心；Y 从目标视觉上缘向上露出 2/5 张（与附属向下露出 2/5 对称），
                // 多张依次再上移 2/5 张（i*0.4），避免整张卡飞到宿主上方过远。
                float sealTopLeftX = centerX - CARD_WIDTH / 2f;
                float sealTopLeftY = topY - CARD_HEIGHT + (i + 1) * (CARD_HEIGHT * 0.4f);
                sealUI.Position = new Vector2(sealTopLeftX, sealTopLeftY);
                sealUI.TargetPosition = sealUI.GlobalPosition;
                sealUI.ZIndex = hostUI.ZIndex - 1 - i;   // 目标层最高，封印卡在其上（层低于目标）
            }
        }

        // 议会（秘社）卡排版：按 Instance.IsTapped 旋转（支持横置）。
        // 每个 CouncilContainer 通常仅 1 张秘社卡，居中放置。
        ArrangeCouncilCards(_councilContainer1);
        ArrangeCouncilCards(_councilContainer2);
        // 结附于秘社的附属：须在秘社排版（Position 已就绪）之后排布，否则坐标错乱偏移远处。
        ArrangeCouncilAttachments();
        // 【秘社批次】封印在秘社上的牌（MSJC010/016 的"封印在貘组/颂亡者上"）：_areaCardUIs 不含秘社，
        // 故 ArrangeAllSeals 覆盖不到，必须与"结附于秘社"同款单独排布，否则会与附属叠在同一位置。
        ArrangeCouncilSeals();
    }

    private void ArrangeContainerCards(Control container)
    {
        if (container == null) return;
        var cards = new System.Collections.Generic.List<CardUI>();
        foreach (var child in container.GetChildren())
            if (child is CardUI c) cards.Add(c);
        LayoutCardsEvenly(container, cards);
    }

    private void ArrangeHandCards(Control container, List<CardUI> cards)
    {
        // 过滤已释放节点
        cards.RemoveAll(c => c == null || !GodotObject.IsInstanceValid(c));
        if (cards.Count == 0) return;

        // 凡进入手牌排列列表的卡，强制允许拖拽（兜底，确保手牌必定可拖）
        foreach (var c in cards)
            if (c != null) c.CanDrag = true;

        LayoutCardsEvenly(container, cards);

        // GD.Print($"[Arena.ArrangeHand] {cards.Count} 张手牌");
    }

    private void ArrangeAreaCards()
    {
        var groups = new Dictionary<Control, List<CardUI>>();
        foreach (var card in _areaCardUIs)
        {
            if (card == null || !GodotObject.IsInstanceValid(card)) continue;
            var parent = card.GetParent() as Control;
            if (parent == null) continue;
            if (!groups.ContainsKey(parent))
                groups[parent] = new List<CardUI>();
            groups[parent].Add(card);
        }

        // 横向均匀排布（与手牌区一致）：放得下居中，放不下重叠，右卡压左卡
        // 【2026-10-16】不再声称"与资产区一致"：资产区/分数区按 `UI美化与空间重排方案.md` 决策 4
        // 将改为**层叠**（+ hover 展开），争夺区自身的平铺口径不变。
        foreach (var kv in groups)
            LayoutCardsEvenly(kv.Key, kv.Value);
    }

    /// <summary>
    /// 把每个争夺区内"角色 + 附属于本地区 + 封印于本地区"的所有卡按进入地区先后（index 递增、后进在右、
    /// 右卡压左卡）合并成一个统一序列，与手牌区一样的均匀平铺（LayoutCardsEvenly）。
    /// 这样地区内的角色、地区附属、地区封印三类卡视觉上对齐成一行，不再错位/重叠/不动。
    /// 【2026-10-16】本函数是选卡方案 **cat-1 的落地保障**——"结附/封印于地区"的卡必须保持场上可见可点；
    /// `UI美化与空间重排方案.md` 决策 3 明确"cat-1 一律不折叠"，故本函数**保持不动**，
    /// 勿随角标改造（cat-2）一并简化。
    /// </summary>
    private void ArrangeAreaAttachmentsAndSeals()
    {
        // 按地区容器分组，容器内部统一排布
        var roleGroups = new Dictionary<Control, List<Control>>();
        var attachGroups = new Dictionary<Control, List<Control>>();
        // 角色卡（_areaCardUIs）按原顺序加入；地区附属/封印（_areaAttachSealUIs）按进入地区先后追加。
        // 两者合并后即为"统一平铺序列"，index 递增决定左右与 ZIndex 上下层。
        // 【2026-10】合并与平铺统一走 CardLayoutHelper.LayoutAreaRow（单机/联机唯一实现）。
        // 注意：角色侧**不做任何 IsAttached/IsSeal 过滤** —— 形态角色（如 JC027 变角色后仍保持附属身份）
        // 已进入争夺区桶，必须参与统一平铺，否则会停留旧位并与地区附属重叠。
        foreach (var card in _areaCardUIs)
        {
            if (card == null || !GodotObject.IsInstanceValid(card)) continue;
            var parent = card.GetParent() as Control;
            if (parent == null) continue;
            if (!roleGroups.ContainsKey(parent)) roleGroups[parent] = new List<Control>();
            roleGroups[parent].Add(card);
        }
        foreach (var card in _areaAttachSealUIs)
        {
            if (card == null || !GodotObject.IsInstanceValid(card)) continue;
            var parent = card.GetParent() as Control;
            if (parent == null) continue;
            if (!attachGroups.ContainsKey(parent)) attachGroups[parent] = new List<Control>();
            attachGroups[parent].Add(card);
        }

        // 对"有角色 或 有地区附属/封印"的容器全部重排一次，保证 index/ZIndex 与联机完全一致
        var containers = new HashSet<Control>(roleGroups.Keys);
        foreach (var k in attachGroups.Keys) containers.Add(k);
        foreach (var container in containers)
        {
            roleGroups.TryGetValue(container, out var roles);
            attachGroups.TryGetValue(container, out var attachSeals);
            CardLayoutHelper.LayoutAreaRow(container, roles, attachSeals);
        }
    }

    /// <summary>
    /// 排列所有 AreaSlot：
    ///   放得下 → 按固定间距整体居中；
    ///   放不下 → 压缩步长使整体宽度恰好等于父容器宽度（相邻 slot 重叠），绝不超出父容器；
    ///   重叠时右侧地区压在左侧地区上方（ZIndex 递增）。
    /// </summary>
    private void ArrangeAreas()
    {
        if (_areaSlots.Count == 0) return;

        float containerWidth = _areasContainer.Size.X;
        if (containerWidth <= 0) containerWidth = 1920;

        int count = _areaSlots.Count;
        // 与 AreaSlot.tscn 设计尺寸一致(400x580),避免 slot 被压扁导致内部 Player1/2Area 比例错位
        const float slotWidth = 400f;
        const float slotHeight = 580f;
        const float spacing = 30f;

        float startX;
        float step;   // 相邻 slot 左边缘之间的距离

        if (count == 1)
        {
            startX = Math.Max(0f, (containerWidth - slotWidth) / 2f);
            step = 0f;
        }
        else
        {
            float idealTotal = count * slotWidth + (count - 1) * spacing;
            if (idealTotal <= containerWidth)
            {
                // 放得下：整体居中，固定间距
                startX = (containerWidth - idealTotal) / 2f;
                step = slotWidth + spacing;
            }
            else
            {
                // 放不下：压缩步长，使最后一张右边缘恰好贴住容器右边缘（重叠）
                startX = 0f;
                step = (containerWidth - slotWidth) / (count - 1f);
                if (step < 0f) step = 0f;
            }
        }

        for (int i = 0; i < count; i++)
        {
            var slot = _areaSlots[i];
            slot.Position = new Vector2(startX + i * step, 0);
            slot.Size = new Vector2(slotWidth, slotHeight);
            slot.AnchorLeft = 0; slot.AnchorRight = 0;
            slot.AnchorTop = 0; slot.AnchorBottom = 0;
            slot.ZIndex = Layers.AreaSlotBase + i;   // 右侧地区压在左侧地区上方（基准层由 Layers 统一分配）
            slot.UpdateLayout();
        }
    }

    /// <summary>【2026-10】附属"先移动后绑定"窗口期的延迟重放抑制标志（见 `SyncCardUIToZone` 头注释）。
    /// 只用于本次延迟重放，避免绑定仍未建立时反复自我延迟（每张卡最多重放一次）。</summary>
    private bool _suppressAttachDefer;

    /// <summary>【2026-10】把"附属区 + 尚未绑定"的卡延迟重放一次归位（`AttachAndRegister` 完成绑定后调用）。</summary>
    private void ResyncAttachedCardUI(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        // 防御：延迟回调可能落在换局/退出之后（节点已释放）——此处直接放弃，避免访问已释放对象。
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
        var inst = _cardManager?.GetInstance(uid);
        if (inst == null) return;
        _suppressAttachDefer = true;
        try { SyncCardUIToZone(inst, inst.CurrentZone, inst.ControllerId, inst.ZoneDetailId); }
        finally { _suppressAttachDefer = false; }
    }

    /// <summary>
    /// 根据卡牌的逻辑区域，把对应 CardUI 重新挂载到正确的 UI 容器。
    /// 这样通过 ZoneManager 的移动（如 PM 的 move 命令、出牌、死亡等）都能自动驱动视觉归位。
    /// </summary>
    private void SyncCardUIToZone(CardInstance card, CardZone toZone, int playerId, string detailId)
    {
        if (card == null) return;

        // 【2026-10 修复·结附秘社的附属"UI 上看不见"】
        // `ZoneManager.AttachAndRegister` 为规避 `DetachAttachmentsToGraveyard` 的无限递归，采用
        // "**先移动、后绑定**"顺序（见该方法注释）：MoveToAttachedZone → FireMoved → 本方法执行时
        // `AttachedTo` / `IsAttached` 都**尚未建立**。
        //  · 结附于角色／地区：`ZoneDetailId` 一定有值 ⇒ 走下方"附属于地区"兜底分支也能先挂进地区容器；
        //  · 结附于秘社：秘社没有 `ZoneDetailId`（detail=""）⇒ 兜底分支 `slot == null`，而此时 CardUI
        //    已在步骤 2 被摘出父节点、这里又挂不回去 ⇒ **数据正确但 UI 完全消失**
        //    （用户实测："附属卡结附到秘社后 UI 上没有显示在秘社上"）。
        // 处理：窗口期内**不动 UI**，把这次同步延迟到本帧末重放一次——届时 AttachAndRegister 已建好
        // 双向绑定，正常走"宿主=秘社 → 挂 CouncilContainer → ArrangeCouncilAttachments"分支。
        if (!_suppressAttachDefer
            && toZone == CardZone.AttachedZone
            && card.AttachedTo == null
            && !card.IsAttached
            && string.IsNullOrEmpty(card.ZoneDetailId))
        {
            string pendingUid = card.UID;
            GD.Print($"[Arena.Sync] 附属 {card.PlayerTemplate?.CardName}(UID={pendingUid}) 处于『先移动后绑定』窗口期 → 延迟到本帧末重放");
            Callable.From(() => ResyncAttachedCardUI(pendingUid)).CallDeferred();
            return;
        }

        // 议会（秘社）卡：走专用同步路径（与玩家卡 _cardUIMap 解耦）。
        // 【2026-09】判定放宽为"**曾是**秘社 UI（`_councilCardUIs` 里有它）"——秘社重放/移除时其
        // CurrentZone 已不是 CouncilZone，若只按当前区域判定就永远不会进入本分支，旧秘社 UI 会残留。
        if (card.CurrentZone == CardZone.CouncilZone || _councilCardUIs.ContainsKey(card.UID))
        {
            SyncCouncilCardUI(card);
            return;
        }

        if (!_cardUIMap.TryGetValue(card.UID, out var cardUI) || cardUI == null)
        {
            // 【诊断·2026-10】玩家卡却查不到卡 UI ⇒ View 与逻辑层分的不是同一批实例
            // （例如卡组被重装、或卡 UI 漏建）。旧实现此处**静默 return**，于是"看不到起始手牌、
            // PM draw 也没有任何 UI 变化"完全无日志可查——诊断这类故障时只能靠猜。
            // 区域卡（AreaCardData）没有 PlayerTemplate，正常流程也会走到这里，故只对玩家卡报错。
            if (card.PlayerTemplate != null)
                GD.PrintErr($"[Arena.Sync] ❌ 找不到卡 UI：{card.PlayerTemplate.CardName}（UID={card.UID}）→ 区域 {card.CurrentZone}；" +
                            "View 与逻辑实例集可能不同源（卡 UI 未建 / 实例已被替换）");
            return;
        }

        // 【2026-09·JC051 用户口径】卡面势力显示依赖的**统一注入收口**（幂等）：
        // 卡面数值＝`AreaStatsCalculator.ValueOf(card, Power, playerManager)`（与地区合计逐卡同一函数），
        // 故每张卡 UI 在**区域同步收口**处注入单机的 `_playerManager`。放这里而不是各建卡分支，
        // 避免"某条路径漏注入 ⇒ 卡面势力少算先手/临时先手能力"（与 A89/A101 同一类漏口）。
        cardUI.SetStatsDependencies(_playerManager);

        // 【2026-09 修复·A89】**双击激活订阅的统一收口**（幂等）：
        // 原先 `AttachmentDoubleClicked` 只在两处订阅——① `Arena.Events.OnCardDrawnFromTurn`（抽牌进手牌）
        // ② `Arena.Layout` 的**附属/封印入区**分支；**没有任何"进入争夺区/其它区"的兜底订阅**。
        // 于是**不经过"抽到手牌 → 打出"这条路径进场的卡**（效果放置、PM 命令、从牌库/墓地直接进场、
        // 场景重建后重新同步…）其信号**没有任何接收者**：CardUI 侧双击照常触发（有 `[CardUI] 左键到达…`
        // 日志），但 `Arena` 的转发器**永远不会被调用**（`EmitSignal` 无监听者＝静默丢弃）——
        // 正是用户实测"卡有高亮、双击**完全没有 LOG**、且**偶发**"的根因（JC042/JC044）。
        // 区域同步是本类唯一收口 ⇒ 在此统一（幂等）订阅，任何进区路径都不会再漏。
        cardUI.AttachmentDoubleClicked -= _onAttachmentDoubleClickedForward;
        cardUI.AttachmentDoubleClicked += _onAttachmentDoubleClickedForward;
        // 【2026-10·JC085】墓地行动双击订阅（同一收口点，幂等）：任何进区路径都不会漏。
        cardUI.GraveyardPlayRequested -= _onGraveyardPlayRequestedForward;
        cardUI.GraveyardPlayRequested += _onGraveyardPlayRequestedForward;

        // 堆叠区 UI 由 RefreshStackUI（OnStackChanged）独占管理：
        // 复用真卡在此挂入 StackEntryUI 并冻结。本方法若对堆叠卡执行后续
        // "无条件解冻 + GetParent()?.RemoveChild + 按区域重挂" 会破坏挂载，
        // 导致卡被摘出堆叠并被 _targetPosition 拉回旧坐标，满屏乱飞。
        // （此前把内嵌独立 CardUI 改为复用真卡的结构调整后，此分支缺失是乱飞根因。）
        if (card.CurrentZone == CardZone.Stack)
            return;

        // 离开手牌（进入任意其他区域）即清除蒙版显示；手牌分支末尾会按当前蒙版重应用。
        // 规则：卡片只有在手牌时才显示这两个蒙版。
        cardUI.SetMask(null);

        // 结算移出堆叠（或任意区域移动）时解冻平滑移动：进入堆叠时由 StackEntryUI.Setup 冻结，
        // 此处统一恢复，确保后续 ArrangeAllCards 平滑归位到新区域（地区/墓地等）正常生效。
        cardUI.FreezeSmoothMove = false;

        // 后台输出卡牌实例的横置/重置状态（BUG 文档需求：移动时输出 IsTapped）
        GD.Print($"[Arena.Sync] 卡牌移动: {card.PlayerTemplate?.CardName} → 区域={card.CurrentZone} " +
                 $"横置状态 IsTapped={card.IsTapped}");

        // 0. 附属跟随宿主移动已由 ZoneManager 在卡牌离开争夺区时统一处理（自动递归返回牌库底），
        //    此处 UI 不再驱动数据层移动，仅做纯 UI 同步。

        // 1. 清除旧归属（Arena 列表 + Zone 组件内部列表）
        _handCardUIs.Remove(cardUI);
        _handCardUIs2.Remove(cardUI);
        _areaCardUIs.Remove(cardUI);
        _areaAttachSealUIs.Remove(cardUI);
        _deckZone?.RemoveCardUI(cardUI);
        _deckZone2?.RemoveCardUI(cardUI);
        _graveyardZone?.RemoveCardUI(cardUI);
        _graveyardZone2?.RemoveCardUI(cardUI);

        // 2. 脱离物理父节点（后续 AddChild 会重新挂载）
        cardUI.GetParent()?.RemoveChild(cardUI);
        // 恢复 _Process：结算移出堆叠后卡需重新参与平滑归位（Setup 中曾 Disabled）。
        cardUI.ProcessMode = ProcessModeEnum.Inherit;

        // 3. 默认重置横置视觉：进入手牌/牌库/墓地/得分/议会/争夺区均视为"立置"，
        //    仅资产区（下方分支会按 card.IsTapped 覆盖）保留横置。这样横置卡离开场上
        //    （如进入墓地）会自动复位，满足"所有卡进入墓地/卡组/手牌需重置"的需求。
        cardUI.SetTapped(false);

        // 4. 按逻辑区域重新挂载
        switch (card.CurrentZone)
        {
            case CardZone.Hand:
            {
                int owner = card.ControllerId;   // 仅读取归属，分流逻辑由辅助方法封装
                var container = GetHandContainer(owner);
                var list = GetHandList(owner);
                container.AddChild(cardUI);
                cardUI.SetFace(true);
                cardUI.SetTapped(false);   // 进入手牌：重置横置视觉

                // 拖拽退回手牌：按放手 X 坐标插入到序列中接近的位置（而非固定末尾）。
                // 索引需夹在有效范围内，避免越界；消费后立即清空。
                if (_pendingHandInsertIndex.HasValue)
                {
                    int idx = Mathf.Clamp(_pendingHandInsertIndex.Value, 0, list.Count);
                    list.Insert(idx, cardUI);
                    container.MoveChild(cardUI, idx);   // 对齐节点树顺序与列表顺序
                    _pendingHandInsertIndex = null;
                    GD.Print($"[Arena.Sync] 手牌按 X 插入索引={idx}");
                }
                else
                {
                    list.Add(cardUI);   // 普通进手牌（抽牌等）：追加末尾
                }

                // 兜底：确保手牌卡可拖拽并订阅拖拽结束事件（避免 OnCardDrawnFromTurn 因查找失败而漏设 CanDrag）
                cardUI.CardDragEnded -= _onCardDragEndedForward;
                cardUI.CardDragEnded += _onCardDragEndedForward;
                cardUI.CanDrag = true;

                // 进入手牌：按当前蒙版状态重应用蒙版（离开手牌时已清除，这里恢复可拖拽态提示）
                ApplyMaskToOneCard(cardUI);
                break;
            }
            case CardZone.AreaZone:
            {
                // 角色/暗藏者：挂在对应 PlayerXArea（附属/封印已移入独立子区域，不在此分支处理）
                var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == card.ZoneDetailId);
                if (slot != null)
                {
                    var container = GetAreaContainer(slot, card.ControllerId);
                    container.AddChild(cardUI);
                    // 暗藏者进入争夺区必须保持背面朝上，不可被强制翻正面
                    if (card.IsCovert)
                        cardUI.SetFace(false);
                    else
                        cardUI.SetFace(true);
                    cardUI.SetTapped(card.IsTapped);   // 进入争夺区：同步实例横置状态
                    cardUI.RefreshStats();
                    _areaCardUIs.Add(cardUI);
                    cardUI.CanDrag = false;   // 争夺区卡不可拖拽（避免误拖回手牌）
                    // 双击现身信号订阅：确保任何进入争夺区的卡（含暗藏者）都稳定订阅，
                    // 避免仅依赖进手牌时的 OnCardDrawnFromTurn 订阅导致暗藏者漏订阅。
                    cardUI.CovertRevealRequested -= _onCovertRevealForward;
                    cardUI.CovertRevealRequested += _onCovertRevealForward;
                }
                else
                {
                    GD.PrintErr($"[Arena.Sync] 未找到争夺区 slot: {card.ZoneDetailId}");
                }
                // 宿主争夺区移动（含跨地区）：把结附的附属 UI 移到宿主新容器，再重排宿主与附属。
                // 否则附属 UI 停留旧容器，ArrangeAllAttachments 因 attUI.GetParent() != host 跳过附属（D48）。
                var newHost = cardUI.GetParent() as Control;
                if (newHost != null)
                {
                    foreach (var attInst in card.AttachedCards)
                    {
                        if (attInst == null) continue;
                        var attUI = FindCardUIByUID(attInst.UID);
                        if (attUI == null || attUI.GetParent() == newHost) continue;
                        attUI.GetParent()?.RemoveChild(attUI);
                        newHost.AddChild(attUI);
                    }
                }
                ArrangeAreaCards();
                ArrangeAllAttachments();
                break;
            }
            case CardZone.AttachedZone:
            case CardZone.SealedZone:
            {
                // 附属/封印卡：挂到宿主卡同容器（附属于角色/封印于角色）或地区容器（附属于地区/封印于地区）。
                // 不加入 _areaCardUIs（它们随宿主渲染，偏移由 ArrangeAllAttachments/ArrangeAllSeals 统一负责）。
                CardInstance hostCard = card.IsAttached ? card.AttachedTo : (card.IsSeal ? card.SealedBy : null);
                if (hostCard != null)
                {
                    // 宿主为秘社（CouncilZone）：秘社 UI 是 CouncilCardUI（非 CardUI 子类），
                    // 需用 FindCouncilUIByUID；其它宿主用 FindCardUIByUID。二者均取 Control 容器。
                    Control targetUI = (hostCard.CurrentZone == CardZone.CouncilZone)
                        ? FindCouncilUIByUID(hostCard.UID)
                        : FindCardUIByUID(hostCard.UID);
                    var host = targetUI?.GetParent() as Control;
                    if (targetUI != null && host != null)
                    {
                        cardUI.GetParent()?.RemoveChild(cardUI);
                        host.AddChild(cardUI);          // 与宿主卡同容器，避免继承宿主横置旋转
                        cardUI.SetFace(true);
                        cardUI.SetTapped(card.IsTapped);
                        cardUI.RefreshStats();
                        cardUI.CanDrag = false;
                        // 附属卡双击激活信号订阅（与秘社双击一致，单击不再弹窗，JC012 交互修正）
                        cardUI.AttachmentDoubleClicked -= _onAttachmentDoubleClickedForward;
                        cardUI.AttachmentDoubleClicked += _onAttachmentDoubleClickedForward;
                        ArrangeAllAttachments();        // 挂上即重排（含结附秘社的附属绕秘社 UI 布局）
                    }
                    else
                    {
                        GD.PrintErr($"[Arena.Sync] 宿主卡 UI 不存在: {hostCard.UID}，附属/封印应已随宿主离场，跳过 UI 挂载");
                    }
                }
                else
                {
                    // 附属于地区 / 封印于地区：挂在对应 PlayerXArea（无偏移，区别于附属于角色的上下偏移）
                    var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == card.ZoneDetailId);
                    if (slot != null)
                    {
                        var container = GetAreaContainer(slot, card.ControllerId);
                        container.AddChild(cardUI);
                        cardUI.SetFace(true);
                        cardUI.SetTapped(card.IsTapped);
                        cardUI.RefreshStats();
                        cardUI.CanDrag = false;
                        // 附属卡双击激活信号订阅（与秘社双击一致，单击不再弹窗，JC012 交互修正）
                        cardUI.AttachmentDoubleClicked -= _onAttachmentDoubleClickedForward;
                        cardUI.AttachmentDoubleClicked += _onAttachmentDoubleClickedForward;
                        _areaAttachSealUIs.Add(cardUI);   // 登记到地区附属/封印列表，与角色统一平铺
                    }
                    else
                    {
                        GD.PrintErr($"[Arena.Sync] 未找到争夺区 slot: {card.ZoneDetailId}");
                    }
                }
                break;
            }
            case CardZone.Deck:
            {
                // 数据移动自动同步通道：本分支由 ZoneManager.FireMoved(Deck) 触发，
                // 对应单张/零散回牌库场景，调用 DeckZone.ReturnCardUI（非调查对抗批量通道）。
                GetDeckZone(card.ControllerId)?.ReturnCardUI(cardUI);
                break;
            }
            case CardZone.Graveyard:
            {
                GetGraveyardZone(card.ControllerId)?.AddCard(cardUI);
                break;
            }
            case CardZone.ScoreZone:
            {
                var container = GetScoreContainer(card.ControllerId);
                container?.AddChild(cardUI);
                cardUI.SetFace(true);
                RefreshScoreLabel();   // 计分区进出 → 实时刷新分数 Label
                break;
            }
            case CardZone.AssetZone:
            {
                var container = GetAssetContainer(card.ControllerId);
                container?.AddChild(cardUI);
                cardUI.SetFace(true);                  // 资产区卡牌始终正面朝上
                cardUI.SetTapped(card.IsTapped);       // 同步横置状态（保留伤害等实例状态）
                cardUI.CanDrag = false;                // 资产区卡不可拖拽
                RefreshAssetLabels();                  // 资产卡进/出资产区时刷新标签
                break;
            }
            // 注意：议会（秘社）卡由 SyncCardUIToZone 开头的 CouncilZone 提前返回，
            // 转发至 SyncCouncilCardUI 处理，此处不再分支（避免与 _councilCardUIs 字典类型冲突）。
            default:
                GD.Print($"[Arena.Sync] 未处理区域的可视化: {card.CurrentZone}");
                break;
        }

        // 【MVC 薄绑定层】数据移动后必须刷新的派生 UI 统一收敛到 SyncDerivedUI，
        // 不在多处手动散弹调用，避免漏刷/重复刷（问题 4）。
        SyncDerivedUI();
    }

    /// <summary>
    /// 阶段 C：主动同步双方议会（秘社）UI。
    /// SetupCouncils 在 GameInitializer.InitializeAll 阶段（Arena 场景未加载）已将秘社移入 CouncilZone，
    /// 其 OnCardMoved 事件早于 Arena 订阅，故 Arena._Ready 末尾主动重放一次，
    /// 为 CouncilZone 中的每张秘社卡创建 CardUI 并挂到对应玩家 CouncilContainer。
    /// 后续秘社的移动（替换/移除）由 SyncCardUIToZone 的 CouncilZone 分支处理。
    /// </summary>
    private void SyncCouncilsUI()
    {
        if (_zoneManager == null || _councilUIScene == null) return;
        for (int playerId = 1; playerId <= 2; playerId++)
        {
            var councils = _zoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            if (councils == null) continue;
            foreach (var card in councils)
            {
                if (card == null) continue;
                // 已创建秘社 UI 则跳过（避免重复创建）
                if (_councilCardUIs.ContainsKey(card.UID)) continue;

                var councilUI = _councilUIScene.Instantiate<CouncilCardUI>();
                councilUI.Setup(card);                  // 读取 CouncilTemplate（CouncilCardData），默认正面朝上
                _councilCardUIs[card.UID] = councilUI;
                var container = playerId == 2 ? _councilContainer2 : _councilContainer1;
                if (container != null)
                {
                    container.AddChild(councilUI);
                    GD.Print($"[Arena.Council] 秘社 UI 已创建: {card.CouncilTemplate?.CardName} (玩家{playerId})");
                }
                councilUI.CouncilDoubleClicked += OnCouncilDoubleClicked;
            }
        }
    }

    /// <summary>
    /// 议会（秘社）卡后续移动同步（由 `SyncCardUIToZone` 转发，覆盖"仍是秘社 UI"的两类变更）：
    ///  · **在秘社区**：UI 尚未建则**补建**（秘社重放后新实例会走这里——原先只打"未找到"日志，
    ///    导致旧 UI 被摘除后玩家看不到自己的秘社），已建则确保挂到正确容器、正面朝上；
    ///  · **已不在秘社区**（被替换/移除）：从容器与字典摘除。
    /// 横置视觉统一由 `ArrangeCouncilCards`（按 `Instance.IsTapped` 旋转）负责，此处不自行旋转。
    /// </summary>
    private void SyncCouncilCardUI(CardInstance card)
    {
        if (card == null) return;
        var container = card.ControllerId == 2 ? _councilContainer2 : _councilContainer1;
        bool hasUI = _councilCardUIs.TryGetValue(card.UID, out var councilUI) && councilUI != null;

        // ① 已不在秘社区（秘社重放时旧实例被移出游戏 / 被替换）→ 从容器与字典摘除
        if (card.CurrentZone != CardZone.CouncilZone)
        {
            if (!hasUI) return;   // 从未建过 UI（如"建了实例但还没渲染"）→ 无事可做
            councilUI.GetParent()?.RemoveChild(councilUI);
            _councilCardUIs.Remove(card.UID);
            councilUI.CouncilDoubleClicked -= OnCouncilDoubleClicked;
            GD.Print($"[Arena.Council] 秘社 UI 已移除: {card.CouncilTemplate?.CardName}");
            if (container != null && GodotObject.IsInstanceValid(container)) ArrangeCouncilCards(container);
            return;
        }

        // ② 在秘社区但还没有 UI → **补建**（秘社重放：`SetupCouncils` 会在开局发牌前重建秘社）
        if (!hasUI)
        {
            if (_councilUIScene == null || container == null) return;
            councilUI = _councilUIScene.Instantiate<CouncilCardUI>();
            container.AddChild(councilUI);   // 先 AddChild 再 Setup，避免 _Ready 的默认状态覆盖 Setup 字段
            councilUI.Setup(card);
            _councilCardUIs[card.UID] = councilUI;
            councilUI.CouncilDoubleClicked += OnCouncilDoubleClicked;
            GD.Print($"[Arena.Council] 秘社 UI 已补建: {card.CouncilTemplate?.CardName} (玩家{card.ControllerId})");
            ArrangeCouncilCards(container);
            return;
        }

        // ③ 在秘社区且已有 UI：确保挂载到正确容器（如归属变更后重挂）
        if (container != null && councilUI.GetParent() != container)
        {
            councilUI.GetParent()?.RemoveChild(councilUI);
            container.AddChild(councilUI);
            ArrangeCouncilCards(container);
        }
        councilUI.SetFace(true);   // 秘社始终正面朝上
    }

    // ============================================================
    // 拖拽落点：秘社（CouncilZone）命中辅助
    // 供 CardDragController.OnCardDragEnded 的"结附于秘社"落点判定使用：
    // 普通结附卡（地区/场上卡）走 AreaCardUIs/AreaSlots，而秘社实例是 CouncilCardUI，
    // 既不在 AreaCardUIs 也不属于 AreaSlot，故需专门暴露容器与实例查询。
    // ============================================================

    /// <summary>返回指定玩家的秘社区容器（玩家1=_councilContainer1，玩家2=_councilContainer2）。</summary>
    internal Control GetCouncilContainer(int playerId)
        => playerId == 2 ? _councilContainer2 : _councilContainer1;

    /// <summary>返回指定玩家 CouncilZone 中的秘社实例（通常唯一），无则返回 null。</summary>
    internal CardInstance GetCouncilInstance(int playerId)
    {
        if (_zoneManager == null) return null;
        var councils = _zoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
        return councils?.FirstOrDefault(c => c != null);
    }

    /// <summary>设置指定玩家秘社 UI 的拖拽目标高亮（结附秘社卡拖拽中反馈）。无秘社 UI 时静默忽略。</summary>
    internal void SetCouncilHighlight(int playerId, bool on)
    {
        if (_zoneManager == null) return;
        var councils = _zoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
        var inst = councils?.FirstOrDefault(c => c != null);
        if (inst == null) return;
        if (_councilCardUIs.TryGetValue(inst.UID, out var ui) && ui != null)
            ui.SetTargetHighlight(on);
    }

    /// <summary>清除所有秘社 UI 的拖拽目标高亮。</summary>
    internal void ClearCouncilHighlights()
    {
        foreach (var ui in _councilCardUIs.Values)
            ui?.SetTargetHighlight(false);
    }

    /// <summary>
    /// 【MVC 薄绑定层】"逻辑状态发生变化 → 应刷新哪些派生 UI"的唯一定义点。
    /// 所有状态变更入口（卡移动 OnCardMoved、回合开始、行动权切换）都只调本方法，
    /// 不在各处重复手写刷新清单，消除散弹刷新（问题 4）。
    /// 注意：本方法只刷新"派生展示"，不负责蒙版退出、行动按钮等交互态，那些由各自 handler 单独处理。
    /// </summary>
    private void SyncDerivedUI()
    {
        try
        {
            RefreshArenaStats();              // 先手加成显示（计算已上收 Controller）
            RefreshAllAreaStatsLabels();      // 地区能力统计
            RefreshAssetLabels();             // 资产区费用/忠诚标签
            RefreshScoreLabel();              // 分数 Label
            // 卡面动态值标签（调查/先手/战斗/子类型等，含 JC030 运行时授予"吸血鬼"）随总线统一刷新，
            // 避免持续效果改变子类型后 SubCategoriesLabel 等不更新的遗漏（与派生 UI 收口原则一致）。
            // 过滤已释放（QueueFree 后 IsInstanceValid=false）的卡 UI，避免访问已释放 Label 抛 ObjectDisposedException。
            foreach (var ui in _cardUIMap.Values)
            {
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                try { ui.RefreshStats(); }
                catch (ObjectDisposedException) { /* 卡 UI 已被释放，跳过本卡刷新 */ }
            }
            ArrangeAllCards();                // 统一排版（含横置旋转/定位）
        }
        catch (ObjectDisposedException)
        {
            // 节点释放竞态（抽卡/翻面/移动到手时触发刷新，DeckZone 等容器可能已 QueueFree 但 Godot 对象未彻底清除）。
            // 逻辑层数据已正确，UI 竞态忽略即可，不因此崩溃。
        }
    }

    /// <summary>
    /// SyncDerivedUI 的安全包装：过滤已释放（QueueFree 后 IsInstanceValid=false）的卡 UI 节点，
    /// 避免遍历 _cardUIMap 时访问已释放 Label 抛 ObjectDisposedException，进而中断调用方
    /// （如 OnPriorityChangedHandler 中本应在后的 UpdateTurnUI）。派生刷新失败不影响核心显示。
    /// </summary>
    private void SyncDerivedUISafe()
    {
        SyncDerivedUI();
    }

    /// <summary>根据资产卡实例的 IsTapped 刷新其横置视觉表现</summary>
    private void RefreshAssetTappedStates()
    {
        RefreshAssetTappedStates(_assetContainer1);
        RefreshAssetTappedStates(_assetContainer2);
    }

        /// <summary>资产区横置变化（含回合重置 UntapAll）时同步刷新争夺区卡牌横置视觉。</summary>
        private void OnAssetsChangedHandler()
        {
            try
            {
                RefreshAssetTappedStates();
                RefreshAreaTappedStates();   // 回合重置会解除争夺区横置，需同步 UI
                RefreshAttachedTappedStates(); // 结附卡（如已横置的 JC012）随重置/激活横置同步 UI（问题②③）
                // 横置视觉由 ArrangeAllCards → PlaceCardInContainer 统一旋转/定位，
                // 支付费用后必须重排资产区与地区才能呈现横置表现。
                ArrangeAllCards();
            }
            catch (ObjectDisposedException)
            {
                // 节点释放竞态（抽卡/翻面/移动到手触发 OnVisualDirty 时，DeckZone 等容器可能已 QueueFree
                // 但 Godot 对象尚未彻底清除）。逻辑层数据已正确，UI 竞态忽略即可，不因此崩溃。
            }
        }

        /// <summary>按逻辑层实例 IsTapped 刷新所有结附卡（AttachedZone，含结附角色与结附秘社的附属、封印卡）
        /// 的横置视觉标记。与 RefreshAreaTappedStates 对称：Model→View 单向同步，UI 不回写 Model。
        /// 解决：JC012 自身激活代价横置（问题②）、回合重置结附卡未还原（问题③）的 UI 不一致。</summary>
        private void RefreshAttachedTappedStates()
        {
            if (_zoneManager == null) return;
            try
            {
                for (int p = 0; p <= 1; p++)
                {
                    foreach (var inst in _zoneManager.GetZoneCards(CardZone.AttachedZone, p))
                    {
                        var ui = FindCardUIByUID(inst.UID);
                        if (ui != null && GodotObject.IsInstanceValid(ui))
                            ui.SetTapped(inst.IsTapped);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // 节点释放竞态：忽略
            }
        }

    /// <summary>按逻辑层实例 IsTapped 刷新争夺区内所有卡牌的横置视觉表现</summary>
    private void RefreshAreaTappedStates()
    {
        if (_zoneManager == null) return;
        try
        {
            foreach (var inst in _zoneManager.GetZoneCards(CardZone.AreaZone, 0))
            {
                var ui = FindCardUIByUID(inst.UID);
                if (ui != null && GodotObject.IsInstanceValid(ui))
                    ui.SetTapped(inst.IsTapped);
            }
        }
        catch (ObjectDisposedException)
        {
            // 节点释放竞态：忽略
        }
    }

    private void RefreshAssetTappedStates(Control container)
    {
        if (container == null || !GodotObject.IsInstanceValid(container)) return;
        try
        {
            foreach (var child in container.GetChildren())
            {
                // 防御：资产区卡 UI 可能因移动/打出被 QueueFree，释放前仍在 GetChildren 返回集内，
                // 直接访问成员会抛 ObjectDisposedException。先判有效性再处理。
                if (child == null || !GodotObject.IsInstanceValid(child)) continue;
                if (child is CardUI c && c.Instance != null)
                    c.SetTapped(c.Instance.IsTapped);
            }
            RefreshAssetLabels();
        }
        catch (ObjectDisposedException)
        {
            // 场景初始化/节点释放竞态：容器或子节点在本次回调期间已被释放，忽略即可
        }
    }

    /// <summary>颜色枚举 → 中文名（与 Enums.cs 注释保持一致）</summary>
    private static readonly Dictionary<CardColor, string> ColorCn = new()
    {
        { CardColor.Brown, "棕" }, { CardColor.Yellow, "黄" }, { CardColor.White, "白" },
        { CardColor.Green, "绿" }, { CardColor.Gray, "灰" }, { CardColor.Purple, "紫" },
        { CardColor.Blue, "蓝" }, { CardColor.Black, "黑" }, { CardColor.Red, "红" },
    };

    /// <summary>领域枚举 → 中文名（与 Enums.cs 注释保持一致）</summary>
    internal static readonly Dictionary<MagicDomain, string> DomainCn = new()
    {
        { MagicDomain.Mind, "心灵" }, { MagicDomain.Divine, "神圣" },
        { MagicDomain.Star, "星空" }, { MagicDomain.Death, "死亡" }, { MagicDomain.Blood, "鲜血" },
    };

    /// <summary>刷新资产区标签：显示玩家可用/总费用，以及颜色忠诚与领域忠诚（中文罗列）。
    /// 在资产卡进出（SyncCardUIToZone）与横置刷新（RefreshAssetTappedStates）时调用。</summary>
    private void RefreshAssetLabels()
    {
        if (_playerManager == null || _zoneManager == null) return;
        RefreshOneAssetLabel(_assetLabel1, 1);
        RefreshOneAssetLabel(_assetLabel2, 2);
    }

    private void RefreshOneAssetLabel(Label label, int playerId)
    {
        if (label == null) return;

        int available = _playerManager.GetAvailableMana(playerId);
        int total = _zoneManager.GetZoneCards(CardZone.AssetZone, playerId).Count;
        label.Text = FormatCostPoolText(available, total,
            _playerManager.GetColorLoyalty(playerId), _playerManager.GetDomainLoyalty(playerId));
    }

    /// <summary>
    /// 费用池三行文案（**单机 Arena 与联机 ArenaOl 共用唯一口径**）：
    /// ① 费用：可用/总　② 颜色忠诚　③ 领域忠诚（均按枚举定义顺序罗列、仅正值、中文名+数量）。
    /// </summary>
    internal static string FormatCostPoolText(int available, int total,
        Dictionary<CardColor, int> colors, Dictionary<MagicDomain, int> domains)
    {
        // 颜色忠诚：按枚举定义顺序罗列，颜色不同用逗号隔开
        var colorParts = new List<string>();
        foreach (CardColor c in Enum.GetValues(typeof(CardColor)))
        {
            int v = (colors != null && colors.TryGetValue(c, out int cv)) ? cv : 0;
            if (v > 0) colorParts.Add($"{(ColorCn.TryGetValue(c, out var cn) ? cn : c.ToString())}{v}");
        }

        // 领域忠诚：同上
        var domainParts = new List<string>();
        foreach (MagicDomain d in Enum.GetValues(typeof(MagicDomain)))
        {
            int v = (domains != null && domains.TryGetValue(d, out int dv)) ? dv : 0;
            if (v > 0) domainParts.Add($"{(DomainCn.TryGetValue(d, out var cn) ? cn : d.ToString())}{v}");
        }

        return $"费用：{available}/{total}\n" +
               $"颜色忠诚：{string.Join("，", colorParts)}\n" +
               $"领域忠诚：{string.Join("，", domainParts)}";
    }

    /// <summary>
    /// 在容器内排布卡牌：按每张卡的【视觉宽度】(横置=140, 竖=100) 累加间距排布，
    /// 放得下→整体居中不重叠；放不下→压缩步长铺满容器宽度(允许重叠)且绝不超出容器。
    /// 右卡(ZIndex 大)压在左卡之上。横置卡通过旋转表现，布局按旋转后的视觉宽度预留空间。
    /// </summary>
    private void LayoutCardsEvenly(Control container, System.Collections.Generic.List<CardUI> cards)
    {
        if (container == null || cards == null || cards.Count == 0) return;

        float containerWidth = container.Size.X;
        if (containerWidth <= 0) containerWidth = 1152;

        // 每张卡的视觉宽度（横置旋转后宽=原高=140, 竖=100）
        var visW = new float[cards.Count];
        for (int i = 0; i < cards.Count; i++)
            visW[i] = cards[i].IsTapped ? CARD_HEIGHT : CARD_WIDTH;

        int n = cards.Count;
        if (n == 1)
        {
            PlaceCardInContainer(container, cards[0], (containerWidth - visW[0]) / 2f, 0);
            return;
        }

        float idealTotal = visW.Sum() + HAND_SPACING * (n - 1);

        float startX;
        float gap = HAND_SPACING;
        if (idealTotal <= containerWidth)
        {
            startX = (containerWidth - idealTotal) / 2f;   // 放得下：整体居中
        }
        else
        {
            // 放不下：压缩间距使总视觉宽恰好等于容器宽，绝不超出容器（允许重叠）
            startX = 0f;
            gap = (containerWidth - visW.Sum()) / (n - 1f);
        }

        float x = startX;
        for (int i = 0; i < n; i++)
        {
            // 每张卡视觉左缘 = x；横置卡视觉中心对齐到其视觉宽度中点
            PlaceCardInContainer(container, cards[i], x, i);
            x += visW[i] + gap;
        }
    }

    private void PlaceCardInContainer(Control container, Control card, float visualX, int z)
    {
        // 横置模型：逻辑 Size 保持 100x140 不变，整体旋转 90°。
        // 因此视觉尺寸为：横置 140x100，竖置 100x140。
        // 横置状态统一从 UI 属性读取：玩家卡用 CardUI.IsTapped，秘社卡用 CouncilCardUI.IsTapped（两者已实现对称）。
        bool isTapped = (card as CardUI)?.IsTapped ?? ((card as CouncilCardUI)?.IsTapped ?? false);
        float visW = isTapped ? CARD_HEIGHT : CARD_WIDTH;
        float visH = isTapped ? CARD_WIDTH : CARD_HEIGHT;

        // 视觉框中心（相对于容器）
        float visCenterX = visualX + visW / 2f;
        float visCenterY = container.Size.Y > visH
            ? container.Size.Y / 2f
            : visH / 2f;

        // 逻辑尺寸固定 100x140，PivotOffset = (50,70)。
        // 【2026-09 修复】逻辑左上角 = 视觉中心 - PivotOffset（**不旋转**）：Godot 的旋转绕 PivotOffset 进行，
        // 旋转后视觉中心仍在 `Position + PivotOffset`（实测见 `HostVisualCenter` 注释）——
        // 原写法减 `PivotOffset.Rotated(角度)` 会让**横置卡**的视觉中心偏 (120,20)，即整张卡向**右下**跑。
        Vector2 pivot = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
        Vector2 localPos = new Vector2(visCenterX, visCenterY) - pivot;

        card.AnchorLeft = 0; card.AnchorRight = 0;
        card.AnchorTop = 0; card.AnchorBottom = 0;
        card.Scale = Vector2.One;
        // 先调最小尺寸，再调 Size，防止被旧的最小尺寸限制
        card.CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
        card.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
        card.PivotOffset = pivot;
        card.RotationDegrees = isTapped ? 90f : 0f;
        card.Position = localPos;
        // TargetPosition：交由共享实现 CardLayoutHelper.SetSmoothTarget 处理，保证与联机完全同源。
        // 【2026-10 修复·横置卡偏移根因】旧写法 `container.GlobalPosition + localPos` 漏了
        // 旋转枢轴项（90° 时为 (120,20)），_Process 会把横置卡拉偏 —— 详见 CardLayoutHelper 注释。
        CardLayoutHelper.SetSmoothTarget(card, container, localPos);
        // 场上/手牌/资产卡统一抬高 ZIndex 基准，确保不被争夺区背景(AreaSlot, ZIndex 0~6)遮挡
        card.ZIndex = ZINDEX_CARD_BASE + z;   // 索引越大越靠右越在上层，实现右卡压左卡
    }

    /// <summary>排版指定玩家议会容器内的所有秘社卡（支持横置旋转，与玩家卡 PlaceCardInContainer 一致）。</summary>
    private void ArrangeCouncilCards(Control container)
    {
        if (container == null) return;
        int i = 0;
        foreach (var child in container.GetChildren())
        {
            if (child is CouncilCardUI cui)
            {
                // 与资产卡 RefreshAssetTappedStates 一致：先同步 UI 横置标记，再排版，
                // 保证 PlaceCardInContainer 读到的 card.IsTapped 与实例一致（完全对称）。
                bool tapped = cui.Instance != null && cui.Instance.IsTapped;
                cui.SetTapped(tapped);
                // 横置时视觉宽 140、竖置 100；按容器宽度居中，避免容器过窄导致横置卡溢出右侧。
                float visW = tapped ? 140f : 100f;
                float centerX = (container.Size.X - visW) / 2f;
                PlaceCardInContainer(container, cui, centerX, i);  // 多张时轻微错位避免完全重叠
                i++;
            }
        }
    }

    /// <summary>按 UID 在全部可见卡牌容器中查找对应的 CardUI（供 PM 命令 tap/untap/flip 使用）</summary>
    internal CardUI FindCardUIByUID(string uid)   // M-08：提升 internal 供 CardDragController 访问
    {
        CardUI FindIn(Control container)
        {
            if (container == null || !GodotObject.IsInstanceValid(container)) return null;
            foreach (var child in container.GetChildren())
            {
                if (child == null || !GodotObject.IsInstanceValid(child)) continue;
                if (child is CardUI c && c.Instance != null && c.Instance.UID == uid) return c;
            }
            return null;
        }

        CardUI FindInAreaSlots()
        {
            if (_areaSlots == null) return null;
            foreach (var slot in _areaSlots)
            {
                // 防御：跨局/投降后 AreaSlot 可能已被释放（延迟 QueueFree），访问其子节点前须校验
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                if (!GodotObject.IsInstanceValid(slot.Player1Area) && !GodotObject.IsInstanceValid(slot.Player2Area)) continue;
                var found = FindIn(slot.Player1Area) ?? FindIn(slot.Player2Area);
                if (found != null) return found;
            }
            return null;
        }

        return FindIn(_handContainerRef) ?? FindIn(_handContainerRef2)
            ?? FindIn(_assetContainer1) ?? FindIn(_assetContainer2)
            ?? FindIn(_scoreContainer1) ?? FindIn(_scoreContainer2)
            ?? _areaCardUIs.FirstOrDefault(c => GodotObject.IsInstanceValid(c) && c.Instance != null && c.Instance.UID == uid)
            ?? FindInAreaSlots()   // 含结附卡的附属 UI（挂于 PlayerXArea，不在 _areaCardUIs）
            ?? FindIn(_councilContainer1) ?? FindIn(_councilContainer2)  // 含结附秘社的附属 UI（挂于 CouncilContainer，与角色附属对称）
            ?? _deckZone?.GetCards().FirstOrDefault(c => GodotObject.IsInstanceValid(c) && c.Instance != null && c.Instance.UID == uid)
            ?? _deckZone2?.GetCards().FirstOrDefault(c => GodotObject.IsInstanceValid(c) && c.Instance != null && c.Instance.UID == uid)
            ?? _graveyardZone?.GetCards().FirstOrDefault(c => GodotObject.IsInstanceValid(c) && c.Instance != null && c.Instance.UID == uid)
            ?? _graveyardZone2?.GetCards().FirstOrDefault(c => GodotObject.IsInstanceValid(c) && c.Instance != null && c.Instance.UID == uid);
    }

    /// <summary>按 UID 查找秘社 UI（CouncilCardUI，非 CardUI 子类）。供结附秘社的附属定位宿主秘社 UI 使用。</summary>
    internal CouncilCardUI FindCouncilUIByUID(string uid)
    {
        CouncilCardUI FindIn(Control container)
        {
            if (container == null) return null;
            foreach (var child in container.GetChildren())
                if (child is CouncilCardUI c && c.Instance != null && c.Instance.UID == uid) return c;
            return null;
        }
        return FindIn(_councilContainer1) ?? FindIn(_councilContainer2);
    }
}
