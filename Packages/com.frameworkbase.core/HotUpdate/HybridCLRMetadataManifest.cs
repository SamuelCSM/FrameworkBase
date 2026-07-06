using System;

namespace Framework.HotUpdate
{
    /// <summary>
    /// HybridCLR AOT 补充元数据清单。
    /// <para>
    /// 权威数据源是 HybridCLR 生成的 <c>AOTGenericReferences.PatchedAOTAssemblyList</c>：发布器
    /// （<c>HybridCLRStreamingAssetsSync</c>）据此拷贝 .dll.bytes 并写 <c>manifest.json</c>，运行时
    /// （<c>HybridCLRMetadataLoader</c>）优先读 <c>manifest.json</c>。
    /// </para>
    /// <para>
    /// 下方 <see cref="PatchedAotAssemblies"/> 仅作 <b>运行时兜底</b>：当 <c>manifest.json</c> 缺失/损坏时使用。
    /// 它不再参与发布（发布以 AOTGenericReferences 为单一源，杜绝漂移），但仍建议大致与之保持一致以防兜底失真。
    /// </para>
    /// </summary>
    public static class HybridCLRMetadataManifest
    {
        /// <summary>StreamingAssets 下元数据目录。</summary>
        public const string StreamingAssetsFolder = "HybridCLRMetadata";

        /// <summary>程序集列表文件名。</summary>
        public const string ManifestFileName = "manifest.json";

        /// <summary>需要加载补充元数据的 AOT 程序集（含 .dll 后缀）。</summary>
        public static readonly string[] PatchedAotAssemblies =
        {
            "Framework.dll",
            "SQLite-net.dll",
            "System.Core.dll",
            "UniTask.dll",
            "UnityEngine.CoreModule.dll",
            "UnityEngine.JSONSerializeModule.dll",
            "mscorlib.dll",
            // protobuf-net 2.4.x 为单一程序集，仅 protobuf-net.dll；.Core 是 3.x 才拆出的产物，2.x 不存在，勿添加。
            "protobuf-net.dll",
        };
    }

    /// <summary>StreamingAssets 内 HybridCLR 元数据 manifest。</summary>
    [Serializable]
    public class HybridCLRMetadataManifestData
    {
        public string[] assemblies;
    }
}
