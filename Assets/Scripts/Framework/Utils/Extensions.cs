using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework
{
    /// <summary>
    /// GameObject 扩展方法
    /// </summary>
    public static class GameObjectExtensions
    {
        /// <summary>
        /// 获取或添加组件
        /// </summary>
        /// <typeparam name="T">组件类型</typeparam>
        /// <param name="go">GameObject</param>
        /// <returns>组件实例</returns>
        public static T GetOrAddComponent<T>(this GameObject go) where T : Component
        {
            if (go == null)
                return null;

            T component = go.GetComponent<T>();
            if (component == null)
            {
                component = go.AddComponent<T>();
            }
            return component;
        }

        /// <summary>
        /// 设置激活状态（安全版本，检查 null）
        /// </summary>
        /// <param name="go">GameObject</param>
        /// <param name="active">是否激活</param>
        public static void SetActiveSafe(this GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
            {
                go.SetActive(active);
            }
        }

        /// <summary>
        /// 销毁 GameObject
        /// </summary>
        /// <param name="go">GameObject</param>
        /// <param name="delay">延迟时间（秒）</param>
        public static void DestroyGameObject(this GameObject go, float delay = 0f)
        {
            if (go == null)
                return;

            if (delay > 0f)
            {
                UnityEngine.Object.Destroy(go, delay);
            }
            else
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// 立即销毁 GameObject（编辑器模式下也可用）
        /// </summary>
        /// <param name="go">GameObject</param>
        public static void DestroyGameObjectImmediate(this GameObject go)
        {
            if (go == null)
                return;

            UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 查找子对象（递归查找）
        /// </summary>
        /// <param name="go">GameObject</param>
        /// <param name="name">子对象名称</param>
        /// <returns>找到的子对象，未找到返回 null</returns>
        public static GameObject FindChildRecursive(this GameObject go, string name)
        {
            if (go == null || string.IsNullOrEmpty(name))
                return null;

            Transform transform = go.transform.FindChildRecursive(name);
            return transform != null ? transform.gameObject : null;
        }

        /// <summary>
        /// 设置层级（包括所有子对象）
        /// </summary>
        /// <param name="go">GameObject</param>
        /// <param name="layer">层级</param>
        public static void SetLayerRecursive(this GameObject go, int layer)
        {
            if (go == null)
                return;

            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
    }

    /// <summary>
    /// Transform 扩展方法
    /// </summary>
    public static class TransformExtensions
    {
        /// <summary>
        /// 设置位置
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="x">X 坐标</param>
        /// <param name="y">Y 坐标</param>
        /// <param name="z">Z 坐标</param>
        public static void SetPosition(this Transform transform, float x, float y, float z)
        {
            if (transform == null)
                return;

            transform.position = new Vector3(x, y, z);
        }

        /// <summary>
        /// 设置本地位置
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="x">X 坐标</param>
        /// <param name="y">Y 坐标</param>
        /// <param name="z">Z 坐标</param>
        public static void SetLocalPosition(this Transform transform, float x, float y, float z)
        {
            if (transform == null)
                return;

            transform.localPosition = new Vector3(x, y, z);
        }

        /// <summary>
        /// 设置 X 坐标
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="x">X 坐标</param>
        public static void SetPositionX(this Transform transform, float x)
        {
            if (transform == null)
                return;

            Vector3 pos = transform.position;
            pos.x = x;
            transform.position = pos;
        }

        /// <summary>
        /// 设置 Y 坐标
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="y">Y 坐标</param>
        public static void SetPositionY(this Transform transform, float y)
        {
            if (transform == null)
                return;

            Vector3 pos = transform.position;
            pos.y = y;
            transform.position = pos;
        }

        /// <summary>
        /// 设置 Z 坐标
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="z">Z 坐标</param>
        public static void SetPositionZ(this Transform transform, float z)
        {
            if (transform == null)
                return;

            Vector3 pos = transform.position;
            pos.z = z;
            transform.position = pos;
        }

        /// <summary>
        /// 重置 Transform（位置、旋转、缩放）
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void ResetTransform(this Transform transform)
        {
            if (transform == null)
                return;

            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        /// <summary>
        /// 重置本地位置
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void ResetLocalPosition(this Transform transform)
        {
            if (transform == null)
                return;

            transform.localPosition = Vector3.zero;
        }

        /// <summary>
        /// 重置本地旋转
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void ResetLocalRotation(this Transform transform)
        {
            if (transform == null)
                return;

            transform.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// 重置本地缩放
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void ResetLocalScale(this Transform transform)
        {
            if (transform == null)
                return;

            transform.localScale = Vector3.one;
        }

        /// <summary>
        /// 查找子对象（递归查找）
        /// </summary>
        /// <param name="transform">Transform</param>
        /// <param name="name">子对象名称</param>
        /// <returns>找到的子对象，未找到返回 null</returns>
        public static Transform FindChildRecursive(this Transform transform, string name)
        {
            if (transform == null || string.IsNullOrEmpty(name))
                return null;

            // 先查找直接子对象
            Transform child = transform.Find(name);
            if (child != null)
                return child;

            // 递归查找所有子对象
            foreach (Transform t in transform)
            {
                child = t.FindChildRecursive(name);
                if (child != null)
                    return child;
            }

            return null;
        }

        /// <summary>
        /// 销毁所有子对象
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void DestroyAllChildren(this Transform transform)
        {
            if (transform == null)
                return;

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(transform.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// 立即销毁所有子对象（编辑器模式下也可用）
        /// </summary>
        /// <param name="transform">Transform</param>
        public static void DestroyAllChildrenImmediate(this Transform transform)
        {
            if (transform == null)
                return;

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(transform.GetChild(i).gameObject);
            }
        }
    }

    /// <summary>
    /// String 扩展方法
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// 判断字符串是否为 null 或空
        /// </summary>
        /// <param name="str">字符串</param>
        /// <returns>是否为 null 或空</returns>
        public static bool IsNullOrEmpty(this string str)
        {
            return string.IsNullOrEmpty(str);
        }

        /// <summary>
        /// 判断字符串是否为 null、空或仅包含空白字符
        /// </summary>
        /// <param name="str">字符串</param>
        /// <returns>是否为 null、空或仅包含空白字符</returns>
        public static bool IsNullOrWhiteSpace(this string str)
        {
            return string.IsNullOrWhiteSpace(str);
        }

        /// <summary>
        /// 格式化字符串（简化版）
        /// </summary>
        /// <param name="format">格式字符串</param>
        /// <param name="args">参数</param>
        /// <returns>格式化后的字符串</returns>
        public static string Format(this string format, params object[] args)
        {
            if (string.IsNullOrEmpty(format))
                return format;

            return string.Format(format, args);
        }

        /// <summary>
        /// 截取字符串（安全版本，不会抛出异常）
        /// </summary>
        /// <param name="str">字符串</param>
        /// <param name="maxLength">最大长度</param>
        /// <param name="suffix">超出时的后缀（如 "..."）</param>
        /// <returns>截取后的字符串</returns>
        public static string Truncate(this string str, int maxLength, string suffix = "...")
        {
            if (string.IsNullOrEmpty(str) || str.Length <= maxLength)
                return str;

            int truncateLength = maxLength - (suffix?.Length ?? 0);
            if (truncateLength <= 0)
                return suffix ?? string.Empty;

            return str.Substring(0, truncateLength) + suffix;
        }

        /// <summary>
        /// 移除字符串中的空白字符
        /// </summary>
        /// <param name="str">字符串</param>
        /// <returns>移除空白字符后的字符串</returns>
        public static string RemoveWhiteSpace(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            return str.Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        }
    }

    /// <summary>
    /// Collection 扩展方法
    /// </summary>
    public static class CollectionExtensions
    {
        private static System.Random _random = new System.Random();

        /// <summary>
        /// 获取随机元素
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        /// <returns>随机元素，列表为空返回 default(T)</returns>
        public static T GetRandomElement<T>(this IList<T> list)
        {
            if (list == null || list.Count == 0)
                return default(T);

            int index = _random.Next(0, list.Count);
            return list[index];
        }

        /// <summary>
        /// 打乱列表（Fisher-Yates 洗牌算法）
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        public static void Shuffle<T>(this IList<T> list)
        {
            if (list == null || list.Count <= 1)
                return;

            int n = list.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = _random.Next(0, i + 1);
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        /// <summary>
        /// 判断列表是否为 null 或空
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        /// <returns>是否为 null 或空</returns>
        public static bool IsNullOrEmpty<T>(this ICollection<T> list)
        {
            return list == null || list.Count == 0;
        }

        /// <summary>
        /// 添加多个元素
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        /// <param name="items">要添加的元素</param>
        public static void AddRange<T>(this ICollection<T> list, IEnumerable<T> items)
        {
            if (list == null || items == null)
                return;

            foreach (T item in items)
            {
                list.Add(item);
            }
        }

        /// <summary>
        /// 移除满足条件的所有元素
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        /// <param name="predicate">条件</param>
        /// <returns>移除的元素数量</returns>
        public static int RemoveAll<T>(this IList<T> list, Predicate<T> predicate)
        {
            if (list == null || predicate == null)
                return 0;

            int count = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (predicate(list[i]))
                {
                    list.RemoveAt(i);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 查找第一个满足条件的元素
        /// </summary>
        /// <typeparam name="T">元素类型</typeparam>
        /// <param name="list">列表</param>
        /// <param name="predicate">条件</param>
        /// <returns>找到的元素，未找到返回 default(T)</returns>
        public static T Find<T>(this IEnumerable<T> list, Predicate<T> predicate)
        {
            if (list == null || predicate == null)
                return default(T);

            foreach (T item in list)
            {
                if (predicate(item))
                    return item;
            }
            return default(T);
        }
    }
}
