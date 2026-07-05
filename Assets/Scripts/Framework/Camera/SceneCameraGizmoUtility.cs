using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景相机 Rig 辅助线绘制工具。
    /// </summary>
    public static class SceneCameraGizmoUtility
    {
        /// <summary>
        /// 在 Scene 视图中绘制一个矩形辅助线。
        /// </summary>
        /// <param name="center">矩形中心。</param>
        /// <param name="right">矩形右方向。</param>
        /// <param name="up">矩形上方向。</param>
        /// <param name="halfWidth">半宽。</param>
        /// <param name="halfHeight">半高。</param>
        /// <param name="color">辅助线颜色。</param>
        public static void DrawRect(Vector3 center, Vector3 right, Vector3 up, float halfWidth, float halfHeight, Color color)
        {
            Vector3 normalizedRight = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
            Vector3 normalizedUp = up.sqrMagnitude > 0.0001f ? up.normalized : Vector3.up;
            Vector3 topLeft = center - normalizedRight * halfWidth + normalizedUp * halfHeight;
            Vector3 topRight = center + normalizedRight * halfWidth + normalizedUp * halfHeight;
            Vector3 bottomRight = center + normalizedRight * halfWidth - normalizedUp * halfHeight;
            Vector3 bottomLeft = center - normalizedRight * halfWidth - normalizedUp * halfHeight;

            Gizmos.color = color;
            Gizmos.DrawLine(topLeft, topRight);
            Gizmos.DrawLine(topRight, bottomRight);
            Gizmos.DrawLine(bottomRight, bottomLeft);
            Gizmos.DrawLine(bottomLeft, topLeft);
        }

        /// <summary>
        /// 计算相机视口宽高比。
        /// </summary>
        /// <param name="viewport">归一化相机视口。</param>
        /// <returns>视口宽高比。</returns>
        public static float CalculateViewportAspect(Rect viewport)
        {
            float screenWidth = Mathf.Max(1, Screen.width);
            float screenHeight = Mathf.Max(1, Screen.height);
            float pixelWidth = Mathf.Max(1f, viewport.width * screenWidth);
            float pixelHeight = Mathf.Max(1f, viewport.height * screenHeight);
            return Mathf.Max(0.01f, pixelWidth / pixelHeight);
        }
    }
}
