using Cysharp.Threading.Tasks;
using Framework.Core;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// 场景上下文组件（MonoBehaviour）。
    ///
    /// 每个业务场景在层级中放置一个继承自 SceneBase 的子类组件，管理场景内引用
    /// （出生点、灯光、相机、怪物刷点等），并提供进入/离开时的生命周期钩子。
    ///
    /// 自动注册到 SceneManager，外部通过 GameEntry.Scene.GetContext&lt;T&gt;() 获取。
    ///
    /// 用法：
    /// <code>
    /// public class BattleSceneContext : SceneBase
    /// {
    ///     public Transform playerSpawnPoint;
    ///     public Light mainLight;
    ///
    ///     public override async UniTask OnSceneEnterAsync()
    ///     {
    ///         GameEntry.Audio.PlayBGM("BGM_Battle_01");
    ///         await UniTask.CompletedTask;
    ///     }
    ///
    ///     public override async UniTask OnSceneExitAsync()
    ///     {
    ///         GameEntry.Audio.StopBGM();
    ///         await UniTask.CompletedTask;
    ///     }
    /// }
    /// </code>
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class SceneBase : MonoBehaviour
    {
        private bool _isRegistered;

        /// <summary>当前场景上下文是否已注册到 SceneManager。</summary>
        public bool IsRegistered => _isRegistered;

        /// <summary>
        /// Awake 中自动注册到 SceneManager。
        /// 若 SceneManager 尚未初始化（例如 Editor 中单独打开场景），跳过注册。
        /// </summary>
        protected virtual void Awake()
        {
            var sceneMgr = GameEntry.Scene;
            if (sceneMgr != null)
            {
                sceneMgr.RegisterContext(this);
                _isRegistered = true;
            }
        }

        protected virtual void OnDestroy()
        {
            if (!_isRegistered) return;

            var sceneMgr = GameEntry.Scene;
            if (sceneMgr != null)
                sceneMgr.UnregisterContext(this);

            _isRegistered = false;
        }

        /// <summary>
        /// 进入场景时调用（场景加载完成后）。
        /// 在此处播放 BGM、初始化场景状态、设置相机参数等。
        /// </summary>
        public virtual UniTask OnSceneEnterAsync()
        {
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// 离开场景时调用（新场景加载前）。
        /// 在此处停止 BGM、清理场景临时数据、恢复全局状态。
        /// </summary>
        public virtual UniTask OnSceneExitAsync()
        {
            return UniTask.CompletedTask;
        }
    }
}
