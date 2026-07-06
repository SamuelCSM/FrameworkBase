using System;
using System.IO;
using System.IO.Compression;
using Google.Protobuf;

namespace Framework
{
    /// <summary>
    /// Protobuf 序列化工具类（Google.Protobuf 实现）。
    /// 提供消息的序列化、反序列化和压缩功能。
    /// 仅走二进制路径（<c>ToByteArray</c>/<c>MergeFrom</c>），不触碰 <c>Descriptor</c>/JSON/反射，保证 IL2CPP(AOT) 安全。
    /// </summary>
    public static class ProtobufUtil
    {
        /// <summary>
        /// 序列化消息为字节数组。
        /// </summary>
        /// <param name="message">消息对象。</param>
        /// <returns>序列化后的字节数组。</returns>
        public static byte[] Serialize(IMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            try
            {
                return message.ToByteArray();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize message of type {message.GetType().Name}", ex);
            }
        }

        /// <summary>
        /// 反序列化字节数组为消息对象。
        /// </summary>
        /// <typeparam name="T">消息类型。</typeparam>
        /// <param name="data">字节数组。</param>
        /// <returns>反序列化后的消息对象。</returns>
        public static T Deserialize<T>(byte[] data) where T : class, IMessage, new()
        {
            try
            {
                // 空 / null payload 是合法的「全默认值」protobuf 消息：protobuf 不写默认值字段，
                // 当一条消息所有字段均为默认时整体即 0 字节。按 protobuf 语义返回默认实例，交由上层按字段判定，
                // 而非在此抛异常。
                var message = new T();
                if (data != null && data.Length > 0)
                {
                    message.MergeFrom(data);
                }

                return message;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize message of type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// 序列化消息并压缩。
        /// </summary>
        /// <param name="message">消息对象。</param>
        /// <returns>压缩后的字节数组。</returns>
        public static byte[] SerializeWithCompression(IMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            try
            {
                byte[] serializedData = Serialize(message);
                return Compress(serializedData);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize and compress message of type {message.GetType().Name}", ex);
            }
        }

        /// <summary>
        /// 解压缩并反序列化消息。
        /// </summary>
        /// <typeparam name="T">消息类型。</typeparam>
        /// <param name="compressedData">压缩的字节数组。</param>
        /// <returns>反序列化后的消息对象。</returns>
        public static T DeserializeWithDecompression<T>(byte[] compressedData) where T : class, IMessage, new()
        {
            if (compressedData == null || compressedData.Length == 0)
            {
                throw new ArgumentException("Compressed data cannot be null or empty", nameof(compressedData));
            }

            try
            {
                byte[] decompressedData = Decompress(compressedData);
                return Deserialize<T>(decompressedData);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to decompress and deserialize message of type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// 压缩字节数组（使用GZip）。
        /// </summary>
        /// <param name="data">原始字节数组。</param>
        /// <returns>压缩后的字节数组。</returns>
        public static byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Data cannot be null or empty", nameof(data));
            }

            try
            {
                using (var outputStream = new MemoryStream())
                {
                    using (var gzipStream = new GZipStream(outputStream, CompressionMode.Compress))
                    {
                        gzipStream.Write(data, 0, data.Length);
                    }
                    return outputStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to compress data", ex);
            }
        }

        /// <summary>
        /// 解压缩字节数组（使用GZip）。
        /// </summary>
        /// <param name="compressedData">压缩的字节数组。</param>
        /// <returns>解压缩后的字节数组。</returns>
        public static byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
            {
                throw new ArgumentException("Compressed data cannot be null or empty", nameof(compressedData));
            }

            try
            {
                using (var inputStream = new MemoryStream(compressedData))
                using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
                using (var outputStream = new MemoryStream())
                {
                    gzipStream.CopyTo(outputStream);
                    return outputStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to decompress data", ex);
            }
        }

        /// <summary>
        /// 计算压缩率。
        /// </summary>
        /// <param name="originalSize">原始大小。</param>
        /// <param name="compressedSize">压缩后大小。</param>
        /// <returns>压缩率（0-1之间，越小压缩效果越好）。</returns>
        public static float GetCompressionRatio(int originalSize, int compressedSize)
        {
            if (originalSize <= 0)
            {
                throw new ArgumentException("Original size must be greater than 0", nameof(originalSize));
            }

            return (float)compressedSize / originalSize;
        }
    }
}
