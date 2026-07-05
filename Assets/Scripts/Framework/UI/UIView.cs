using UnityEngine;

namespace Framework
{
    /// <summary>
    /// UI 视图基类（纯数据容器）
    ///
    /// 职责：仅持有序列化 UI 组件引用，在 Inspector 中拖拽赋值，不包含任何逻辑。
    ///
    /// 所有 Button / TMP_Text / Slider / GameObject 等字段均可直接使用
    /// <see cref="UIExtensions"/> 提供的扩展方法操作：
    ///   View.btnClose.AddClick(() => Close());
    ///   View.lblTitle.SetText("Hello");
    ///   View.progressBar.SetProgress(0.5f);
    ///   View.panelError.SetVisible(false);
    ///
    /// 子类示例：
    ///   public class LoginView : UIView
    ///   {
    ///       public Button      btnLogin;
    ///       public TMP_Text    lblWelcome;
    ///       public Slider      progressBar;
    ///       public GameObject  panelError;
    ///   }
    /// </summary>
    public abstract class UIView : MonoBehaviour { }
}

