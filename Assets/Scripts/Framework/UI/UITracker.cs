using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 事件类型
    /// </summary>
    public enum UIEventType
    {
        Click,
        LongPress,
        PointerDown,
        PointerUp,
        BeginDrag,
        EndDrag,
        PointerEnter,
        PointerExit,
    }

    /// <summary>
    /// 单条 UI 埋点记录
    /// </summary>
    [Serializable]
    public struct UIEventRecord
    {
        /// <summary>事件发生的 Unix 时间戳（秒）</summary>
        public long      Timestamp;

        /// <summary>按钮/组件在场景中的完整路径，例如 "UIRoot/Panel_Main/BtnLogin"</summary>
        public string    Path;

        /// <summary>事件类型</summary>
        public UIEventType EventType;

        /// <summary>事件发生时所在的场景名</summary>
        public string    Scene;
    }

    /// <summary>
    /// UI 埋点收集器
    ///
    /// 职责：
    ///   记录玩家所有 UI 交互行为，供后端分析玩家操作路径/漏斗。
    ///
    /// 当前状态：内存缓冲 + 本地 JSON Lines 落盘已实现（阈值自动刷写、退出兜底刷写、
    /// 文件体积上限保护）；服务器上报待分析端点就绪后接入（见 <see cref="UploadToServer"/>）。
    ///
    /// 典型数据格式（每次点击一条，JSON Lines 逐行追加）：
    ///   { "Timestamp":1716537600, "Path":"UIRoot/Panel_Main/BtnLogin",
    ///     "EventType":0, "Scene":"Main" }
    /// </summary>
    public static class UITracker
    {
        // ── 开关 ──────────────────────────────────────────────────────────
        /// <summary>全局开关，false 时所有 Record 调用立即返回</summary>
        public static bool IsEnabled { get; set; } = true;

        /// <summary>缓冲达到该条数自动落盘，防止内存缓冲无上限增长。</summary>
        private const int FlushThreshold = 64;

        /// <summary>本地埋点文件体积上限（字节）：超限时删除重建，防止长期运行无限膨胀。</summary>
        private const long MaxLocalFileBytes = 2 * 1024 * 1024;

        /// <summary>本地埋点文件名（JSON Lines：每行一条记录，追加写、崩溃安全）。</summary>
        private const string LocalFileName = "ui_events.jsonl";

        // ── 内存缓冲 ──────────────────────────────────────────────────────
        private static readonly List<UIEventRecord> _buffer = new List<UIEventRecord>(256);

        /// <summary>退出兜底刷写是否已挂接（静态惰性挂接，避免依赖初始化顺序）。</summary>
        private static bool _quitHookInstalled;

        /// <summary>当前缓冲区内的记录数量</summary>
        public static int BufferCount => _buffer.Count;

        // ── 事件钩子（外部可监听，用于实时调试或自定义上报）──────────────
        /// <summary>每条记录生成时触发（参数为新记录）</summary>
        public static event Action<UIEventRecord> OnEventRecorded;

        // ── 核心记录方法 ──────────────────────────────────────────────────

        /// <summary>记录一条 UI 事件（由 UIExtensions 调用，业务层无需手动调用）</summary>
        public static void Record(GameObject go, UIEventType eventType)
        {
            if (!IsEnabled || go == null) return;

            InstallQuitHookOnce();

            var record = new UIEventRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Path      = GetHierarchyPath(go),
                EventType = eventType,
                Scene     = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            };

            _buffer.Add(record);
            OnEventRecorded?.Invoke(record);

            // 达到阈值自动落盘，保证内存缓冲有界。
            if (_buffer.Count >= FlushThreshold)
            {
                FlushToLocal();
            }
        }

        // ── 缓冲区操作 ────────────────────────────────────────────────────

        /// <summary>返回当前缓冲区的快照（只读副本）</summary>
        public static IReadOnlyList<UIEventRecord> GetBuffer()
            => _buffer.AsReadOnly();

        /// <summary>清空内存缓冲区</summary>
        public static void ClearBuffer()
            => _buffer.Clear();

        // ── 本地持久化 ────────────────────────────────────────────────────
        /// <summary>
        /// 将内存缓冲追加写入本地 JSON Lines 文件（persistentDataPath/ui_events.jsonl），
        /// 成功后清空缓冲；写入失败保留缓冲（下次刷写重试），失败原因记入日志。
        /// 文件超过体积上限时删除重建，防止长期运行无限膨胀。
        /// </summary>
        public static void FlushToLocal()
        {
            if (_buffer.Count == 0) return;

            string filePath = System.IO.Path.Combine(Application.persistentDataPath, LocalFileName);
            try
            {
                var fileInfo = new System.IO.FileInfo(filePath);
                if (fileInfo.Exists && fileInfo.Length > MaxLocalFileBytes)
                {
                    // 超限重建：埋点属可丢失的辅助数据，丢弃旧文件优于撑爆用户存储。
                    fileInfo.Delete();
                }

                var sb = new System.Text.StringBuilder(_buffer.Count * 96);
                for (int i = 0; i < _buffer.Count; i++)
                {
                    sb.AppendLine(JsonUtility.ToJson(_buffer[i]));
                }

                System.IO.File.AppendAllText(filePath, sb.ToString());
                _buffer.Clear();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // 磁盘满/权限异常等已知 IO 失败：保留缓冲待下次重试，不让埋点故障影响主流程。
                Logger.Warning($"[UITracker] 埋点落盘失败（保留缓冲下次重试）：{ex.Message}");
            }
        }

        // ── 上报到服务器 ──────────────────────────────────────────────────
        /// <summary>
        /// 批量上报本地埋点到分析服务器。
        /// <b>当前未接线</b>：项目尚无分析上报端点，本方法仅先落盘并记录告警；
        /// 端点就绪后在此实现「读 ui_events.jsonl → HTTP POST → 成功后删除本地文件」。
        /// 调用方不应依赖其产生任何网络行为。
        /// </summary>
        public static void UploadToServer()
        {
            FlushToLocal();
            Logger.Warning("[UITracker] 分析上报端点未配置，埋点仅本地缓存（persistentDataPath/ui_events.jsonl）");
        }

        /// <summary>
        /// 惰性挂接应用退出兜底：退出前把内存缓冲落盘，避免尾部事件丢失。
        /// </summary>
        private static void InstallQuitHookOnce()
        {
            if (_quitHookInstalled) return;
            _quitHookInstalled = true;
            Application.quitting += FlushToLocal;
        }

        // ── 工具方法 ──────────────────────────────────────────────────────

        /// <summary>
        /// 获取 GameObject 在场景层级中的完整路径。
        /// 例如：Canvas/Panel_Main/BtnLogin
        /// </summary>
        private static string GetHierarchyPath(GameObject go)
        {
            var sb = new System.Text.StringBuilder(go.name);
            Transform t = go.transform.parent;
            while (t != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }
    }
}
