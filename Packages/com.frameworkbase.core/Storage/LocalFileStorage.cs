using System;
using System.IO;
using Cysharp.Threading.Tasks;

namespace Framework.Storage
{
    /// <summary>
    /// 基于 System.IO 的 <see cref="IFileStorage"/> 实现，供 Unity 运行时和编辑器代码使用。
    /// </summary>
    public sealed class LocalFileStorage : IFileStorage
    {
        /// <inheritdoc />
        public bool FileExists(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <inheritdoc />
        public bool DirectoryExists(string path)
        {
            return !string.IsNullOrEmpty(path) && Directory.Exists(path);
        }

        /// <inheritdoc />
        public void EnsureDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path))
                Directory.CreateDirectory(path);
        }

        /// <inheritdoc />
        public void EnsureParentDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        /// <inheritdoc />
        public string ReadText(string path)
        {
            return File.ReadAllText(path);
        }

        /// <inheritdoc />
        public string[] ReadLines(string path)
        {
            return File.ReadAllLines(path);
        }

        /// <inheritdoc />
        public byte[] ReadBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        /// <inheritdoc />
        public void WriteText(string path, string content)
        {
            EnsureParentDirectory(path);
            File.WriteAllText(path, content ?? string.Empty);
        }

        /// <inheritdoc />
        public void WriteLines(string path, string[] lines)
        {
            EnsureParentDirectory(path);
            File.WriteAllLines(path, lines ?? Array.Empty<string>());
        }

        /// <inheritdoc />
        public void WriteBytes(string path, byte[] bytes)
        {
            EnsureParentDirectory(path);
            File.WriteAllBytes(path, bytes ?? Array.Empty<byte>());
        }

        /// <inheritdoc />
        public void AppendText(string path, string content)
        {
            EnsureParentDirectory(path);
            File.AppendAllText(path, content ?? string.Empty);
        }

        /// <inheritdoc />
        public void AppendBytes(string path, byte[] bytes)
        {
            EnsureParentDirectory(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] normalizedBytes = bytes ?? Array.Empty<byte>();
            stream.Write(normalizedBytes, 0, normalizedBytes.Length);
        }

        /// <inheritdoc />
        public void AtomicWriteText(string path, string content, string backupPath = null)
        {
            EnsureParentDirectory(path);

            string tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content ?? string.Empty);

                if (!string.IsNullOrEmpty(backupPath) && File.Exists(path))
                {
                    EnsureParentDirectory(backupPath);
                    File.Copy(path, backupPath, overwrite: true);
                }

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tempPath, path);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        /// <inheritdoc />
        public void DeleteFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }

        /// <inheritdoc />
        public bool TryDeleteFile(string path)
        {
            try
            {
                DeleteFile(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void DeleteDirectory(string path, bool recursive)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, recursive);
        }

        /// <inheritdoc />
        public long GetFileSize(string path)
        {
            if (!FileExists(path))
                return 0;
            return new FileInfo(path).Length;
        }

        /// <inheritdoc />
        public UniTask<byte[]> ReadBytesAsync(string path)
        {
            return UniTask.RunOnThreadPool(() => ReadBytes(path));
        }

        /// <inheritdoc />
        public UniTask WriteTextAsync(string path, string content)
        {
            return UniTask.RunOnThreadPool(() => WriteText(path, content));
        }
    }
}
