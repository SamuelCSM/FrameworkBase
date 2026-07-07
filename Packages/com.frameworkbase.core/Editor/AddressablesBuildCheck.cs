using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Framework.Editor
{
    /// <summary>
    /// 构建前置门禁：Addressables 分组配置存在 Error 级问题（路径错配 / 场景混包 /
    /// 组缺 Schema）时直接终止构建——这类错误打出来的包要么资源加载失败、
    /// 要么热更通道被焊死，宁可构建失败也不能流出。Warning 只打印不拦截。
    /// 排在热更安全检查（-100）之后执行；工程未启用 Addressables 时自动跳过。
    /// </summary>
    public class AddressablesBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -90;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!AddressablesValidator.ValidateForBuild(out string errorSummary))
                throw new BuildFailedException($"[AddressablesBuildCheck] {errorSummary}");
        }
    }
}
