using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Framework.Editor.Release
{
    /// <summary>
    /// Release Center 控制台（目标设计 §6）。
    /// <para>
    /// 铁律：本窗口只做编排与展示——仓库读取走 <see cref="ReleaseRepositoryScanner"/>，
    /// 校验/回滚/晋级全部调用与 CLI 完全相同的管线方法，禁止面板独有逻辑。
    /// 操作分级：dev 环境允许本地发布/回滚；qa/prod 一律经 gh workflow run 触发
    /// release.yml（审批链不可绕过），面板对 qa/prod 只读+发起，不能本机直发。
    /// </para>
    /// </summary>
    public class ReleaseCenterWindow : EditorWindow
    {
        private const string UploadRootPrefsKey = "FrameworkBase.ReleaseCenter.UploadRoot";

        private string _uploadRoot = string.Empty;
        private string[] _scopes = Array.Empty<string>();
        private int _scopeIndex;
        private ChannelSnapshot _snapshot;
        private Vector2 _scroll;

        [MenuItem("Framework/发布/Release Center 控制台")]
        private static void Open()
        {
            var window = GetWindow<ReleaseCenterWindow>("Release Center");
            window.minSize = new Vector2(760, 420);
        }

        private void OnEnable()
        {
            _uploadRoot = EditorPrefs.GetString(UploadRootPrefsKey, string.Empty);
            Refresh();
        }

        private void Refresh()
        {
            _scopes = ReleaseRepositoryScanner.ScanScopes(_uploadRoot).ToArray();
            _scopeIndex = Mathf.Clamp(_scopeIndex, 0, Mathf.Max(0, _scopes.Length - 1));
            _snapshot = _scopes.Length > 0
                ? ReleaseRepositoryScanner.LoadChannel(_uploadRoot, _scopes[_scopeIndex])
                : null;
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (_snapshot == null)
            {
                EditorGUILayout.HelpBox(
                    "未发现渠道作用域。请选择产物仓库根目录（包含 {env}/{platform}/{channel}/releases 布局）。",
                    MessageType.Info);
                return;
            }

            DrawPointer();
            DrawReleases();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("产物仓库根", GUILayout.Width(70));
                string newRoot = EditorGUILayout.TextField(_uploadRoot);
                if (GUILayout.Button("浏览…", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    string picked = EditorUtility.OpenFolderPanel("选择产物仓库根目录", _uploadRoot, "");
                    if (!string.IsNullOrEmpty(picked)) newRoot = picked;
                }
                if (newRoot != _uploadRoot)
                {
                    _uploadRoot = newRoot;
                    EditorPrefs.SetString(UploadRootPrefsKey, _uploadRoot);
                    Refresh();
                }
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(40)))
                    Refresh();
            }

            if (_scopes.Length > 0)
            {
                int newIndex = EditorGUILayout.Popup("渠道作用域", _scopeIndex, _scopes);
                if (newIndex != _scopeIndex)
                {
                    _scopeIndex = newIndex;
                    Refresh();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                string env = CurrentEnv();
                if (GUILayout.Button("dry-run 门禁", GUILayout.Width(100)))
                    RunDryRun(env);
                if (env == "dev")
                {
                    if (GUILayout.Button("本地发布（发布窗口）", GUILayout.Width(140)))
                        GetWindow<HotUpdatePublisher>("热更发布");
                }
                else
                {
                    if (GUILayout.Button($"经 workflow 发布到 {env}", GUILayout.Width(160)))
                        RunWorkflow($"-f kind=hotupdate -f releaseEnv={env}",
                            "发布需要 appVersion/resourceVersion/codeVersion 等输入，已生成命令示例到 Console，请补全后执行或前往 Actions 页面。");
                    if (GUILayout.Button($"经 workflow 晋级到 {env}", GUILayout.Width(160)))
                        RunWorkflow($"-f kind=promote -f releaseEnv={env} -f sourceEnv=qa", null);
                }
            }
        }

        private void DrawPointer()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                if (_snapshot.Pointer == null)
                {
                    EditorGUILayout.LabelField("当前指针", "（该渠道尚未切换过 current.json）");
                    return;
                }

                var pointer = _snapshot.Pointer;
                string switchedAt = DateTimeOffset
                    .FromUnixTimeSeconds(pointer.SwitchedAtUnixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                EditorGUILayout.LabelField("★ 当前指针（Active）",
                    $"{Short(pointer.ReleaseId)}  App={pointer.AppVersion}  切换于 {switchedAt}  by {pointer.SwitchedBy}");
                if (!string.IsNullOrEmpty(pointer.PreviousReleaseId))
                    EditorGUILayout.LabelField("历史链上一跳", Short(pointer.PreviousReleaseId));
            }
        }

        private void DrawReleases()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ReleaseEntryView entry in _snapshot.Releases)
            {
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label(StateLabel(entry.State), GUILayout.Width(72));
                    GUILayout.Label(Short(entry.ReleaseId), GUILayout.Width(90));
                    GUILayout.Label($"App {entry.AppVersion}  Res {entry.ResourceVersion}  Code {entry.CodeVersion}",
                        GUILayout.Width(180));
                    GUILayout.Label($"commit {Short(entry.GitCommit)}", GUILayout.Width(110));
                    GUILayout.Label(entry.GeneratedAtUtc ?? "-", GUILayout.MinWidth(120));

                    if (GUILayout.Button("台账", GUILayout.Width(40)))
                        EditorUtility.OpenWithDefaultApp(entry.LedgerPath);
                    using (new EditorGUI.DisabledScope(entry.State == ReleaseDisplayState.Incomplete))
                    {
                        if (GUILayout.Button("校验", GUILayout.Width(40)))
                            RunVerify(entry);
                        using (new EditorGUI.DisabledScope(entry.State == ReleaseDisplayState.Active))
                        {
                            if (GUILayout.Button("回滚到此", GUILayout.Width(64)))
                                RunRollback(entry);
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        // ── 操作全部复用管线层（与 CLI 同一实现）────────────────────────────────

        /// <summary>上传后校验重跑：与发布/回滚使用同一 VerifyLedgerArtifacts。</summary>
        private void RunVerify(ReleaseEntryView entry)
        {
            try
            {
                int count = ReleasePublishingSteps.VerifyLedgerArtifacts(
                    _snapshot.RootAbsolute, entry.LedgerPath, immutableOnly: true);
                EditorUtility.DisplayDialog("校验通过", $"{Short(entry.ReleaseId)}：{count} 个不可变产物与台账一致。", "好");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("校验失败", ex.Message, "关闭");
            }
        }

        /// <summary>dry-run：只跑环境门禁（ValidateReleaseEnvironment），不产生任何产物。</summary>
        private void RunDryRun(string env)
        {
            var ctx = new ReleaseContext
            {
                EnvironmentName = env,
                AppVersion = "dry-run",
                UploadRootOverride = _uploadRoot,
                Log = message => UnityEngine.Debug.Log("[ReleaseCenter] " + message),
            };
            try
            {
                new HotUpdateReleaseSteps.ValidateReleaseEnvironment().Execute(ctx);
                EditorUtility.DisplayDialog("dry-run 通过", $"环境 {env} 门禁全部通过（详见 Console）。", "好");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("dry-run 未通过", ex.Message, "关闭");
            }
        }

        /// <summary>回滚：dev 本地执行 ExecuteRollback（与 CLI 相同）；qa/prod 经 workflow 发起。</summary>
        private void RunRollback(ReleaseEntryView entry)
        {
            string env = CurrentEnv();
            if (env != "dev")
            {
                RunWorkflow($"-f kind=rollback -f releaseEnv={env} -f targetReleaseId={entry.ReleaseId}", null);
                return;
            }

            if (!EditorUtility.DisplayDialog("确认回滚",
                    $"将 dev 指针回切到 {Short(entry.ReleaseId)}（仅指针与别名变化，产物不重建）？", "回滚", "取消"))
                return;
            var ctx = new ReleaseContext
            {
                EnvironmentName = env,
                AppVersion = "rollback",
                UploadRootOverride = _uploadRoot,
                SwitchedBy = $"release-center@{Environment.UserName}",
                Log = message => UnityEngine.Debug.Log("[ReleaseCenter] " + message),
            };
            try
            {
                new HotUpdateReleaseSteps.ValidateReleaseEnvironment().Execute(ctx);
                ReleasePublishingSteps.ExecuteRollback(ctx, entry.ReleaseId);
                EditorUtility.DisplayDialog("回滚完成", $"dev 指针已指向 {Short(entry.ReleaseId)}。", "好");
                Refresh();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("回滚失败", ex.Message, "关闭");
            }
        }

        /// <summary>qa/prod 操作统一经 gh workflow run 触发 release.yml，审批链不可绕过。</summary>
        private void RunWorkflow(string fields, string extraHint)
        {
            string command = $"gh workflow run release.yml {fields}";
            UnityEngine.Debug.Log($"[ReleaseCenter] {command}");
            if (!string.IsNullOrEmpty(extraHint))
            {
                EditorUtility.DisplayDialog("需要补全输入", extraHint, "好");
                return;
            }
            if (!EditorUtility.DisplayDialog("经 workflow 发起", command + "\n\n确认发起？（prod 需在 GitHub 完成审批）", "发起", "取消"))
                return;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    Arguments = $"workflow run release.yml {fields}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(15000);
                    if (process.ExitCode == 0)
                        EditorUtility.DisplayDialog("已发起", "workflow 已触发，请前往 GitHub Actions 跟踪与审批。", "好");
                    else
                        EditorUtility.DisplayDialog("发起失败", string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, "关闭");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("发起失败",
                    $"无法执行 gh CLI：{ex.Message}\n请安装 GitHub CLI 并完成 gh auth login。", "关闭");
            }
        }

        private string CurrentEnv() =>
            _snapshot != null ? _snapshot.Scope.Split('/')[0] : "dev";

        private static string Short(string value) =>
            string.IsNullOrEmpty(value) ? "-" : (value.Length > 10 ? value.Substring(0, 10) : value);

        private static string StateLabel(ReleaseDisplayState state) => state switch
        {
            ReleaseDisplayState.Active => "★ Active",
            ReleaseDisplayState.Previous => "Previous",
            ReleaseDisplayState.Archived => "Archived",
            _ => "Incomplete",
        };
    }
}
