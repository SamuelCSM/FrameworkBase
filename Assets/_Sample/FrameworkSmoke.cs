using Framework;
using Framework.Core;
using UnityEngine;

/// <summary>
/// 地基冒烟自检（Sample，编译进 Assembly-CSharp，不属于 Framework 程序集）。
/// 验证 GameEntry 各 Manager 已启动、Timer 真正在运转。
///
/// 用法：
/// 1) 新建空场景，放一个空 GameObject；
/// 2) 挂上 GameEntry 与本组件（本组件在 Start 运行，晚于 GameEntry.Awake 的 Manager 初始化）；
/// 3) Resources/AppConfig.asset 已设 EnableHotUpdate=0 / UseNetworkLogin=0（纯框架离线）；
/// 4) 按 Play，Console 出现 "✅ Framework OK" 与 0.5s 后的 Timer 回调即通过。
///
/// 注 1：本冒烟场景不接 Loading 预制体，GameEntry.Start 会打印一条 _loadingViewPrefab 未赋值的 Error——
/// 属预期（真实项目需拖 Loading 预制体驱动完整启动序列）；Manager 已在 Awake 初始化，不影响本自检。
///
/// 注 2：本文件属 Assembly-CSharp（AOT），<b>严禁引用热更程序集</b>（GameProtocol/HotUpdate 均在
/// HybridCLRSettings 的 hotUpdateAssemblies 中，AOT 裁剪时被剔除，直接引用会导致 IL2CPP 链接期
/// LinkerFatalError 无法解析程序集、整包构建中断）。故协议序列化/组包往返自检不在此处，改由
/// EditMode 的 MessagePacketTests 与发布演练 ReleaseRehearsalTests（走真实热更程序集路径）覆盖。
/// </summary>
public sealed class FrameworkSmoke : MonoBehaviour
{
    private void Start()
    {
        bool ok = GameEntry.Event != null
                  && GameEntry.Timer != null
                  && GameEntry.Resource != null
                  && GameEntry.UI != null
                  && GameEntry.Network != null
                  && GameEntry.RefData != null
                  && GameEntry.Audio != null
                  && GameEntry.Scene != null
                  && GameEntry.Auth != null;

        if (!ok)
        {
            GameLog.Error("[FrameworkSmoke] ❌ 部分 Manager 未初始化，确认场景中有 GameEntry 且其 Awake 先于本组件执行");
            return;
        }

        // 用 Timer 排一个一次性回调，证明 Manager 真正在运转（非仅构造成功）。
        GameEntry.Timer.AddTimer(
            () => GameLog.Log("[FrameworkSmoke] ✅ Timer 0.5s 回调触发，地基运转正常"),
            0.5f);

        GameLog.Log("[FrameworkSmoke] ✅ Framework OK —— 所有 Manager 已启动（Event/Timer/Resource/UI/Network/RefData/Audio/Scene/Auth）");

        // 协议序列化/组包往返自检不在此处：见类注释「注 2」——AOT 侧不得引用热更程序集 GameProtocol。
    }
}
