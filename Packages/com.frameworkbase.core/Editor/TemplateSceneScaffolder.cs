using System;
using System.IO;
using Framework.Core;
using Framework.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Framework.Editor
{
    /// <summary>
    /// 模板启动壳脚手架（垂直切片 A，见 Docs/TemplateVerticalSliceDesign.md）。
    ///
    /// 一键把 Launch 场景从"裸场景"补成"零噪声启动壳"：
    ///   1. 确保 TMP Essential Resources 已导入（新工程首次必踩坑，自动处理）；
    ///   2. 生成 CJK 动态回退字体并挂入 TMP Settings（开发期中文可读；正式包走字体子集，见评审差距项）；
    ///   3. 程序化生成灰盒 LoadingView / LoginView 预制体（纯 UGUI，禁美术投入）；
    ///   4. 重建 Launch.unity 接线：UIBootstrap（EventSystem + UIRoot Canvas）+ _GameEntry 全字段赋值。
    ///
    /// 与 <see cref="ProjectScaffoldWindow"/>（PlayerSettings / AppConfig）互补：
    /// 那边管"应用标识"，这边管"场景与 UI 基础设施"。均可重复执行（幂等：同名节点先删后建）。
    ///
    /// batchmode 入口 <see cref="SetupLaunchScene"/> 失败时抛异常（规避 batchmode 退出码不可靠），
    /// 成功打印 ASCII 哨兵 <c>TEMPLATE_SCAFFOLD_OK</c> 供 CI 轮询日志判定。
    /// </summary>
    public static class TemplateSceneScaffolder
    {
        private const string TemplateUiFolder  = "Assets/FrameworkTemplate/UI";
        private const string LoadingPrefabPath = TemplateUiFolder + "/LoadingView.prefab";
        private const string LoginPrefabPath   = TemplateUiFolder + "/LoginView.prefab";
        private const string LaunchScenePath   = "Assets/Scenes/Launch.unity";
        private const string FontsFolder       = "Assets/FrameworkTemplate/Fonts";
        private const string FontsResFolder    = FontsFolder + "/Resources";
        private const string CjkFontFilePath   = FontsResFolder + "/CjkDevFallback.ttc";
        private const string CjkFontAssetPath  = FontsResFolder + "/CjkDevFallback SDF.asset";

        // 灰盒配色：深底 + 亮字 + 单一强调色，不引入任何美术资源
        private static readonly Color BgDark     = new Color(0.086f, 0.098f, 0.133f, 1f);
        private static readonly Color PanelDark  = new Color(0.157f, 0.176f, 0.227f, 1f);
        private static readonly Color Accent     = new Color(0.290f, 0.565f, 0.886f, 1f);
        private static readonly Color TextBright = new Color(0.92f, 0.93f, 0.95f, 1f);
        private static readonly Color TextDim    = new Color(0.62f, 0.65f, 0.70f, 1f);

        [MenuItem("Framework/Template/Setup Launch Scene (灰盒启动壳)")]
        public static void SetupLaunchSceneMenu()
        {
            SetupLaunchScene();
            EditorUtility.DisplayDialog("Template Scaffolder",
                "启动壳已生成：灰盒预制体 + Launch 场景接线完成。\n直接打开 Launch.unity 按 Play 验收。", "确定");
        }

        /// <summary>
        /// batchmode 入口：Unity.exe -batchmode -executeMethod Framework.Editor.TemplateSceneScaffolder.SetupLaunchScene
        /// </summary>
        public static void SetupLaunchScene()
        {
            EnsureTmpEssentials();
            EnsureFolder(TemplateUiFolder);
            TryCreateCjkFallbackFont();

            LoadingView loadingPrefab = CreateLoadingViewPrefab();
            LoginView   loginPrefab   = CreateLoginViewPrefab();
            WireLaunchScene(loadingPrefab, loginPrefab);

            AssetDatabase.SaveAssets();
            Debug.Log("[TemplateScaffolder] TEMPLATE_SCAFFOLD_OK 启动壳生成完成：" +
                      $"{LoadingPrefabPath} / {LoginPrefabPath} / {LaunchScenePath}");
        }

        // ── Step 1: TMP 基础资源 ─────────────────────────────────────────────

        /// <summary>
        /// TMP Essential Resources 未导入时自动导入（否则任何 TextMeshProUGUI 都会刷错误弹窗）。
        /// 新项目派生自壳工程时这是首个必踩坑，脚手架直接吃掉。
        /// <para>
        /// 注意：不走 <see cref="AssetDatabase.ImportPackage(string, bool)"/>——它在 batchmode 下异步排队，
        /// -quit 时永远等不到导入完成（已实测）。unitypackage 本质是 tar.gz（GUID 目录 + asset/asset.meta/pathname），
        /// 这里直接同步解包落盘再 Refresh，batchmode / 交互模式行为一致。
        /// </para>
        /// </summary>
        private static void EnsureTmpEssentials()
        {
            if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
                return;

            const string pkg = "Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage";
            if (!File.Exists(pkg))
                throw new InvalidOperationException($"[TemplateScaffolder] 找不到 TMP 资源包：{pkg}");

            Debug.Log("[TemplateScaffolder] TMP Essential Resources 未导入，开始同步解包导入...");
            int count = ExtractUnityPackage(pkg);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            if (!File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
                throw new InvalidOperationException("[TemplateScaffolder] TMP Essential Resources 导入失败，请手动导入后重试");
            Debug.Log($"[TemplateScaffolder] TMP Essential Resources 导入完成（{count} 个条目）");
        }

        /// <summary>
        /// 同步解包 .unitypackage（tar.gz）：每个 GUID 目录含 pathname（目标路径）、asset（内容，文件夹条目无）、
        /// asset.meta（GUID 元数据，必须一并落盘，否则引用全断）。返回落盘条目数。
        /// </summary>
        private static int ExtractUnityPackage(string packagePath)
        {
            // GUID → (pathname, assetBytes, metaBytes)
            var entries = new System.Collections.Generic.Dictionary<string, (string pathname, byte[] asset, byte[] meta)>();

            using (var fileStream = File.OpenRead(packagePath))
            using (var gzip = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadExactly(gzip, header, 512))
                {
                    // 全零块 = tar 结束标记
                    if (header[0] == 0)
                        break;

                    string name = ParseTarString(header, 0, 100);
                    string prefix = ParseTarString(header, 345, 155);
                    if (prefix.Length > 0)
                        name = prefix + "/" + name;
                    long sizeBytes = Convert.ToInt64(ParseTarString(header, 124, 12).Trim(), 8);
                    byte typeFlag = header[156];

                    var content = new byte[sizeBytes];
                    if (sizeBytes > 0 && !ReadExactly(gzip, content, (int)sizeBytes))
                        throw new InvalidOperationException($"[TemplateScaffolder] tar 条目截断：{name}");
                    // 跳过 512 对齐填充
                    int pad = (int)((512 - sizeBytes % 512) % 512);
                    if (pad > 0)
                        ReadExactly(gzip, new byte[pad], pad);

                    if (typeFlag == (byte)'5') // 目录条目
                        continue;

                    // 条目名形如 "./<guid>/asset" 或 "<guid>/pathname"
                    string trimmed = name.StartsWith("./") ? name.Substring(2) : name;
                    int slash = trimmed.IndexOf('/');
                    if (slash <= 0)
                        continue;
                    string guid = trimmed.Substring(0, slash);
                    string kind = trimmed.Substring(slash + 1);

                    if (!entries.TryGetValue(guid, out var e))
                        e = (null, null, null);
                    switch (kind)
                    {
                        // pathname 文件首行是目标路径（部分包第二行有附加数据）
                        case "pathname":   e.pathname = System.Text.Encoding.UTF8.GetString(content)
                                                              .Split('\n')[0].Trim(); break;
                        case "asset":      e.asset = content; break;
                        case "asset.meta": e.meta = content; break;
                    }
                    entries[guid] = e;
                }
            }

            int written = 0;
            foreach (var e in entries.Values)
            {
                if (string.IsNullOrEmpty(e.pathname) || e.meta == null)
                    continue;
                // asset 为空 = 文件夹条目：建目录 + 落 meta；否则落文件 + meta
                if (e.asset == null)
                    Directory.CreateDirectory(e.pathname);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(e.pathname));
                    File.WriteAllBytes(e.pathname, e.asset);
                }
                File.WriteAllBytes(e.pathname + ".meta", e.meta);
                written++;
            }
            return written;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    return false;
                offset += read;
            }
            return true;
        }

        private static string ParseTarString(byte[] header, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && header[end] != 0)
                end++;
            return System.Text.Encoding.ASCII.GetString(header, offset, end - offset);
        }

        /// <summary>
        /// 生成开发期 CJK 回退字体资产（本机专用，整个 Fonts 目录 gitignore，不进仓库）。
        ///
        /// 约束链与决策：
        ///   - 系统中文字体（微软雅黑）专有授权，不能提交进仓库 → 拷贝到 gitignore 目录仅本机使用；
        ///   - TMP 3.0.7 的 CreateFontAsset 只认"含字体数据的工程内 Font 资产"，
        ///     OS 动态字体（CreateDynamicFontFromOSFont）走 LoadFontFace 必失败（已实测）→ 先拷入工程再导入；
        ///   - TMP Settings 是已提交资产，而本地生成的字体资产每台机器 GUID 不同 → 不写 TMP Settings，
        ///     由随仓库提交的 <c>TemplateDevFontFallback</c> 运行时钩子按 Resources 路径加载（与 GUID 无关）。
        ///
        /// 仅解决开发期"中文显示为方框"；正式包体的字体子集化是既有评审差距项，不在本脚手架范围。
        /// 失败不阻断（灰盒验收允许英文兜底），仅告警。
        /// </summary>
        private static void TryCreateCjkFallbackFont()
        {
            try
            {
                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(CjkFontAssetPath) != null)
                    return;

                // 候选系统字体（按覆盖面排序）；都找不到就跳过（如 Linux CI 容器）
                string osFontFile = null;
                foreach (string candidate in new[]
                         {
                             @"C:\Windows\Fonts\msyh.ttc",   // 微软雅黑
                             @"C:\Windows\Fonts\msyhl.ttc",  // 微软雅黑 Light
                             @"C:\Windows\Fonts\simhei.ttf", // 黑体
                         })
                {
                    if (File.Exists(candidate)) { osFontFile = candidate; break; }
                }
                if (osFontFile == null)
                {
                    Debug.LogWarning("[TemplateScaffolder] 未找到系统中文字体，跳过 CJK 回退字体生成（界面中文将显示为方框）");
                    return;
                }

                EnsureFolder(FontsResFolder);
                WriteFontsGitignore();

                // 拷入工程并同步导入为 Font 资产（含字体数据），TMP 才能 LoadFontFace
                string targetFile = Path.ChangeExtension(CjkFontFilePath, Path.GetExtension(osFontFile));
                File.Copy(osFontFile, targetFile, overwrite: true);
                AssetDatabase.ImportAsset(targetFile, ImportAssetOptions.ForceSynchronousImport);
                var projectFont = AssetDatabase.LoadAssetAtPath<Font>(targetFile);
                if (projectFont == null)
                {
                    Debug.LogWarning($"[TemplateScaffolder] 字体导入失败：{targetFile}，跳过 CJK 回退字体生成");
                    return;
                }

                TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                    projectFont, 60, 8,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    1024, 1024,
                    AtlasPopulationMode.Dynamic);
                if (fontAsset == null)
                {
                    Debug.LogWarning("[TemplateScaffolder] CJK 动态字体资产创建失败，跳过");
                    return;
                }

                fontAsset.name = Path.GetFileNameWithoutExtension(CjkFontAssetPath);
                AssetDatabase.CreateAsset(fontAsset, CjkFontAssetPath);
                fontAsset.material.name = fontAsset.name + " Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                if (fontAsset.atlasTextures != null && fontAsset.atlasTextures.Length > 0)
                {
                    fontAsset.atlasTextures[0].name = fontAsset.name + " Atlas";
                    AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[0], fontAsset);
                }

                Debug.Log($"[TemplateScaffolder] CJK 回退字体已生成（本机专用，不入库）：{CjkFontAssetPath}，" +
                          "运行时由 TemplateDevFontFallback 挂入 TMP 回退链");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TemplateScaffolder] CJK 回退字体生成失败（不阻断，界面中文可能显示为方框）：{ex.Message}");
            }
        }

        /// <summary>Fonts 目录整体不入库（系统字体授权 + 本机 GUID），只保留 .gitignore 自身。</summary>
        private static void WriteFontsGitignore()
        {
            string path = FontsFolder + "/.gitignore";
            if (!File.Exists(path))
                File.WriteAllText(path, "# 开发期本机字体（系统字体授权不可再分发；资产 GUID 每机不同）\n*\n!.gitignore\n");
        }

        // ── Step 2: LoadingView 灰盒预制体 ───────────────────────────────────

        private static LoadingView CreateLoadingViewPrefab()
        {
            GameObject root = NewUiRoot("LoadingView");
            try
            {
                root.AddComponent<CanvasGroup>();
                var view = root.AddComponent<LoadingView>();

                CreateImage(root.transform, "BG", BgDark, stretch: true);

                view.versionText = CreateText(root.transform, "VersionText", "v-.-.-",
                    24, TextAlignmentOptions.BottomLeft, TextDim,
                    anchor(0, 0), pos(24, 16), size(600, 36), pivot(0, 0));

                view.statusText = CreateText(root.transform, "StatusText", "Loading...",
                    32, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 0), pos(0, 200), size(1200, 44));

                // 进度条：Background + FillArea/Fill 手工构建（不依赖 DefaultControls 的 Sprite）
                var sliderGo = NewChild(root.transform, "ProgressBar");
                SetRect(sliderGo, anchor(0.5f, 0), pos(0, 140), size(1200, 28));
                var sliderBg = CreateImage(sliderGo.transform, "Background", PanelDark, stretch: true);
                var fillArea = NewChild(sliderGo.transform, "Fill Area");
                SetRect(fillArea, anchor(0.5f, 0.5f), pos(0, 0), size(0, 0));
                StretchFull(fillArea);
                var fill = CreateImage(fillArea.transform, "Fill", Accent, stretch: false);
                SetRect(fill.gameObject, anchor(0, 0.5f), pos(0, 0), size(0, 0));
                var fillRt = fill.GetComponent<RectTransform>();
                fillRt.anchorMin = Vector2.zero;
                fillRt.anchorMax = new Vector2(0, 1);
                fillRt.sizeDelta = Vector2.zero;

                var slider = sliderGo.AddComponent<Slider>();
                slider.interactable   = false;
                slider.transition     = Selectable.Transition.None;
                slider.targetGraphic  = sliderBg;
                slider.fillRect       = fillRt;
                slider.direction      = Slider.Direction.LeftToRight;
                slider.minValue       = 0f;
                slider.maxValue       = 1f;
                slider.value          = 0f;
                view.progressBar = slider;

                view.progressText = CreateText(sliderGo.transform, "ProgressText", "0%",
                    22, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 0.5f), pos(0, 0), size(200, 30));

                view.downloadText = CreateText(root.transform, "DownloadText", "",
                    22, TextAlignmentOptions.Center, TextDim,
                    anchor(0.5f, 0), pos(0, 96), size(1200, 30));

                // 错误面板（默认隐藏）：遮罩 + 面板 + 消息 + 重试/退出
                var errorPanel = CreateOverlayPanel(root.transform, "ErrorPanel", size(720, 380),
                    out RectTransform errorInner);
                view.errorPanel = errorPanel;
                view.errorMessageText = CreateText(errorInner, "ErrorMessage", "Error",
                    28, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 1), pos(0, -40), size(640, 180), pivot(0.5f, 1));
                view.retryButton = CreateButton(errorInner, "RetryButton", "Retry",
                    anchor(0.5f, 0), pos(-140, 48), size(220, 72));
                view.exitButton = CreateButton(errorInner, "ExitButton", "Exit",
                    anchor(0.5f, 0), pos(140, 48), size(220, 72));
                errorPanel.SetActive(false);

                // 整包更新面板（默认隐藏）
                var forcePanel = CreateOverlayPanel(root.transform, "ForceUpdatePanel", size(720, 380),
                    out RectTransform forceInner);
                view.forceUpdatePanel = forcePanel;
                view.updateDescText = CreateText(forceInner, "UpdateDesc", "New version available",
                    28, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 1), pos(0, -40), size(640, 180), pivot(0.5f, 1));
                view.updateButton = CreateButton(forceInner, "UpdateButton", "Update",
                    anchor(0.5f, 0), pos(0, 48), size(280, 72));
                forcePanel.SetActive(false);

                view.canvasGroup = root.GetComponent<CanvasGroup>();

                return SavePrefab(root, LoadingPrefabPath).GetComponent<LoadingView>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ── Step 3: LoginView 灰盒预制体 ─────────────────────────────────────

        private static LoginView CreateLoginViewPrefab()
        {
            GameObject root = NewUiRoot("LoginView");
            try
            {
                root.AddComponent<CanvasGroup>();
                var view = root.AddComponent<LoginView>();

                CreateImage(root.transform, "BG", BgDark, stretch: true);

                CreateText(root.transform, "Title", "LOGIN",
                    56, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 1), pos(0, -160), size(600, 80), pivot(0.5f, 1));

                view.accountInput = CreateInputField(root.transform, "AccountInput", "Account",
                    anchor(0.5f, 0.5f), pos(0, 90), size(620, 78), password: false);
                view.passwordInput = CreateInputField(root.transform, "PasswordInput", "Password",
                    anchor(0.5f, 0.5f), pos(0, -10), size(620, 78), password: true);

                view.accountLoginButton = CreateButton(root.transform, "AccountLoginButton", "Account Login",
                    anchor(0.5f, 0.5f), pos(0, -130), size(620, 84));
                view.guestLoginButton = CreateButton(root.transform, "GuestLoginButton", "Guest Login",
                    anchor(0.5f, 0.5f), pos(0, -240), size(620, 84));

                view.statusText = CreateText(root.transform, "StatusText", "",
                    26, TextAlignmentOptions.Center, TextDim,
                    anchor(0.5f, 0), pos(0, 90), size(1000, 36));
                view.versionText = CreateText(root.transform, "VersionText", "v-.-.-",
                    24, TextAlignmentOptions.BottomLeft, TextDim,
                    anchor(0, 0), pos(24, 16), size(600, 36), pivot(0, 0));

                var errorPanel = CreateOverlayPanel(root.transform, "ErrorPanel", size(720, 380),
                    out RectTransform errorInner);
                view.errorPanel = errorPanel;
                view.errorMessageText = CreateText(errorInner, "ErrorMessage", "Error",
                    28, TextAlignmentOptions.Center, TextBright,
                    anchor(0.5f, 1), pos(0, -40), size(640, 180), pivot(0.5f, 1));
                view.retryButton = CreateButton(errorInner, "RetryButton", "Retry",
                    anchor(0.5f, 0), pos(-140, 48), size(220, 72));
                view.exitButton = CreateButton(errorInner, "ExitButton", "Exit",
                    anchor(0.5f, 0), pos(140, 48), size(220, 72));
                errorPanel.SetActive(false);

                view.canvasGroup = root.GetComponent<CanvasGroup>();

                return SavePrefab(root, LoginPrefabPath).GetComponent<LoginView>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ── Step 4: Launch 场景接线 ──────────────────────────────────────────

        private static void WireLaunchScene(LoadingView loadingPrefab, LoginView loginPrefab)
        {
            var scene = EditorSceneManager.OpenScene(LaunchScenePath, OpenSceneMode.Single);

            // 幂等：脚手架自建节点先删后建；相机 / 灯光等既有节点不动
            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                if (rootGo.name == "_GameEntry" || rootGo.name == "UIBootstrap")
                    UnityEngine.Object.DestroyImmediate(rootGo);
            }

            // UIBootstrap：EventSystem + UIRoot Canvas（层级 Canvas 由 UIBootstrap.Awake 动态生成）
            var bootstrapGo = new GameObject("UIBootstrap");
            var bootstrap   = bootstrapGo.AddComponent<UIBootstrap>();

            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.transform.SetParent(bootstrapGo.transform, false);
            var eventSystem = eventSystemGo.AddComponent<EventSystem>();
            var inputModule = eventSystemGo.AddComponent<StandaloneInputModule>();

            var uiRootGo = new GameObject("UIRoot");
            uiRootGo.layer = LayerMask.NameToLayer("UI");
            uiRootGo.transform.SetParent(bootstrapGo.transform, false);
            var canvas = uiRootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = uiRootGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight  = 0.5f; // 运行时由 CanvasScalerAutoMatch 按屏幕比动态接管
            uiRootGo.AddComponent<GraphicRaycaster>();

            var bootstrapSo = new SerializedObject(bootstrap);
            bootstrapSo.FindProperty("_uiRootCanvas").objectReferenceValue = canvas;
            bootstrapSo.FindProperty("_eventSystem").objectReferenceValue  = eventSystem;
            bootstrapSo.FindProperty("_inputModule").objectReferenceValue  = inputModule;
            bootstrapSo.ApplyModifiedPropertiesWithoutUndo();

            // _GameEntry：全 Inspector 字段接满，消灭"预期噪声"
            var entryGo = new GameObject("_GameEntry");
            var entry   = entryGo.AddComponent<GameEntry>();
            var entrySo = new SerializedObject(entry);
            entrySo.FindProperty("_uiBootstrap").objectReferenceValue       = bootstrap;
            entrySo.FindProperty("_loadingViewPrefab").objectReferenceValue = loadingPrefab;
            entrySo.FindProperty("_loginViewPrefab").objectReferenceValue   = loginPrefab;
            entrySo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"[TemplateScaffolder] 场景保存失败：{LaunchScenePath}");

            Debug.Log($"[TemplateScaffolder] Launch 场景接线完成：UIBootstrap + _GameEntry（{LaunchScenePath}）");
        }

        // ── UGUI 灰盒构建工具 ────────────────────────────────────────────────

        private static GameObject NewUiRoot(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            StretchFull(go);
            return go;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = Vector2.zero;
            rt.anchorMax        = Vector2.one;
            rt.sizeDelta        = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }

        private static void SetRect(GameObject go, Vector2 anchorPoint, Vector2 position, Vector2 sizeDelta,
            Vector2? pivotPoint = null)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = anchorPoint;
            rt.anchorMax        = anchorPoint;
            rt.pivot            = pivotPoint ?? new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta        = sizeDelta;
        }

        private static Image CreateImage(Transform parent, string name, Color color, bool stretch)
        {
            var go = NewChild(parent, name);
            if (stretch) StretchFull(go);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text,
            float fontSize, TextAlignmentOptions align, Color color,
            Vector2 anchorPoint, Vector2 position, Vector2 sizeDelta, Vector2? pivotPoint = null)
        {
            var go = NewChild(parent, name);
            SetRect(go, anchorPoint, position, sizeDelta, pivotPoint);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text          = text;
            tmp.fontSize      = fontSize;
            tmp.alignment     = align;
            tmp.color         = color;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static Button CreateButton(Transform parent, string name, string label,
            Vector2 anchorPoint, Vector2 position, Vector2 sizeDelta)
        {
            var go = NewChild(parent, name);
            SetRect(go, anchorPoint, position, sizeDelta);
            var img = go.AddComponent<Image>();
            img.color = Accent;
            var button = go.AddComponent<Button>();
            button.targetGraphic = img;

            var text = CreateText(go.transform, "Label", label, 28,
                TextAlignmentOptions.Center, Color.white,
                anchor(0.5f, 0.5f), pos(0, 0), size(0, 0));
            StretchFull(text.gameObject);
            return button;
        }

        /// <summary>遮罩 + 居中面板的两层结构（错误弹窗 / 整包更新弹窗共用），返回遮罩根，出参内层面板。</summary>
        private static GameObject CreateOverlayPanel(Transform parent, string name, Vector2 panelSize,
            out RectTransform inner)
        {
            var mask = NewChild(parent, name);
            StretchFull(mask);
            var maskImg = mask.AddComponent<Image>();
            maskImg.color = new Color(0f, 0f, 0f, 0.6f);

            var panel = NewChild(mask.transform, "Panel");
            SetRect(panel, anchor(0.5f, 0.5f), pos(0, 0), panelSize);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = PanelDark;

            inner = panel.GetComponent<RectTransform>();
            return mask;
        }

        private static TMP_InputField CreateInputField(Transform parent, string name, string placeholder,
            Vector2 anchorPoint, Vector2 position, Vector2 sizeDelta, bool password)
        {
            var go = NewChild(parent, name);
            SetRect(go, anchorPoint, position, sizeDelta);
            var bg = go.AddComponent<Image>();
            bg.color = PanelDark;

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;

            // Text Area + Placeholder + Text 三件套（TMP_InputField 约定结构）
            var textArea = NewChild(go.transform, "Text Area");
            StretchFull(textArea);
            var textAreaRt = textArea.GetComponent<RectTransform>();
            textAreaRt.offsetMin = new Vector2(20, 8);
            textAreaRt.offsetMax = new Vector2(-20, -8);
            textArea.AddComponent<RectMask2D>();

            var placeholderTmp = CreateText(textArea.transform, "Placeholder", placeholder,
                28, TextAlignmentOptions.Left, TextDim,
                anchor(0.5f, 0.5f), pos(0, 0), size(0, 0));
            StretchFull(placeholderTmp.gameObject);
            placeholderTmp.fontStyle = FontStyles.Italic;

            var textTmp = CreateText(textArea.transform, "Text", string.Empty,
                28, TextAlignmentOptions.Left, TextBright,
                anchor(0.5f, 0.5f), pos(0, 0), size(0, 0));
            StretchFull(textTmp.gameObject);

            input.textViewport  = textAreaRt;
            input.textComponent = textTmp;
            input.placeholder   = placeholderTmp;
            input.contentType   = password ? TMP_InputField.ContentType.Password
                                           : TMP_InputField.ContentType.Standard;
            return input;
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            if (!success || prefab == null)
                throw new InvalidOperationException($"[TemplateScaffolder] 预制体保存失败：{path}");
            Debug.Log($"[TemplateScaffolder] 灰盒预制体已生成：{path}");
            return prefab;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // 短工厂：让上面的布局代码读起来像坐标表
        private static Vector2 anchor(float x, float y) => new Vector2(x, y);
        private static Vector2 pos(float x, float y)    => new Vector2(x, y);
        private static Vector2 size(float x, float y)   => new Vector2(x, y);
        private static Vector2 pivot(float x, float y)  => new Vector2(x, y);
    }
}
