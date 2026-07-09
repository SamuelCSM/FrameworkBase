using System;
using System.IO;
using UnityEngine;

namespace Framework.Editor.BuildSize
{
    /// <summary>
    /// 尺寸快照的采集与落盘：扫描产物目录成快照、基线 JSON 读写。
    /// 目录扫描是纯 <see cref="System.IO"/>，可用临时目录单测；基线读写用 <see cref="JsonUtility"/>。
    /// </summary>
    public static class BuildSizeSnapshotIO
    {
        /// <summary>
        /// 递归扫描目录，每个文件成一条条目（相对路径为名），汇总为快照。
        /// </summary>
        /// <param name="directory">产物目录（热更 bundle 目录 / 整包输出目录）。</param>
        /// <param name="label">快照标签（版本/渠道/平台）。</param>
        /// <param name="searchPattern">文件通配，默认 <c>*</c>（全部）。</param>
        /// <returns>快照；目录不存在返回总量 0 的空快照。</returns>
        public static BuildSizeSnapshot FromDirectory(string directory, string label = null, string searchPattern = "*")
        {
            var snapshot = new BuildSizeSnapshot
            {
                label = label ?? string.Empty,
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return snapshot;

            string root = Path.GetFullPath(directory);
            foreach (string file in Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories))
            {
                long len;
                try
                {
                    len = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue; // 文件被占用/瞬时消失，跳过不炸
                }

                string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                snapshot.entries.Add(new BuildSizeEntry(rel, len));
                snapshot.totalBytes += len;
            }

            return snapshot;
        }

        /// <summary>
        /// 读取基线 JSON。
        /// </summary>
        /// <param name="path">基线文件路径。</param>
        /// <returns>基线快照；文件不存在或解析失败返回 null（视为首次）。</returns>
        public static BuildSizeSnapshot LoadBaseline(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                var snapshot = JsonUtility.FromJson<BuildSizeSnapshot>(json);
                return snapshot;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BuildSizeGate] 基线读取失败（视为首次）：{e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 写入基线 JSON（覆盖）。
        /// </summary>
        /// <param name="path">基线文件路径。</param>
        /// <param name="snapshot">要落盘的快照。</param>
        public static void SaveBaseline(string path, BuildSizeSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(path) || snapshot == null)
                return;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
        }
    }
}
