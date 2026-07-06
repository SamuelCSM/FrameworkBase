using Cysharp.Threading.Tasks;

namespace Framework
{
    /// <summary>
    /// 游戏阶段抽象基类。
    ///
    /// 每个游戏阶段描述一组"加载什么场景、使用什么过场、进入/离开时做什么"。
    /// 由 <see cref="GameStageManager"/> 统一编排生命周期；
    /// 由 <see cref="GameStageNavigationManager"/> 维护 Push / Back 返回栈。
    /// <see cref="OnEnterAsync"/> 会在过场遮罩仍显示期间调用，让 UI 打开等操作在幕后完成。
    ///
    /// 用法：
    /// <code>
    /// await GameEntry.StageNavigation.PushStageAsync(new BattleStage(levelId: 101));
    /// </code>
    /// </summary>
    public abstract class GameStage
    {
        /// <summary>要切换到的场景 Addressables 地址。返回 null 表示不切换场景（纯 UI 切换）。</summary>
        protected abstract string SceneAddress { get; }

        /// <summary>场景切换过场配置。返回 null 表示使用 <see cref="SceneTransitionConfig.Standard"/>。</summary>
        protected abstract SceneTransitionConfig Transition { get; }

        /// <summary>
        /// 进入阶段时调用（场景已加载、过场遮罩仍显示中）。
        /// 在此处打开 UI、初始化场景对象、设置玩家位置等。
        /// 打开 UI 的加载耗时会隐藏在遮罩下，不会让用户看到中间态。
        /// 若阶段会被压入返回栈，阶段实例应只保存可重建数据，不保存已卸载场景中的 Unity 对象引用。
        /// </summary>
        protected internal abstract UniTask OnEnterAsync();

        /// <summary>
        /// 离开阶段时调用（新阶段开始加载之前）。
        /// 在此处关闭 UI、停止音频、保存数据等。
        /// </summary>
        protected internal abstract UniTask OnExitAsync();

        /// <summary>获取场景地址。</summary>
        internal string GetSceneAddress() => SceneAddress;

        /// <summary>获取过场配置。</summary>
        internal SceneTransitionConfig GetTransition() => Transition;
    }
}
