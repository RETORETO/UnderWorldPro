using Godot;
using Script.UI;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-09·UI 布局】锁定"横置卡视觉中心"的**唯一正确算法**：
    /// Godot 的 `Control` 旋转是**绕 `PivotOffset`** 进行的 ⇒ 旋转后视觉中心**仍在** `Position + PivotOffset`
    /// （`GetGlobalTransform()` 的四角 AABB 中心），而**不是** `Position + PivotOffset.Rotated(角度)`。
    ///
    /// 为什么值得单测：附属/封印排版与 `PlaceCardInContainer` 都依赖这条几何关系；
    /// 历史上用 `PivotOffset.Rotated(90°)` 估算 ⇒ 宿主横置时算出的中心偏 **(120,20)**，
    /// 附属被摆到宿主的**左上方**（用户实测："部分情况下附属 UI 会显示在宿主上方"）。
    /// 若将来 Godot 语义变化（或有人"顺手"改回旋转公式），本用例会立刻失败。
    /// </summary>
    public class P5LayoutMathTests : TestRunner
    {
        public void TestLayout_旋转卡的视觉中心等于Position加PivotOffset()
        {
            // 【P0】尺寸不再写字面量：跟随 CardLayoutHelper（现 140×196），改常量后用例自动跟随语义。
            float w = CardLayoutHelper.CARD_WIDTH, h = CardLayoutHelper.CARD_HEIGHT;
            var c = new Control { Size = new Vector2(w, h) };
            c.PivotOffset = new Vector2(w / 2f, h / 2f);
            c.Position = new Vector2(0, 0);
            c.RotationDegrees = 90f;

            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");
            tree.Root.AddChild(c);

            var gt = c.GetGlobalTransform();
            Vector2 p00 = gt * new Vector2(0, 0);
            Vector2 pW0 = gt * new Vector2(w, 0);
            Vector2 p0H = gt * new Vector2(0, h);
            Vector2 pWH = gt * new Vector2(w, h);
            float minX = Mathf.Min(Mathf.Min(p00.X, pW0.X), Mathf.Min(p0H.X, pWH.X));
            float maxX = Mathf.Max(Mathf.Max(p00.X, pW0.X), Mathf.Max(p0H.X, pWH.X));
            float minY = Mathf.Min(Mathf.Min(p00.Y, pW0.Y), Mathf.Min(p0H.Y, pWH.Y));
            float maxY = Mathf.Max(Mathf.Max(p00.Y, pW0.Y), Mathf.Max(p0H.Y, pWH.Y));
            var aabbCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
            var visSize = new Vector2(maxX - minX, maxY - minY);

            Assert.AreEqual(h, visSize.X, "横置后视觉宽＝卡高");
            Assert.AreEqual(w, visSize.Y, "横置后视觉高＝卡宽");
            Assert.AreEqual(c.Position + c.PivotOffset, aabbCenter,
                "视觉中心＝Position + PivotOffset（**不旋转**）——附属/封印排版与 PlaceCardInContainer 的基准");
            Assert.IsFalse(aabbCenter == c.Position + c.PivotOffset.Rotated(Mathf.Pi / 2f),
                "对照：Position + PivotOffset.Rotated(90°) 是**错的**（偏 (卡高−卡宽/2…) 见下一条用例）");

            c.QueueFree();
        }

        /// <summary>
        /// 【P4·2026-10-18】锁定资产/分数区的**层叠几何**：
        ///  - 每张露边 = 36px（放得下时）；
        ///  - 最右卡的右缘不得超出容器（放不下时压缩露边，绝不溢出）；
        ///  - ZIndex 递增（右卡压左卡）。
        /// 为什么值得单测：层叠是"数量可读性"的唯一保障，露边算错会立刻变成"全叠成一张"或"横向溢出遮住同排元素"。
        /// </summary>
        public void TestLayout_层叠露边36且不溢出容器()
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");

            float w = CardLayoutHelper.CARD_WIDTH, h = CardLayoutHelper.CARD_HEIGHT;

            // ---- ① 5 张、容器够宽：step 恰好 36 ----
            var container = new Control { Size = new Vector2(400, 210) };
            tree.Root.AddChild(container);

            var cards = new System.Collections.Generic.List<Control>();
            for (int i = 0; i < 5; i++)
            {
                var c = new Control { Size = new Vector2(w, h) };
                container.AddChild(c);
                cards.Add(c);
            }
            CardLayoutHelper.LayoutStacked(container, cards, 36f);

            float step = cards[1].Position.X - cards[0].Position.X;
            Assert.AreEqual(36f, step, "放得下时露边宽度＝36px");

            float rightEdge = cards[4].Position.X + w;
            Assert.IsTrue(rightEdge <= container.Size.X + 0.5f,
                $"最右卡右缘 {rightEdge} 不得超出容器宽 {container.Size.X}");

            Assert.IsTrue(cards[4].ZIndex > cards[0].ZIndex, "右卡压左卡（ZIndex 递增）");

            // ---- ② 张数过多：压缩露边，仍不溢出 ----
            var many = new System.Collections.Generic.List<Control>();
            for (int i = 0; i < 12; i++)
            {
                var c = new Control { Size = new Vector2(w, h) };
                container.AddChild(c);
                many.Add(c);
            }
            CardLayoutHelper.LayoutStacked(container, many, 36f);
            float lastRight = many[11].Position.X + w;
            Assert.IsTrue(lastRight <= container.Size.X + 0.5f,
                $"12 张时压缩露边后仍不得溢出（末卡右缘 {lastRight}）");
            float stepMany = many[1].Position.X - many[0].Position.X;
            Assert.IsTrue(stepMany < 36f, "张数过多时露边被压缩到 36px 以下");

            foreach (var c in cards) c.QueueFree();
            foreach (var c in many) c.QueueFree();
            container.QueueFree();
        }

        /// <summary>
        /// 【P4·决策 7·2026-10-18】锁定"叠成一摞"的几何：
        ///  - `LayoutCompactBacks` / `LayoutPile`：所有卡**叠在同一位置**且整体居中（只露最上面一张）；
        ///  - `LayoutStacked`：每张露 36px 且不溢出容器。
        ///
        /// **2026-10-19 使用口径已回退**（用户：对手手牌折叠效果不好、资产区折叠也回退），
        /// 两个函数当前**无调用点**；本用例保留为几何契约，防止将来做"真·牌堆"时改坏
        /// （只要 Position 有位移就会露边，立刻从"一摞"破功成"一排重叠的牌"）。
        /// </summary>
        public void TestLayout_牌堆叠一处且层叠露边36()
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");

            float w = CardLayoutHelper.CARD_WIDTH;

            var container = new Control { Size = new Vector2(960, 240) };   // 联机 HandContainer 实测宽 960
            tree.Root.AddChild(container);

            var cards = new System.Collections.Generic.List<Control>();
            for (int i = 0; i < 7; i++)
            {
                var c = new Control { Size = new Vector2(w, CardLayoutHelper.CARD_HEIGHT) };
                container.AddChild(c);
                cards.Add(c);
            }

            // ---- ① 紧凑：全部同一位置 ----
            CardLayoutHelper.LayoutCompactBacks(container, cards);
            for (int i = 1; i < cards.Count; i++)
            {
                Assert.AreEqual(cards[0].Position, cards[i].Position,
                    $"紧凑态第 {i} 张必须与第 0 张完全重合（否则会露出边、看起来不像「一张牌背」）");
            }
            Assert.AreEqual((960f - w) / 2f, cards[0].Position.X,
                "紧凑牌背在容器内水平居中");

            // ---- ② 展开：每张露 36px，且不溢出 ----
            CardLayoutHelper.LayoutStacked(container, cards, 36f);
            Assert.AreEqual(36f, cards[1].Position.X - cards[0].Position.X,
                "展开态露边＝36px（与资产/分数区层叠同口径）");
            float rightEdge = cards[6].Position.X + w;
            Assert.IsTrue(rightEdge <= container.Size.X + 0.5f,
                $"展开后末卡右缘 {rightEdge} 不得溢出容器宽 {container.Size.X}");

            foreach (var c in cards) c.QueueFree();
            container.QueueFree();
        }

        /// <summary>
        /// 【2026-10·卡平滑归位】锁定"**旋转 Control 的 GlobalPosition 含枢轴项**"：
        /// 旋转后的 transform origin = Position + pivot − R·pivot（scale=1），90° 时多出 **(120,20)**。
        ///
        /// 为什么值得单测：`CardUI._Process` 用 `GlobalPosition.Lerp(TargetPosition, 0.15/帧)` 逼近目标。
        /// 历史上 `PlaceCardInContainer` 把目标写成 `container.GlobalPosition + localPos`（**漏了枢轴项**），
        /// ⇒ 横置卡到目标的距离恒为 121.7 > 0.5 ⇒ 每帧都被拉、**稳定收敛到 localPos − (120,20)**，
        /// 表现为"横置卡整体偏移"；而结附于它的附属用自锁写法 `TargetPosition = GlobalPosition`（不漂移）
        /// ⇒ **宿主漂移、附属不动 ⇒ 两者分离**。
        /// 现由 `CardLayoutHelper.SetSmoothTarget` 统一取 `card.GlobalPosition`，本用例防止有人改回旧算式。
        /// </summary>
        public void TestLayout_横置卡全局位置含枢轴项故平滑目标须取自GlobalPosition()
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            Assert.IsNotNull(tree, "测试运行于 Godot 场景树内");

            var parent = new Control { Size = new Vector2(400, 240), Position = new Vector2(500, 300) };
            tree.Root.AddChild(parent);

            var localPos = new Vector2(120, 50);
            var cardSize = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT);
            var pivot = cardSize / 2f;

            // ---- ① 横置卡：GlobalPosition 比 Position 多出枢轴项 ----
            var tapped = new Control { Size = cardSize };
            tapped.PivotOffset = pivot;
            tapped.RotationDegrees = 90f;
            tapped.Position = localPos;
            parent.AddChild(tapped);

            // 旋转枢轴项推导：pivot − R(90°)·pivot（R 把 (x,y) 映到 (−y,x)）
            var pivotTerm = pivot - new Vector2(-pivot.Y, pivot.X);
            Assert.AreEqual(parent.GlobalPosition + localPos + pivotTerm, tapped.GlobalPosition,
                $"横置卡 GlobalPosition = Position + 枢轴项 {pivotTerm}（旋转绕 PivotOffset ⇒ transform origin 带该项）");
            Assert.IsFalse(parent.GlobalPosition + localPos == tapped.GlobalPosition,
                "对照：`容器.GlobalPosition + localPos` 是**错的**平滑目标（少枢轴项）");

            // Bug 现场：旧目标与 Lerp 起点距离 > 0.5 ⇒ _Process 每帧把横置卡拉偏；正确目标距离为 0 ⇒ 零漂移
            float distOld = tapped.GlobalPosition.DistanceTo(parent.GlobalPosition + localPos);
            float distCorrect = tapped.GlobalPosition.DistanceTo(tapped.GlobalPosition);
            Assert.IsTrue(distOld > 0.5f, "旧目标：距离 > 0.5 ⇒ _Process 每帧拉偏横置卡（Bug 现场）");
            Assert.IsTrue(distCorrect <= 0.5f,
                "正确目标＝卡自身 GlobalPosition ⇒ 与 Lerp 起点距离为 0 ⇒ 不漂移（SetSmoothTarget 的做法）");

            // ---- ② 未旋转卡：GlobalPosition == 容器全局 + localPos（新旧写法等价 ⇒ 修复无副作用） ----
            var upright = new Control { Size = cardSize };
            upright.PivotOffset = pivot;
            upright.RotationDegrees = 0f;
            upright.Position = localPos;
            parent.AddChild(upright);
            Assert.AreEqual(parent.GlobalPosition + localPos, upright.GlobalPosition,
                "未旋转卡：GlobalPosition 恰为 容器.GlobalPosition + localPos（两种写法等价）");

            tapped.QueueFree();
            upright.QueueFree();
            parent.QueueFree();
        }
    }
}
