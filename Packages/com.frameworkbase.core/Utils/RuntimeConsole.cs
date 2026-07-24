using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework.Diagnostics;
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
    ///   - 组合根经 <see cref="AttachCommands"/> 接入命令总线后，面板底部出现命令输入行：
    ///     输入命令回车/点执行，结果以日志形式回显；输入前缀时给出候选命令按钮
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

        // ── 命令输入（AttachCommands 接入后启用）───────────────
        private CommandRegistry _commands;
        private string _input = string.Empty;
        private readonly List<string> _cmdHistory = new List<string>();
        private int _historyIndex = -1;
        private bool _inputFocused;
        private const string InputControlName = "RC_CmdInput";
        private const int MaxCmdHistory = 32;
        private const int MaxSuggestions = 6;

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
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            enabled = false;
            return;
#endif
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

        /// <summary>
        /// 接入命令总线（组合根调用）。接入后面板底部出现命令输入行；
        /// 传 null 可摘除。命令授权由总线自身门禁，本面板只负责输入与回显。
        /// </summary>
        public void AttachCommands(CommandRegistry registry)
        {
            _commands = registry;
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
            // PC：Tab 键切换显示/隐藏（命令输入框聚焦时不抢键）
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) && !_inputFocused)
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

            // 主日志列表（接入命令总线后为输入区预留高度：候选行 + 输入行）
            float detailHeight = _showDetail ? 120 : 0;
            float commandHeight = _commands != null ? 84 : 0;
            float listHeight   = _windowRect.height - 60 - detailHeight - commandHeight;

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

            // 命令输入区
            if (_commands != null)
                DrawCommandBar();

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 30));
        }

        // ── 命令输入区 ─────────────────────────────────────────

        private void DrawCommandBar()
        {
            // 键盘事件先于控件绘制处理：回车提交、上下键翻历史（仅输入框聚焦时）
            Event e = Event.current;
            bool focused = GUI.GetNameOfFocusedControl() == InputControlName;
            if (focused && e.type == EventType.KeyDown)
            {
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                    && !string.IsNullOrWhiteSpace(_input))
                {
                    SubmitCommand();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.UpArrow && _cmdHistory.Count > 0)
                {
                    _historyIndex = _historyIndex < 0
                        ? _cmdHistory.Count - 1
                        : Mathf.Max(0, _historyIndex - 1);
                    _input = _cmdHistory[_historyIndex];
                    e.Use();
                }
                else if (e.keyCode == KeyCode.DownArrow && _historyIndex >= 0)
                {
                    _historyIndex++;
                    if (_historyIndex >= _cmdHistory.Count)
                    {
                        _historyIndex = -1;
                        _input = string.Empty;
                    }
                    else
                    {
                        _input = _cmdHistory[_historyIndex];
                    }
                    e.Use();
                }
            }

            // 候选命令行：输入了命令名前缀（尚未打空格进入参数）时给出可点选的补全按钮
            GUILayout.BeginHorizontal(GUILayout.Height(34));
            if (_input.Length > 0 && !_input.Contains(" "))
            {
                int shown = 0;
                foreach (CommandInfo info in _commands.ListAvailable())
                {
                    if (!info.Name.StartsWith(_input, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (GUILayout.Button(info.Name, GUILayout.Height(30)))
                    {
                        _input = info.Name + " ";
                        GUI.FocusControl(InputControlName);
                    }
                    if (++shown >= MaxSuggestions)
                        break;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 输入行
            GUILayout.BeginHorizontal(GUILayout.Height(40));
            GUI.SetNextControlName(InputControlName);
            _input = GUILayout.TextField(_input, new GUIStyle(GUI.skin.textField) { fontSize = fontSize },
                GUILayout.ExpandWidth(true), GUILayout.Height(36));
            if (GUILayout.Button("执行", GUILayout.Width(80), GUILayout.Height(36))
                && !string.IsNullOrWhiteSpace(_input))
            {
                SubmitCommand();
            }
            GUILayout.EndHorizontal();

            // 供 Update 的 Tab 显隐判断使用（GetNameOfFocusedControl 只能在 OnGUI 里读）
            if (e.type == EventType.Repaint)
                _inputFocused = GUI.GetNameOfFocusedControl() == InputControlName;
        }

        private void SubmitCommand()
        {
            string line = _input.Trim();
            _input = string.Empty;
            _historyIndex = -1;

            // 相邻去重后入历史，封顶丢最旧
            if (_cmdHistory.Count == 0 || _cmdHistory[_cmdHistory.Count - 1] != line)
            {
                _cmdHistory.Add(line);
                if (_cmdHistory.Count > MaxCmdHistory)
                    _cmdHistory.RemoveAt(0);
            }

            RunCommandAsync(line).Forget();
        }

        /// <summary>执行命令并把结果回显进日志列表（失败走 Warning 黄色醒目；命令由人工输入，不会污染自动化验收的零告警门禁）。</summary>
        private async UniTaskVoid RunCommandAsync(string line)
        {
            GameLog.Log($"[Cmd] > {line}");
            CommandResult result = await _commands.ExecuteAsync(line);
            if (result.Success)
                GameLog.Log($"[Cmd] {(string.IsNullOrEmpty(result.Message) ? "OK" : result.Message)}");
            else
                GameLog.Warning($"[Cmd] {result.Message}");
        }
    }
}
