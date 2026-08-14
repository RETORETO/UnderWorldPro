using Godot;
using Script.Enums;

namespace Script.UI
{
    /// <summary>
    /// 堆叠条目 → 目标 的箭头。Control + _Draw() 自绘（兼容 Web 导出）。
    /// 箭头层（ArrowLayer）负责把它 AddChild 并每帧维护起点/终点，然后 QueueRedraw()。
    /// </summary>
    public partial class StackArrow : Control
    {
        private Vector2 _from;
        private Vector2 _to;
        private Color _color = Colors.White;

        public void SetPoints(Vector2 from, Vector2 to)
        {
            _from = from;
            _to = to;
            QueueRedraw();
        }

        public void SetColor(Color color)
        {
            _color = color;
            QueueRedraw();
        }

        public override void _Draw()
        {
            // 转换为相对本 Control 的局部坐标（ArrowLayer 全屏，故直接用全局点）
            Vector2 a = _from;
            Vector2 b = _to;

            float headSize = 14f;
            Vector2 dir = (b - a);
            float len = dir.Length();
            if (len < 1f) return;
            dir = dir / len;

            // 主干线
            DrawLine(a, b, _color, 3f, true);

            // 箭头头部（两个斜边）
            Vector2 left = b - dir * headSize + dir.Rotated(Mathf.DegToRad(150)) * headSize;
            Vector2 right = b - dir * headSize + dir.Rotated(Mathf.DegToRad(-150)) * headSize;
            DrawLine(b, left, _color, 3f, true);
            DrawLine(b, right, _color, 3f, true);
        }
    }
}
