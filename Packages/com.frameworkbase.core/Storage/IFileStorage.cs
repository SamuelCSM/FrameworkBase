using Cysharp.Threading.Tasks;

namespace Framework.Storage
{
    /// <summary>
    /// 面向框架运行时代码的文件存储抽象。
    /// 统一收敛常见 IO 行为，避免业务模块重复实现原子写入、静默删除、目录准备或线程池文件访问。
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>判断文件是否存在。</summary>
        bool FileExists(string path);

        /// <summary>判断目录是否存在。</summary>
        bool DirectoryExists(string path);

        /// <summary>确保目录存在。</summary>
        void EnsureDirectory(string path);

        /// <summary>确保文件路径的父目录存在。</summary>
        void EnsureParentDirectory(string filePath);

        /// <summary>同步读取文本文件。</summary>
        string ReadText(string path);

        /// <summary>同步读取文本文件的所有行。</summary>
        string[] ReadLines(string path);

        /// <summary>同步读取文件的全部字节。</summary>
        byte[] ReadBytes(string path);

        /// <summary>同步写入文本文件，必要时创建父目录。</summary>
        void WriteText(string path, string content);

        /// <summary>同步写入所有文本行，必要时创建父目录。</summary>
        void WriteLines(string path, string[] lines);

        /// <summary>同步写入字节，必要时创建父目录。</summary>
        void WriteBytes(string path, byte[] bytes);

        /// <summary>同步追加文本，必要时创建父目录。</summary>
        void AppendText(string path, string content);

        /// <summary>同步追加字节，必要时创建父目录。</summary>
        void AppendBytes(string path, byte[] bytes);

        /// <summary>
        /// 通过先写临时文件再移动替换的方式，原子化替换文本文件。
        /// 如果传入备份路径，会在替换前复制旧文件作为备份。
        /// </summary>
        void AtomicWriteText(string path, string content, string backupPath = null);

        /// <summary>删除文件；底层文件系统操作失败时抛出异常。</summary>
        void DeleteFile(string path);

        /// <summary>文件存在时尝试删除，并吞掉 IO 失败。</summary>
        bool TryDeleteFile(string path);

        /// <summary>目录存在时删除目录。</summary>
        void DeleteDirectory(string path, bool recursive);

        /// <summary>返回文件字节大小；文件不存在时返回 0。</summary>
        long GetFileSize(string path);

        /// <summary>在线程池读取文件字节。</summary>
        UniTask<byte[]> ReadBytesAsync(string path);

        /// <summary>在线程池写入文本文件。</summary>
        UniTask WriteTextAsync(string path, string content);
    }
}
