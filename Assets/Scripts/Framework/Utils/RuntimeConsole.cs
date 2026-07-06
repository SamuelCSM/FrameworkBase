using System.Collections.Generic;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 运行时屏幕日志控制台（Development Build 专用）
    /// 挂到场景任意 GameObject 上即可，自动监听 Debug.Log / Warning / Error。
    ///
    /// 使用方式：
    ///   - 开发包直接挂组件，正式包通过 DEVELOPMENT_BUILD 宏自动禁用显示
    ///   - 运行时按 Tab 键（PC）或三指点击（移动端）展开/收起面板
    /// </summary>
    public class RuntimeConsole : MonoBehaviour
    {
        [Header("显示设置")]
        [Tooltip("最多保留的日志条数")]
        [SerializeField] private int maxLines = 50;

        [Tooltip("面板透明度 0~1")]
        [SerializeField] private float backgroundAlpha = 0.85f;

        [Tooltip("字体大小")]
        [SerializeField] private int fontSize = 22;

        // ── 状态 ──────────────────────────────────────────────
        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private Vector2 _scrollPos;
        private bool _visible = true;
        private bool _showDetail = false;
        private int _selectedIndex = -1;
        private Rect _windowRect;
        private GUIStyle _logStyle;
        private GUIStyle _warnStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _detailStyle;
        private GUIStyle _bgStyle;
        private bool _stylesBuilt = false;

        private int _logCount, _warnCount, _errorCount;

        // ── FPS / 内存采样（每 0.5s 刷新一次显示，避免数字抖动）──
        private float _fpsTimer;
        private int _fpsFrames;
        private float _fps;
        private long _managedMemoryMb;

        private struct LogEntry
        {
            public string Message;
            public string StackTrace;
            public LogType Type;
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Application.logMessageReceived += OnLogReceived;

            float w = Screen.width * 0.98f;
            float h = Screen.height * 0.42f;
            _windowRect = new Rect(Screen.width * 0.01f, Screen.height * 0.57f, w, h);
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLogReceived;
        }

        private void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            _logs.Add(new LogEntry
            {
                Message    = condition,
                StackTrace = stackTrace,
                Type       = type
            });

            switch (type)
            {
                case LogType.Warning: _warnCount++;  break;
                case LogType.Error:
                case LogType.Exception: _errorCount++; break;
                default: _logCount++; break;
            }

            if (_logs.Count > maxLines)
                _logs.RemoveAt(0);

            // 新日志进来时自动滚到底部
            _scrollPos.y = float.MaxValue;
        }

        private void Update()
        {
            // PC：Tab 键切换显示/隐藏
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
                _visible = !_visible;

            // FPS 采样（unscaled，暂停/慢动作时仍反映真实渲染帧率）
            _fpsTimer += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _fpsFrames / _fpsTimer;
                _managedMemoryMb = System.GC.GetTotalMemory(false) / (1024 * 1024);
                _fpsTimer = 0f;
                _fpsFrames = 0;
            }
        }

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _bgStyle = new GUIStyle(GUI.skin.box);

            _logStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fontSize,
                wordWrap  = true,
                normal    = { textColor = Color.white }
            };
            _warnStyle = new GUIStyle(_logStyle)
            {
                normal = { textColor = new Color(1f, 0.85f, 0.2f) }
            };
            _errorStyle = new GUIStyle(_logStyle)
            {
                normal = { textColor = new Color(1f, 0.35f, 0.35f) }
            };
            _detailStyle = new GUIStyle(_logStyle)
            {
                fontSize = fontSize - 2,
                normal   = { textColor = new Color(0.8f, 0.8f, 0.8f) }
            };
        }

        private void OnGUI()
        {
            BuildStyles();

            // ── 右上角小按钮，始终可见 ──────────────────────────
            DrawToggleButton();

            if (!_visible) return;

            // ── 主面板 ─────────────────────────────────────────
            Color bg = new Color(0f, 0f, 0f, backgroundAlpha);
            GUI.backgroundColor = bg;
            _windowRect = GUI.Window(9527, _windowRect, DrawWindow, "");
            GUI.backgroundColor = Color.white;
        }

        private void DrawToggleButton()
        {
            string label = _errorCount > 0
                ? $"{_fps:0}fps <color=#ff5555>E:{_errorCount}</color> W:{_warnCount} L:{_logCount}"
                : $"{_fps:0}fps W:{_warnCount} L:{_logCount}";

            GUIStyle btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize  = fontSize - 2,
                richText  = true,
                alignment = TextAnchor.MiddleCenter
            };

            float bw = 220, bh = 36; // 加入 FPS 显示后加宽，避免文字截断
            if (GUI.Button(new Rect(Screen.width - bw - 6, 6, bw, bh), label, btnStyle))
                _visible = !_visible;
        }

        private void DrawWindow(int id)
        {
            // 标题行：统计 + 清除按钮
            GUILayout.BeginHorizontal();
            GUILayout.Label(
                $"<b>RuntimeConsole</b>  FPS:{_fps:0}  Mem:{_managedMemoryMb}MB  Log:{_logCount}  Warn:{_warnCount}  Error:{_errorCount}  [Tab 显/隐]",
                new GUIStyle(GUI.skin.label) { fontSize = fontSize - 2, richText = true, normal = { textColor = Color.cyan } });
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(28)))
            {
                _logs.Clear();
                _logCount = _warnCount = _errorCount = 0;
                _selectedIndex = -1;
                _showDetail = false;
            }
            GUILayout.EndHorizontal();

            // 主日志列表
            float detailHeight = _showDetail ? 120 : 0;
            float listHeight   = _windowRect.height - 60 - detailHeight;

            _scrollPos = GUILayout.BeginScrollView(_scrollPos,
                GUILayout.Height(listHeight));

            for (int i = 0; i < _logs.Count; i++)
            {
                var entry = _logs[i];
                GUIStyle style = entry.Type == LogType.Warning  ? _warnStyle
                               : entry.Type == LogType.Error ||
                                 entry.Type == LogType.Exception ? _errorStyle
                               : _logStyle;

                // 选中行高亮
                if (_selectedIndex == i)
                {
                    GUI.backgroundColor = new Color(0.3f, 0.5f, 0.8f, 0.6f);
                    GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    GUI.backgroundColor = Color.white;
                }

                if (GUILayout.Button(entry.Message, style))
                {
                    _selectedIndex = i;
                    _showDetail    = true;
                }
            }

            GUILayout.EndScrollView();

            // 详情区域
            if (_showDetail && _selectedIndex >= 0 && _selectedIndex < _logs.Count)
            {
                GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(2));
                GUILayout.BeginVertical(GUILayout.Height(detailHeight));
                GUILayout.Label(_logs[_selectedIndex].StackTrace, _detailStyle);
                GUILayout.EndVertical();
            }

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 30));
        }
    }
}
