using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// 基于 <see cref="System.Text.Json"/> 的轻量兼容工具。
    /// 输出结构保持 Dictionary/List + 基础类型，便于复用旧解析代码。
    /// </summary>
    internal static class LegacyJsonHelper
    {
        /// <summary>
        /// 将 JSON 文本反序列化为对象字典。
        /// </summary>
        public static Dictionary<string, object> DeserializeObject(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("JSON 解析结果不是对象。");
            }

            return ConvertToDictionary(document.RootElement);
        }

        /// <summary>
        /// 将对象强转为字典，失败时抛出异常。
        /// </summary>
        public static Dictionary<string, object> AsDictionary(object value)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
            {
                throw new InvalidOperationException("JSON 节点不是对象。");
            }

            return dictionary;
        }

        /// <summary>
        /// 将对象转换为列表，兼容 ArrayList / object[] / IEnumerable。
        /// </summary>
        public static IList<object> AsList(object value)
        {
            if (value is ArrayList arrayList)
            {
                return arrayList.Cast<object>().ToList();
            }

            if (value is object[] array)
            {
                return array.ToList();
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
            {
                return enumerable.Cast<object>().ToList();
            }

            throw new InvalidOperationException("JSON 节点不是数组。");
        }

        /// <summary>
        /// 按 key 获取子字典。
        /// </summary>
        public static bool TryGetDictionary(Dictionary<string, object> dictionary, string key, out Dictionary<string, object>? result)
        {
            if (dictionary.TryGetValue(key, out object? value) && value is Dictionary<string, object> typedDictionary)
            {
                result = typedDictionary;
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// 按 key 获取子列表。
        /// </summary>
        public static bool TryGetList(Dictionary<string, object> dictionary, string key, out IList<object>? result)
        {
            if (dictionary.TryGetValue(key, out object? value))
            {
                try
                {
                    if (value != null)
                    {
                        result = AsList(value);
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            result = null;
            return false;
        }

        /// <summary>
        /// 按 key 读取字符串，不存在时返回空字符串。
        /// </summary>
        public static string GetString(Dictionary<string, object> dictionary, string key)
        {
            if (!dictionary.TryGetValue(key, out object? value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value) ?? string.Empty;
        }

        /// <summary>
        /// 按 key 读取整数，不存在时返回 0。
        /// </summary>
        public static int GetInt(Dictionary<string, object> dictionary, string key)
        {
            if (!dictionary.TryGetValue(key, out object? value) || value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value);
        }

        /// <summary>
        /// 按 key 读取 long，不存在时返回 0。
        /// </summary>
        public static long GetLong(Dictionary<string, object> dictionary, string key)
        {
            if (!dictionary.TryGetValue(key, out object? value) || value == null)
            {
                return 0L;
            }

            return Convert.ToInt64(value);
        }

        /// <summary>
        /// JsonElement 对象节点转字典。
        /// </summary>
        private static Dictionary<string, object> ConvertToDictionary(JsonElement element)
        {
            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                object? convertedValue = ConvertToValue(property.Value);
                dictionary[property.Name] = convertedValue ?? string.Empty;
            }

            return dictionary;
        }

        /// <summary>
        /// JsonElement 数组节点转列表。
        /// </summary>
        private static IList<object> ConvertToList(JsonElement element)
        {
            var list = new List<object>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                object? convertedValue = ConvertToValue(item);
                list.Add(convertedValue ?? string.Empty);
            }

            return list;
        }

        /// <summary>
        /// JsonElement 值节点转换为 C# 基础类型。
        /// </summary>
        private static object? ConvertToValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    return ConvertToDictionary(element);
                case JsonValueKind.Array:
                    return ConvertToList(element);
                case JsonValueKind.String:
                    return element.GetString() ?? string.Empty;
                case JsonValueKind.Number:
                    {
                        if (element.TryGetInt64(out var longValue))
                        {
                            return longValue;
                        }

                        if (element.TryGetDouble(out var doubleValue))
                        {
                            return doubleValue;
                        }

                        return element.GetRawText();
                    }
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return null;
            }
        }
    }
}
