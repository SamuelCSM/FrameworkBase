using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Framework.Core.Auth
{
    /// <summary>登录后端接口（可替换 Mock/真实服务端实现）。</summary>
    public interface IAuthBackend
    {
        UniTask<LoginResult> LoginAsync(LoginRequestContext context, CancellationToken cancellationToken);
    }

    /// <summary>登录失败弹窗展示接口。</summary>
    public interface IAuthPopupPresenter
    {
        UniTask PresentAsync(AuthPopupDecision decision, Func<UniTask> retryHandler, Action exitHandler);
    }
}
