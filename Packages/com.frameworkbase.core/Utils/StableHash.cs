using System.Text;

namespace Framework
{
    /// <summary>
    /// 稳定哈希：跨平台 / 跨进程 / 跨版本结果一致
    /// （string.GetHashCode 无此保证，禁止用于持久化分桶）。
    /// 用途：灰度放量与功能开关按设备分桶——同一输入永远落同一桶，
    /// 放量百分比上调时已命中设备保持命中（判定为 桶号 &lt; 百分比，单调扩大）。
    /// </summary>
    public static class StableHash
    {
        /// <summary>FNV-1a 32 位哈希（对 UTF-8 字节序列）。</summary>
        public static uint Fnv1a32(string text)
        {
            const uint OffsetBasis = 2166136261;
            const uint Prime = 16777619;

            uint hash = OffsetBasis;
            if (string.IsNullOrEmpty(text))
                return hash;

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= Prime;
            }
            return hash;
        }

        /// <summary>把输入映射到 [0, bucketCount) 的稳定桶号。</summary>
        public static int Bucket(string text, int bucketCount = 100)
        {
            if (bucketCount <= 0)
                return 0;
            return (int)(Fnv1a32(text) % (uint)bucketCount);
        }
    }
}
