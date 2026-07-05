using System;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 变长主轴布局策略：单列/单行，每项主轴尺寸可不同（如聊天气泡）。
    /// <para>
    /// <see cref="Measure"/> 时按数据源提供的逐项尺寸建前缀和（各项主轴起止），可视区间用二分查找定位，
    /// 单次查询 O(log n)。前缀和数组只在条数增长时重新分配，<see cref="Measure"/> 非热路径。
    /// </para>
    /// </summary>
    public sealed class VariableLoopLayout : ILoopLayout
    {
        /// <summary>最近一次测量使用的配置。</summary>
        private LoopLayoutConfig _cfg;

        /// <summary>数据条数。</summary>
        private int _count;

        /// <summary>各项主轴起始坐标（前缀和），长度 ≥ _count。</summary>
        private float[] _starts = Array.Empty<float>();

        /// <summary>各项主轴结束坐标（起始 + 该项尺寸），长度 ≥ _count，单调递增供二分。</summary>
        private float[] _ends = Array.Empty<float>();

        /// <summary>主轴内容总尺寸。</summary>
        private float _contentMain;

        /// <inheritdoc/>
        public float ContentSize => _contentMain;

        /// <inheritdoc/>
        public void Measure(int count, in LoopLayoutConfig config, Func<int, float> mainSizeOf)
        {
            _cfg = config;
            _count = count < 0 ? 0 : count;
            if (_count <= 0)
            {
                _contentMain = 0f;
                return;
            }

            if (_starts.Length < _count)
            {
                _starts = new float[_count];
                _ends = new float[_count];
            }

            float cursor = config.PadStart;
            for (int i = 0; i < _count; i++)
            {
                float size = mainSizeOf != null ? mainSizeOf(i) : 0f;
                if (size < 0f)
                {
                    size = 0f;
                }

                _starts[i] = cursor;
                _ends[i] = cursor + size;
                cursor = _ends[i];
                if (i < _count - 1)
                {
                    cursor += config.SpacingMain;
                }
            }

            _contentMain = cursor + config.PadEnd;
        }

        /// <inheritdoc/>
        public void GetVisibleRange(float scrollOffset, float viewportMain, out int first, out int last)
        {
            if (_count <= 0)
            {
                first = 0;
                last = -1;
                return;
            }

            int buffer = _cfg.Buffer < 0 ? 0 : _cfg.Buffer;
            int f = FirstEndGreater(scrollOffset) - buffer;          // 首个底边越过视口上沿的项
            int l = LastStartLess(scrollOffset + viewportMain) + buffer; // 末个顶边在视口下沿之前的项

            if (f < 0)
            {
                f = 0;
            }

            if (l > _count - 1)
            {
                l = _count - 1;
            }

            if (f > _count - 1 || l < 0 || f > l)
            {
                first = 0;
                last = -1;
                return;
            }

            first = f;
            last = l;
        }

        /// <inheritdoc/>
        public float GetItemMainStart(int index)
        {
            if (index < 0)
            {
                index = 0;
            }
            else if (index > _count - 1)
            {
                index = _count - 1;
            }

            return _count <= 0 ? 0f : _starts[index];
        }

        /// <inheritdoc/>
        public Vector2 GetAnchoredPosition(int index)
        {
            float mainPos = GetItemMainStart(index);
            float crossPos = _cfg.PadCrossStart;

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

            float mainStart = _starts[index];
            float size = _ends[index] - _starts[index];
            float target;
            switch (align)
            {
                case LoopAlign.Center:
                    target = mainStart + size * 0.5f - viewportMain * 0.5f;
                    break;
                case LoopAlign.End:
                    target = mainStart + size - viewportMain;
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

        /// <summary>二分查找首个结束坐标大于 x 的项下标；全部 ≤ x 时返回 _count。</summary>
        /// <param name="x">主轴坐标。</param>
        /// <returns>满足条件的最小下标。</returns>
        private int FirstEndGreater(float x)
        {
            int lo = 0;
            int hi = _count; // [lo, hi) 待定区间
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_ends[mid] > x)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return lo;
        }

        /// <summary>二分查找末个起始坐标小于 x 的项下标；全部 ≥ x 时返回 -1。</summary>
        /// <param name="x">主轴坐标。</param>
        /// <returns>满足条件的最大下标。</returns>
        private int LastStartLess(float x)
        {
            int lo = 0;
            int hi = _count; // 找首个 _starts[i] >= x，结果减一
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_starts[mid] >= x)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return lo - 1;
        }
    }
}
