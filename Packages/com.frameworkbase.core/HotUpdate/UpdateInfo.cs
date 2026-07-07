using System;
using System.Collections.Generic;

namespace Framework.HotUpdate
{
    /// <summary>
    /// 版本更新信息
    /// </summary>
    [Serializable]
    public class UpdateInfo
    {
        /// <summary>
        /// 应用版本号（如1.0.0）
        /// </summary>
        public string AppVersion;
        
        /// <summary>
        /// 资源版本号
        /// </summary>
        public int ResourceVersion;
        
        /// <summary>
        /// 代码版本号
        /// </summary>
        public int CodeVersion;
        
        /// <summary>
        /// 是否强制更新
        /// </summary>
        public bool ForceUpdate;
        
        /// <summary>
        /// 最低兼容版本
        /// </summary>
        public string MinCompatibleVersion;
        
        /// <summary>
        /// 补丁文件列表
        /// </summary>
        public List<PatchFile> PatchFiles;
        
        /// <summary>
        /// 更新描述
        /// </summary>
        public string Description;
        
        /// <summary>
        /// 更新类型
        /// </summary>
        public UpdateType Type;

        /// <summary>
        /// 整包更新下载链接（仅 FullUpdate 时有效，指向应用商店或安装包地址）
        /// </summary>
        public string UpdateUrl;

        /// <summary>
        /// 灰度放量百分比：0（缺省）与 ≥100 表示全量下发；1~99 表示仅命中分桶的设备
        /// 应用本次更新，其余设备按"无更新"继续（放量上调后自动纳入）。
        /// 命中判定见 <see cref="VersionManager.IsDeviceInGrayRollout"/>。
        /// </summary>
        public int GrayPercent;
    }
    
    /// <summary>
    /// 补丁文件信息
    /// </summary>
    [Serializable]
    public class PatchFile
    {
        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName;
        
        /// <summary>
        /// 下载URL
        /// </summary>
        public string Url;
        
        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long Size;
        
        /// <summary>
        /// MD5校验值
        /// </summary>
        public string MD5;
    }
    
    /// <summary>
    /// 更新类型
    /// </summary>
    public enum UpdateType
    {
        /// <summary>
        /// 无需更新
        /// </summary>
        None,
        
        /// <summary>
        /// 热更新（资源或代码）
        /// </summary>
        HotUpdate,
        
        /// <summary>
        /// 整包更新（需要重新下载安装包）
        /// </summary>
        FullUpdate
    }
}
