using System;

namespace Framework
{
    /// <summary>窗口由 Addressables Prefab 自动注册，或由业务提供代码 View Factory。</summary>
    public enum UIWindowRegistrationMode
    {
        Addressable = 0,
        Code = 1,
    }

    [Serializable]
    public sealed class UIWindowModuleDefinition
    {
        public int Id;
        public string Key;
        public string Description;
        public int WindowIdMin;
        public int WindowIdMax;
        public int TargetIdMin;
        public int TargetIdMax;
    }

    [Serializable]
    public sealed class UIWindowDefinition
    {
        public int Id;
        public int ModuleId;
        public string Key;
        public string LogicType;
        public UIWindowRegistrationMode RegistrationMode;
        public string Address;
        public UILayer Layer;
        public bool AllowMultiple;
        public UIStackBehavior StackBehavior;
        public UIBlockerMode BlockerMode;
        public string Description;
    }

    [Serializable]
    public sealed class UITargetDefinition
    {
        public int Id;
        public int ModuleId;
        public int WindowId;
        public string Key;
        public string Description;
    }

    [Serializable]
    public sealed class UIStableIdRetiredDefinition
    {
        public int Id;
        public string FormerKey;
        public string RetiredVersion;
        public string Reason;
    }

    [Serializable]
    public sealed class UIWindowCatalog
    {
        public int SchemaVersion = 1;
        public UIWindowModuleDefinition[] Modules = Array.Empty<UIWindowModuleDefinition>();
        public UIWindowDefinition[] Windows = Array.Empty<UIWindowDefinition>();
        public UITargetDefinition[] Targets = Array.Empty<UITargetDefinition>();
        public UIStableIdRetiredDefinition[] RetiredWindowIds = Array.Empty<UIStableIdRetiredDefinition>();
        public UIStableIdRetiredDefinition[] RetiredTargetIds = Array.Empty<UIStableIdRetiredDefinition>();
    }
}
