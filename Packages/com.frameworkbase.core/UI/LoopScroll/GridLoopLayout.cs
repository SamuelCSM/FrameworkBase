using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 定尺寸网格 / 线性布局策略：所有项主轴、交叉轴尺寸一致，交叉轴排 <c>CrossCount</c> 项。
    /// <para>
    /// <c>CrossCount = 1</c> 即退化为单列（竖向）或单行（横向）；<c>≥2</c> 即网格。各项位置为 O(1) 公式，无前缀和。
    /// </para>
    /// </summary>
    public sealed class GridLoopLayout : ILoopLayout
    {
        /// <summary>最近一次测量使用的配置。</summary>
        private LoopLayoutConfig _cfg;

        /// <summary>数据条数。</summary>
        private int _count;

        /// <summary>行（线）数 = ceil(count / 交叉轴项数)。</summary>
        private int _lineCount;

        /// <summary>主轴内容总尺寸。</summary>
        private float _contentMain;

        /// <inheritdoc/>
        public float ContentSize => _contentMain;

        /// <summary>交叉轴项数（至少 1）。</summary>
        private int Cross => _cfg.CrossCount < 1 ? 1 : _cfg.CrossCount;

        /// <summary>主轴步进（行高/列宽 + 主轴间距，带正下限防除零）。</summary>
        private float StepMain
        {
            get
            {
                float step = _cfg.CellMain + _cfg.SpacingMain;
                return step > 0.0001f ? step : 0.0001f;
            }
        }

        /// <summary>交叉轴步进。</summary>
        private float StepCross => _cfg.CellCross + _cfg.SpacingCross;

        /// <inheritdoc/>
        public void Measure(int count, in LoopLayoutConfig config, Func<int, float> mainSizeOf)
        {
            _cfg = config;
            _count = count < 0 ? 0 : count;
            int cross = config.CrossCount < 1 ? 1 : config.CrossCount;
            _lineCount = _count <= 0 ? 0 : (_count + cross - 1) / cross;

            _contentMain = _lineCount <= 0
                ? 0f
                : config.PadStart + config.PadEnd + _lineCount * config.CellMain + (_lineCount - 1) * config.SpacingMain;
        }

        /// <inheritdoc/>
        public void GetVisibleRange(float scrollOffset, float viewportMain, out int first, out int last)
        {
            if (_count <= 0 || _lineCount <= 0)
            {
                first = 0;
                last = -1;
                return;
            }

            int buffer = _cfg.Buffer < 0 ? 0 : _cfg.Buffer;
            float step = StepMain;
            int lineFirst = Mathf.FloorToInt((scrollOffset - _cfg.PadStart) / step) - buffer;
            int lineLast = Mathf.FloorToInt((scrollOffset + viewportMain - _cfg.PadStart) / step) + buffer;

            if (lineFirst < 0)
            {
                lineFirst = 0;
            }

            if (lineLast > _lineCount - 1)
            {
                lineLast = _lineCount - 1;
            }

            if (lineFirst > _lineCount - 1 || lineLast < 0 || lineFirst > lineLast)
            {
                first = 0;
                last = -1;
                return;
            }

            first = lineFirst * Cross;
            last = lineLast * Cross + (Cross - 1);
            if (last > _count - 1)
            {
                last = _count - 1;
            }

            if (first > last)
            {
                first = 0;
                last = -1;
            }
        }

        /// <inheritdoc/>
        public float GetItemMainStart(int index)
        {
            int line = index / Cross;
            return _cfg.PadStart + line * StepMain;
        }

        /// <inheritdoc/>
        public Vector2 GetAnchoredPosition(int index)
        {
            int line = index / Cross;
            int col = index % Cross;
            float mainPos = _cfg.PadStart + line * StepMain;
            float crossPos = _cfg.PadCrossStart + col * StepCross;

            return _cfg.Axis == LoopAxis.Vertical
                ? new Vector2(crossPos, -mainPos)
                : new Vector2(mainPos, -crossPos);
        }

        /// <inheritdoc/>
        public float GetScrollOffset(int index, LoopAlign align, float viewportMain)
        {
            if (_count <= 0)
            {
                return 0f;
            }

            if (index < 0)
            {
                index = 0;
            }
            else if (index > _count - 1)
            {
                index = _count - 1;
            }

            float mainStart = GetItemMainStart(index);
            float target;
            switch (align)
            {
                case LoopAlign.Center:
                    target = mainStart + _cfg.CellMain * 0.5f - viewportMain * 0.5f;
                    break;
                case LoopAlign.End:
                    target = mainStart + _cfg.CellMain - viewportMain;
                    break;
                default:
                    target = mainStart;
                    break;
            }

            float max = _contentMain - viewportMain;
            if (max < 0f)
            {
                max = 0f;
            }

            return Mathf.Clamp(target, 0f, max);
        }
    }
}
