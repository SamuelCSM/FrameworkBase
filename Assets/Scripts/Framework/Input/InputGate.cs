using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Framework.Input
{
    /// <summary>
    /// 输入门禁，统一判断场景输入是否应被 UI 或全局屏蔽消费。
    /// </summary>
    public sealed class InputGate
    {
        /// <summary>全局输入屏蔽栈。</summary>
        private readonly InputBlockStack blockStack;

        /// <summary>UI 事件系统，用于检测指针是否位于 UGUI 上。</summary>
        private EventSystem eventSystem;

        /// <summary>UI 射线检测复用结果，避免频繁分配。</summary>
        private readonly List<RaycastResult> uiRaycastResults = new List<RaycastResult>(8);

        /// <summary>UI 射线检测复用事件数据。</summary>
        private PointerEventData uiPointerEventData;

        /// <summary>
        /// 创建输入门禁。
        /// </summary>
        /// <param name="blocks">全局输入屏蔽栈。</param>
        public InputGate(InputBlockStack blocks)
        {
            blockStack = blocks;
        }

        /// <summary>
        /// 注入 UI EventSystem 引用。
        /// </summary>
        /// <param name="uiEventSystem">全局 UI EventSystem。</param>
        public void SetEventSystem(EventSystem uiEventSystem)
        {
            eventSystem = uiEventSystem;
        }

        /// <summary>
        /// 全局输入是否被屏蔽（不含 UI 命中判断）。
        /// </summary>
        public bool IsGloballyBlocked => blockStack != null && blockStack.IsBlocked;

        /// <summary>
        /// 判断指定指针是否可以驱动 3D 场景交互。
        /// </summary>
        /// <param name="pointerId">指针 Id；鼠标为 0，触控为 fingerId。</param>
        /// <returns>允许场景交互时返回 true。</returns>
        public bool AllowScenePointer(int pointerId)
        {
            if (IsGloballyBlocked)
            {
                return false;
            }

            if (IsPointerOverUi(pointerId))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断指定指针是否可以驱动 3D 场景交互。
        /// </summary>
        /// <param name="pointerId">指针 Id；鼠标为 0，触控为 fingerId。</param>
        /// <param name="screenPosition">指针屏幕坐标。</param>
        /// <returns>允许场景交互时返回 true。</returns>
        public bool AllowScenePointer(int pointerId, Vector2 screenPosition)
        {
            if (IsGloballyBlocked)
            {
                return false;
            }

            if (IsPointerOverUi(pointerId, screenPosition))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断相机手势（缩放/平移）是否允许执行。
        /// </summary>
        /// <remarks>相机手势不受 UI 命中限制，但仍受全局屏蔽栈影响。</remarks>
        /// <returns>允许执行时返回 true。</returns>
        public bool AllowCameraGesture()
        {
            return !IsGloballyBlocked;
        }

        /// <summary>
        /// 判断指针是否位于 UGUI 可操作区域上。
        /// </summary>
        /// <param name="pointerId">指针 Id。</param>
        /// <returns>位于 UI 上时返回 true。</returns>
        public bool IsPointerOverUi(int pointerId)
        {
            if (eventSystem == null)
            {
                return false;
            }

            if (!UnityEngine.Input.touchSupported || UnityEngine.Input.touchCount <= 0)
            {
                return eventSystem.IsPointerOverGameObject();
            }

            return eventSystem.IsPointerOverGameObject(pointerId);
        }

        /// <summary>
        /// 判断指针是否位于 UGUI 可操作区域上。
        /// </summary>
        /// <param name="pointerId">指针 Id。</param>
        /// <param name="screenPosition">指针屏幕坐标。</param>
        /// <returns>位于 UI 上时返回 true。</returns>
        public bool IsPointerOverUi(int pointerId, Vector2 screenPosition)
        {
            if (eventSystem == null)
            {
                return false;
            }

            if (IsPointerOverUi(pointerId))
            {
                return true;
            }

            uiPointerEventData ??= new PointerEventData(eventSystem);
            uiPointerEventData.Reset();
            uiPointerEventData.pointerId = pointerId;
            uiPointerEventData.position = screenPosition;

            uiRaycastResults.Clear();
            eventSystem.RaycastAll(uiPointerEventData, uiRaycastResults);
            return uiRaycastResults.Count > 0;
        }
    }
}
