using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Framework.UI
{
    /// <summary>
    /// 登录界面视图层（随包 Prefab，不含 Canvas）。
    /// 由 GameEntry 实例化到 Canvas_System，再由 LoginWindow 驱动。
    /// </summary>
    public class LoginView : MonoBehaviour
    {
        [Header("登录表单")]
        public TMP_InputField accountInput;
        public TMP_InputField passwordInput;
        public Button guestLoginButton;
        public Button accountLoginButton;

        [Header("状态")]
        public TextMeshProUGUI statusText;
        public TextMeshProUGUI versionText;

        [Header("错误面板")]
        public GameObject errorPanel;
        public TextMeshProUGUI errorMessageText;
        public Button retryButton;
        public Button exitButton;

        /// <summary>用于淡出的 CanvasGroup（可与 Loading 一致挂在根节点）。</summary>
        [HideInInspector] public CanvasGroup canvasGroup;

        private void Awake()
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            if (errorPanel != null)
                errorPanel.SetActive(false);
        }
    }
}
