namespace Framework
{
    /// <summary>
    /// UI层级枚举
    /// 定义UI的显示层级，数值越大显示越靠前
    /// </summary>
    public enum UILayer
    {
        /// <summary>
        /// 背景层 - 用于背景UI，如主界面背景
        /// </summary>
        Background = 0,

        /// <summary>
        /// 普通层 - 用于常规UI，如主界面、背包等
        /// </summary>
        Normal = 1,

        /// <summary>
        /// 弹窗层 - 用于弹出窗口，如对话框、提示框
        /// </summary>
        Popup = 2,

        /// <summary>
        /// 顶层 - 用于需要显示在最上层的UI，如引导、公告
        /// </summary>
        Top = 3,

        /// <summary>
        /// 系统层 - 用于系统级UI，如加载界面、网络错误提示
        /// </summary>
        System = 4
    }
}
