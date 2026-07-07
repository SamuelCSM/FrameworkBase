using Cysharp.Threading.Tasks;

namespace Framework.Storage
{
    /// <summary>
    /// File storage abstraction for framework runtime code.
    /// It centralizes common IO behavior so business modules do not each reinvent atomic writes,
    /// quiet deletes, directory preparation, or thread-pool file access.
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>Return true when the file exists.</summary>
        bool FileExists(string path);

        /// <summary>Return true when the directory exists.</summary>
        bool DirectoryExists(string path);

        /// <summary>Ensure a directory exists.</summary>
        void EnsureDirectory(string path);

        /// <summary>Ensure the parent directory for a file path exists.</summary>
        void EnsureParentDirectory(string filePath);

        /// <summary>Read a UTF-8/default text file synchronously.</summary>
        string ReadText(string path);

        /// <summary>Read all lines from a text file synchronously.</summary>
        string[] ReadLines(string path);

        /// <summary>Read all bytes from a file synchronously.</summary>
        byte[] ReadBytes(string path);

        /// <summary>Write a text file synchronously, creating the parent directory if needed.</summary>
        void WriteText(string path, string content);

        /// <summary>Write all lines synchronously, creating the parent directory if needed.</summary>
        void WriteLines(string path, string[] lines);

        /// <summary>Write bytes synchronously, creating the parent directory if needed.</summary>
        void WriteBytes(string path, byte[] bytes);

        /// <summary>Append text synchronously, creating the parent directory if needed.</summary>
        void AppendText(string path, string content);

        /// <summary>Append bytes synchronously, creating the parent directory if needed.</summary>
        void AppendBytes(string path, byte[] bytes);

        /// <summary>
        /// Atomically replace a text file by writing a temporary file and moving it into place.
        /// Optionally copies the previous file to a backup path before replacement.
        /// </summary>
        void AtomicWriteText(string path, string content, string backupPath = null);

        /// <summary>Delete a file. Throws if the underlying file system operation fails.</summary>
        void DeleteFile(string path);

        /// <summary>Delete a file if it exists, suppressing IO failures.</summary>
        bool TryDeleteFile(string path);

        /// <summary>Delete a directory if it exists.</summary>
        void DeleteDirectory(string path, bool recursive);

        /// <summary>Return the file size in bytes, or 0 when the file does not exist.</summary>
        long GetFileSize(string path);

        /// <summary>Read bytes on a background thread.</summary>
        UniTask<byte[]> ReadBytesAsync(string path);

        /// <summary>Write text on a background thread.</summary>
        UniTask WriteTextAsync(string path, string content);
    }
}
