using System;
using UnityEngine;

namespace Framework.Serialization
{
    /// <summary>
    /// JsonUtility-backed serializer for Unity runtime objects and serializable DTOs.
    /// It intentionally preserves JsonUtility's field-based behavior and limitations.
    /// </summary>
    public sealed class UnityJsonSerializer : IJsonSerializer
    {
        /// <inheritdoc />
        public string ToJson<T>(T value, bool prettyPrint = false)
        {
            return JsonUtility.ToJson(value, prettyPrint);
        }

        /// <inheritdoc />
        public T FromJson<T>(string json)
        {
            return JsonUtility.FromJson<T>(json);
        }

        /// <inheritdoc />
        public object FromJson(string json, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return JsonUtility.FromJson(json, type);
        }

        /// <inheritdoc />
        public bool TryFromJson<T>(string json, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                value = FromJson<T>(json);
                return value != null;
            }
            catch (Exception)
            {
                value = default;
                return false;
            }
        }
    }
}
