using System;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Installer;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Framework.Editor
{
    /// <summary>
    /// HybridCLR 构建前置准备的 CI 入口：安装本地 il2cpp（打过补丁的运行时）并生成 link.xml /
    /// 方法桥 / AOT 泛型引用等构建期依赖。
    /// <para>
    /// 背景：启用 HybridCLR 后，任何 IL2CPP 出包都会先经 <c>CheckSettings</c> 构建预处理器校验
    /// “本地 il2cpp 已安装”，未安装即抛 <see cref="BuildFailedException"/> 中断出包。而
    /// <c>HybridCLRData/</c>（含 LocalIl2CppData）是<b>按机器安装、不进版本库</b>的，冷 CI 环境
    /// 从未安装，故 Android/iOS 出包 job 必须在真正 BuildPlayer 之前先跑一次本入口。
    /// </para>
    /// <para>
    /// 安装源仓库地址取自 ProjectSettings/HybridCLRSettings.asset（已指向 GitHub 上游的不可变
    /// tag），克隆后把当前 Unity 版本的 il2cpp 拷入工程根 <c>HybridCLRData/LocalIl2CppData</c>；
    /// 该目录随 job workspace 在后续构建步骤间保留，因此本入口与出包步骤同 job、前后相邻即可。
    /// </para>
    /// <para>
    /// 命名遵循仓库约定：<c>*ForBuilder</c> 变体失败以 <see cref="BuildFailedException"/> 上抛，
    /// 让 GameCI unity-builder 可靠拿到非零结果（batchmode 进程退出码不可靠），不调用
    /// <see cref="EditorApplication.Exit"/> 抢占宿主退出流程。与 <see cref="CiGate"/> 同款。
    /// </para>
    /// </summary>
    public static class HybridCLRBuildSetup
    {
        /// <summary>
        /// GameCI unity-builder 入口：确保本地 il2cpp 安装就绪并执行 GenerateAll。
        /// 调用示例（作为出包步骤的前置步骤）：
        /// <code>
        /// - uses: game-ci/unity-builder@v4
        ///   with:
        ///     targetPlatform: Android
        ///     allowDirtyBuild: true
        ///     buildMethod: Framework.Editor.HybridCLRBuildSetup.InstallAndGenerateForBuilder
        /// </code>
        /// </summary>
        public static void InstallAndGenerateForBuilder()
        {
            try
            {
                EnsureInstalledAndGenerated();
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BuildFailedException(
                    $"HybridCLR 构建前置准备失败：{ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>幂等地安装本地 il2cpp（缺失才装）并生成构建期依赖；失败抛 <see cref="BuildFailedException"/>。</summary>
        private static void EnsureInstalledAndGenerated()
        {
            var controller = new InstallerController();

            if (controller.HasInstalledHybridCLR())
            {
                Debug.Log($"[HybridCLRSetup] 已检测到本地 il2cpp 安装（已装版本 {controller.InstalledLibil2cppVersion}），跳过安装。");
            }
            else
            {
                Debug.Log($"[HybridCLRSetup] 未检测到本地 il2cpp，开始安装（包版本 {controller.PackageVersion}，" +
                          $"il2cpp_plus 分支 {controller.Il2cppPlusLocalVersion}）……");
                controller.InstallDefaultHybridCLR();
                if (!controller.HasInstalledHybridCLR())
                    throw new BuildFailedException("HybridCLR 安装流程结束后仍未就绪（LocalIl2CppData/libil2cpp/hybridclr 缺失），请检查源仓库克隆日志。");
                Debug.Log("[HybridCLRSetup] 本地 il2cpp 安装完成。");
            }

            Debug.Log("[HybridCLRSetup] 执行 PrebuildCommand.GenerateAll（link.xml / 方法桥 / AOT 泛型引用）……");
            PrebuildCommand.GenerateAll();
            Debug.Log("[HybridCLRSetup] GenerateAll 完成，HybridCLR 构建前置准备就绪。");
        }
    }
}
