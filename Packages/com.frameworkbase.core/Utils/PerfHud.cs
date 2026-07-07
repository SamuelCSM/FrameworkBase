using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace Framework
{
    /// <summary>
    /// 性能 HUD 叠加层（Editor / Development Build 专用，正式包逻辑整体剥离，零开销）。
    ///
    /// 屏幕顶部常驻一行：
    ///   FPS（窗口均值 + 最差帧耗时——均值会掩盖卡顿尖刺，最差帧才是玩家体感）
    ///   内存（托管 / Native 已分配 / Native 预留）与本次会话 GC 次数
    ///   Addressables 存活句柄（资源 / 实例 / 标签——阶段切换前后不回落即有泄漏，
    ///   配合 ResourceScope 定位，见 Resource/RESOURCE_SCOPE_GUIDE.md）
    ///   网络 RTT（心跳采样，未连接显示离线）
    ///
    /// 由 GameEntry 自动挂载（Inspector 可关）；运行时可经 <see cref="Visible"/> 开关，
    /// 例如接入 RuntimeConsole 的调试指令。文本每 0.5s 重建一次，帧内零字符串分配。
    /// </summary>
    public class PerfHud : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const long Mb = 1024 * 1024;

        /// <summary>运行时显隐开关（业务/调试指令可切）。</summary>
        public static bool Visible = true;

        private readonly FrameStatsAggregator _frameStats = new FrameStatsAggregator(0.5f);

        private long _managedMb;
        private long _nativeMb;
        private long _reservedMb;
        private int _gcBaseline;
        private int _gcCount;
        private string _line = "采样中...";

        private GUIStyle _style;
        private bool _styleBuilt;

        private void Awake()
        {
            _gcBaseline = GC.CollectionCount(0);
        }

        private void Update()
        {
            // 用 unscaled：暂停 / 慢动作时 HUD 仍反映真实帧率
            if (!_frameStats.Tick(Time.unscaledDeltaTime))
                return;

            // 每窗口（0.5s）刷新一次读数与文本，避免每帧字符串分配干扰被测对象
            _managedMb = GC.GetTotalMemory(false) / Mb;
            _nativeMb = Profiler.GetTotalAllocatedMemoryLong() / Mb;
            _reservedMb = Profiler.GetTotalReservedMemoryLong() / Mb;
            _gcCount = GC.CollectionCount(0) - _gcBaseline;
            BuildLine();
        }

        private void BuildLine()
        {
            var resource = Core.GameEntry.Resource;
            string resPart = resource != null
                ? $"句柄 {resource.LiveAssetHandleCount}资/{resource.LiveInstanceCount}例/{resource.LiveLabelHandleCount}签"
                : "句柄 -";

            var network = Core.GameEntry.Network;
            string netPart = network != null && network.IsConnected
                ? (ServerTime.RttMs > 0 ? $"RTT {ServerTime.RttMs}ms" : "RTT -")
                : "离线";

            _line = $"FPS {_frameStats.Fps:0.#}  最差帧 {_frameStats.WorstFrameMs:0.#}ms  |  " +
                    $"托管 {_managedMb}MB  Native {_nativeMb}/{_reservedMb}MB  GC {_gcCount}  |  " +
                    $"{resPart}  |  {netPart}";
        }

        private void OnGUI()
        {
            if (!Visible)
                return;

            if (!_styleBuilt)
            {
                _styleBuilt = true;
                _style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    // 按屏幕高度自适应字号（真机高 DPI 下固定字号看不清）
                    fontSize = Mathf.Max(12, Screen.height / 60),
                    normal = { textColor = Color.white },
                };
            }

            float height = _style.fontSize * 1.8f;
            GUI.Box(new Rect(4, 2, Screen.width - 8, height), _line, _style);
        }
#endif
    }
}
