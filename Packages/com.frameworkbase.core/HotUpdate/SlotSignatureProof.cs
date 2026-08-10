using System;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 代码槽的发布方签名凭据（ADR-001 补遗）。
    /// <para>
    /// 远端 version.json 的验签只发生在下载那一刻。槽提交后，磁盘上留下的
    /// <c>slot.json</c> 是<b>本地生成</b>的普通 JSON——能写 persistentDataPath 的人可以连同 DLL
    /// 一起改，启动复验拿它自己的摘要比对自己，永远自洽。把签名信封随槽落盘，
    /// 启动时重新验签并与 slot.json 交叉核对，才能让每次启动都处在发布方的签名信任链内。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class SlotSignatureProof
    {
        /// <summary>结构版本，便于日后扩展字段时识别旧凭据。</summary>
        public int SchemaVersion = 1;

        /// <summary>选择信任根用的密钥 ID，须命中 AppConfig 的热更公钥环。</summary>
        public string KeyId = string.Empty;

        /// <summary>Base64 的 RSA-SHA256 签名，覆盖 <see cref="ManifestJson"/> 的 UTF-8 字节。</summary>
        public string Signature = string.Empty;

        /// <summary>
        /// 下载时验签通过的 version.json <b>原始文本</b>。必须原样保存：
        /// 重新序列化会改变字段顺序与空白，签名边界随之失效。
        /// </summary>
        public string ManifestJson = string.Empty;

        /// <summary>三个字段齐备才可能验签成功。</summary>
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(KeyId) &&
            !string.IsNullOrWhiteSpace(Signature) &&
            !string.IsNullOrEmpty(ManifestJson);
    }
}
