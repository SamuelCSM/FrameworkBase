using System;
using System.IO;
using System.IO.Compression;
using ProtoBuf;
using ProtoBuf.Meta;

namespace Framework
{
    /// <summary>
    /// Protobuf序列化工具类
    /// 提供消息的序列化、反序列化和压缩功能
    /// </summary>
    public static class ProtobufUtil
    {
        /// <summary>
        /// 静态构造：在任何序列化发生前关闭运行时编译。
        /// IL2CPP（AOT）下无法在运行时用 Reflection.Emit 动态生成序列化器，
        /// 必须让 protobuf-net 走反射解释路径；否则首次序列化会在 AOT 上抛 Emit 相关异常。
        /// 所有协议消息均经本工具收发，故在此设置可全局生效。
        /// </summary>
        static ProtobufUtil()
        {
            RuntimeTypeModel.Default.AutoCompile = false;
        }

        /// <summary>
        /// 序列化消息为字节数组
        /// </summary>
        /// <typeparam name="T">消息类型</typeparam>
        /// <param name="message">消息对象</param>
        /// <returns>序列化后的字节数组</returns>
        public static byte[] Serialize<T>(T message) where T : class
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            try
            {
                using (var stream = new MemoryStream())
                {
                    Serializer.Serialize(stream, message);
                    return stream.ToArray();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to serialize message of type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// 反序列化字节数组为消息对象
        /// </summary>
        /// <typeparam name="T">消息类型</typeparam>
        /// <param name="data">字节数组</param>
        /// <returns>反序列化后的消息对象</returns>
        public static T Deserialize<T>(byte[] data) where T : class
        {
            try
            {
                // 空 / null payload 是合法的「全默认值」protobuf 消息：protobuf 不写默认值字段，
                // 当一条消息所有字段均为默认（如冷启动「无进行中对局」时复用的 MatchFound：RoomId=0、空列表）
                // 时整体即 0 字节。按 protobuf 语义反序列化为默认实例，交由上层按字段（如 RoomId<=0）判定，
                // 而非在此抛「Data cannot be null or empty」。
                using (var stream = new MemoryStream(data ?? Array.Empty<byte>()))
                {
                    return Serializer.Deserialize<T>(stream);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to deserialize message of type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// 序列化消息并压缩
        /// </summary>
        /// <typeparam name="T">消息类型</typeparam>
        /// <param name="message">消息对象</param>
        /// <returns>压缩后的字节数组</returns>
        public static byte[] SerializeWithCompression<T>(T message) where T : class
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
                throw new InvalidOperationException($"Failed to serialize and compress message of type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// 解压缩并反序列化消息
        /// </summary>
        /// <typeparam name="T">消息类型</typeparam>
        /// <param name="compressedData">压缩的字节数组</param>
        /// <returns>反序列化后的消息对象</returns>
        public static T DeserializeWithDecompression<T>(byte[] compressedData) where T : class
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
        /// 压缩字节数组（使用GZip）
        /// </summary>
        /// <param name="data">原始字节数组</param>
        /// <returns>压缩后的字节数组</returns>
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
        /// 解压缩字节数组（使用GZip）
        /// </summary>
        /// <param name="compressedData">压缩的字节数组</param>
        /// <returns>解压缩后的字节数组</returns>
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
        /// 计算压缩率
        /// </summary>
        /// <param name="originalSize">原始大小</param>
        /// <param name="compressedSize">压缩后大小</param>
        /// <returns>压缩率（0-1之间，越小压缩效果越好）</returns>
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
