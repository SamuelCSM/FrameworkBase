using System;

namespace Framework.Serialization
{
    /// <summary>
    /// Strongly typed JSON serializer abstraction for framework runtime code.
    /// The default implementation wraps Unity JsonUtility, while dynamic dictionary JSON is handled by
    /// <see cref="JsonObjectParser"/> and <see cref="JsonWriter"/>.
    /// </summary>
    public interface IJsonSerializer
    {
        /// <summary>Serialize a strongly typed value to JSON.</summary>
        string ToJson<T>(T value, bool prettyPrint = false);

        /// <summary>Deserialize JSON into a strongly typed value.</summary>
        T FromJson<T>(string json);

        /// <summary>Deserialize JSON into a runtime-provided type.</summary>
        object FromJson(string json, Type type);

        /// <summary>Try to deserialize JSON without throwing for invalid input.</summary>
        bool TryFromJson<T>(string json, out T value);
    }
}
