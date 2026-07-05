using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景相机 Rig 基类。
    /// </summary>
    /// <remarks>
    /// 子类只实现具体场景的相机规则；SceneContext 负责在场景进入、布局数据变化或表现层创建时调用
    /// <see cref="Apply"/>。该入口要求幂等，允许被重复调用。
    /// </remarks>
    public abstract class SceneCameraRigBase : MonoBehaviour, ISceneCameraRig
    {
        [Header("主相机")]
        public Camera mainCamera;

        [Header("编辑器预览开关")]
        public bool previewInEditMode = true;

        [Header("编辑器显示相机辅助线")]
        public bool drawPreviewGizmos = true;

        [Header("相机取景辅助线颜色")]
        public Color cameraBoundsGizmoColor = new Color(0.2f, 1f, 0.35f, 0.9f);

        /// <summary>Rig 当前是否已经成功应用过配置。</summary>
        private bool isApplied;

        /// <inheritdoc/>
        public Camera MainCamera => mainCamera;

        /// <inheritdoc/>
        public bool IsApplied => isApplied;

        /// <inheritdoc/>
        public virtual bool TryValidate(out string error)
        {
            if (mainCamera == null)
            {
                error = "mainCamera 未绑定";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <inheritdoc/>
        public void Apply()
        {
            if (!TryValidate(out string error))
            {
                isApplied = false;
                Debug.LogError("[" + GetType().Name + "] 相机 Rig 配置不完整：" + error);
                return;
            }

            ApplyInternal();
            isApplied = true;
        }

        /// <inheritdoc/>
        public void MarkDirty()
        {
            isApplied = false;
        }

        /// <summary>
        /// 子类实现的相机应用逻辑。
        /// </summary>
        protected abstract void ApplyInternal();

#if UNITY_EDITOR
        /// <summary>
        /// 使用编辑器预览参数立即应用一次相机取景。
        /// </summary>
        public virtual void ApplyEditorPreview()
        {
            Apply();
        }

        /// <summary>
        /// 编辑器参数变化时刷新预览相机，避免调参只能靠运行后观察。
        /// </summary>
        protected virtual void OnValidate()
        {
            NormalizeEditorPreviewSettings();
            if (!Application.isPlaying && previewInEditMode && mainCamera != null)
            {
                ApplyEditorPreview();
            }
        }

        /// <summary>
        /// 选中 Rig 时绘制通用相机辅助线。
        /// </summary>
        protected virtual void OnDrawGizmosSelected()
        {
            if (!drawPreviewGizmos)
            {
                return;
            }

            DrawPreviewGizmosInternal();
        }

        /// <summary>
        /// 子类可重写的编辑器预览参数规整入口。
        /// </summary>
        protected virtual void NormalizeEditorPreviewSettings()
        {
        }

        /// <summary>
        /// 绘制当前 Rig 的编辑器预览辅助线。
        /// </summary>
        protected virtual void DrawPreviewGizmosInternal()
        {
            DrawOrthographicCameraFrameGizmo();
        }

        /// <summary>
        /// 获取相机辅助线聚焦位置。
        /// </summary>
        /// <returns>辅助线聚焦位置。</returns>
        protected virtual Vector3 ResolvePreviewFocusPosition()
        {
            return transform.position;
        }

        /// <summary>
        /// 绘制正交相机取景边界。
        /// </summary>
        protected void DrawOrthographicCameraFrameGizmo()
        {
            if (mainCamera == null || !mainCamera.orthographic)
            {
                return;
            }

            float aspect = SceneCameraGizmoUtility.CalculateViewportAspect(mainCamera.rect);
            SceneCameraGizmoUtility.DrawRect(
                ResolvePreviewFocusPosition(),
                mainCamera.transform.right,
                mainCamera.transform.up,
                mainCamera.orthographicSize * aspect,
                mainCamera.orthographicSize,
                cameraBoundsGizmoColor);
        }
#endif
    }
}
