namespace Framework.Http
{
    /// <summary>
    /// Framework-level HTTP verbs. Business modules should depend on this enum instead of UnityWebRequest constants.
    /// </summary>
    public enum HttpMethod
    {
        Get,
        Post,
        Put,
        Delete,
        Patch,
        Head
    }
}
