using System;
using System.Collections.Generic;
using UnityEngine;

namespace Editor.ExcelTool.Examples
{
    /// <summary>
    /// 自定义类型示例
    /// 演示如何定义可以从 Excel 友好解析的自定义类型
    /// 
    /// 分隔符使用规范：
    /// - 逗号 (,)  : 用于同级元素分隔（数组、列表、坐标、颜色等）
    /// - 冒号 (:)  : 用于键值对（如 itemId:count）
    /// - 分号 (;)  : 用于不同记录/组的分隔（如多个奖励组）
    /// - 竖线 (|)  : 用于复杂结构的字段分隔
    /// - 星号 (*)  : 用于权重表示（如 item*weight）
    /// - 减号/波浪 (-/~) : 用于范围表示（如 100-200）
    /// 
    /// 示例：
    /// - 单个奖励: 1001:10
    /// - 多个奖励: 1001:10;1002:5;1003:1
    /// - 坐标: 100,200
    /// - 颜色: 255,0,0
    /// - 范围: 100-200
    /// - 权重: item1*10;item2*20
    /// </summary>

    #region 示例 1: 物品奖励（键值对格式）

    /// <summary>
    /// 物品奖励
    /// Excel 格式：1001:10 表示物品ID为1001，数量为10
    /// </summary>
    [Serializable]
    public class ItemReward
    {
        public int ItemId;
        public int Count;

        public ItemReward() { }

        public ItemReward(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        /// <summary>
        /// 从字符串解析
        /// </summary>
        public static ItemReward Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new ItemReward(0, 0);
            }

            var parts = value.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length != 2)
            {
                throw new FormatException(
                    $"ItemReward 格式错误: '{value}'\n" +
                    $"期望格式: itemId:count\n" +
                    $"示例: 1001:10"
                );
            }

            return new ItemReward
            {
                ItemId = int.Parse(parts[0].Trim()),
                Count = int.Parse(parts[1].Trim())
            };
        }

        public override string ToString()
        {
            return $"{ItemId}:{Count}";
        }
    }

    #endregion

    #region 示例 2: 奖励列表（多个奖励）

    /// <summary>
    /// 奖励列表
    /// Excel 格式：1001:10;1002:5;1003:1 表示多个奖励（使用分号分隔不同奖励）
    /// </summary>
    [Serializable]
    public class RewardList
    {
        public List<ItemReward> Rewards = new List<ItemReward>();

        public static RewardList Parse(string value)
        {
            var list = new RewardList();

            if (string.IsNullOrWhiteSpace(value))
            {
                return list;
            }

            // 使用分号分隔不同的奖励
            var parts = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                list.Rewards.Add(ItemReward.Parse(part.Trim()));
            }

            return list;
        }

        public override string ToString()
        {
            return string.Join(";", Rewards);
        }
    }

    #endregion

    #region 示例 3: 数值范围

    /// <summary>
    /// 整数范围
    /// Excel 格式：100-200 或 100~200 表示范围从100到200
    /// 也支持单个值：100 表示 Min=Max=100
    /// </summary>
    [Serializable]
    public class IntRange
    {
        public int Min;
        public int Max;

        public IntRange() { }

        public IntRange(int min, int max)
        {
            Min = min;
            Max = max;
        }

        public static IntRange Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new IntRange(0, 0);
            }

            var parts = value.Split(new[] { '-', '~' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length == 1)
            {
                // 单个值，Min 和 Max 相同
                int val = int.Parse(parts[0].Trim());
                return new IntRange(val, val);
            }
            else if (parts.Length == 2)
            {
                return new IntRange
                {
                    Min = int.Parse(parts[0].Trim()),
                    Max = int.Parse(parts[1].Trim())
                };
            }

            throw new FormatException($"IntRange 格式错误: '{value}'，期望格式: min-max 或 value");
        }

        /// <summary>
        /// 获取随机值
        /// </summary>
        public int GetRandom()
        {
            return UnityEngine.Random.Range(Min, Max + 1);
        }

        public override string ToString()
        {
            return Min == Max ? $"{Min}" : $"{Min}-{Max}";
        }
    }

    #endregion

    #region 示例 4: 技能效果（使用自定义解析器）

    /// <summary>
    /// 技能效果
    /// Excel 格式：Damage|100|Fire 表示伤害效果，参数100，元素火
    /// </summary>
    [Serializable]
    [CustomTypeParser(typeof(SkillEffectParser))]
    public class SkillEffect
    {
        public string EffectType;
        public string Param1;
        public string Param2;

        public override string ToString()
        {
            return $"{EffectType}|{Param1}|{Param2}";
        }
    }

    /// <summary>
    /// 技能效果解析器
    /// </summary>
    public class SkillEffectParser : ICustomTypeParser
    {
        public object Parse(string value, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new SkillEffect();
            }

            var parts = value.Split('|');
            
            return new SkillEffect
            {
                EffectType = parts.Length > 0 ? parts[0].Trim() : "",
                Param1 = parts.Length > 1 ? parts[1].Trim() : "",
                Param2 = parts.Length > 2 ? parts[2].Trim() : ""
            };
        }

        public bool CanParse(Type targetType)
        {
            return targetType == typeof(SkillEffect);
        }
    }

    #endregion

    #region 示例 5: 坐标点

    /// <summary>
    /// 2D 坐标点
    /// Excel 格式：100,200（使用逗号分隔）
    /// </summary>
    [Serializable]
    public class Point2D
    {
        public int X;
        public int Y;

        public static Point2D Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Point2D();
            }

            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length < 2)
            {
                throw new FormatException($"Point2D 格式错误: '{value}'，期望格式: x,y");
            }

            return new Point2D
            {
                X = int.Parse(parts[0].Trim()),
                Y = int.Parse(parts[1].Trim())
            };
        }

        public override string ToString()
        {
            return $"{X},{Y}";
        }
    }

    #endregion

    #region 示例 6: 权重配置

    /// <summary>
    /// 权重配置
    /// Excel 格式：item1*10;item2*20;item3*30 表示不同权重的物品（使用分号分隔不同项）
    /// </summary>
    [Serializable]
    public class WeightedItem
    {
        public string ItemName;
        public int Weight;

        public static WeightedItem Parse(string value)
        {
            var parts = value.Split('*');
            return new WeightedItem
            {
                ItemName = parts[0].Trim(),
                Weight = parts.Length > 1 ? int.Parse(parts[1].Trim()) : 1
            };
        }

        public override string ToString()
        {
            return $"{ItemName}*{Weight}";
        }
    }

    /// <summary>
    /// 权重列表
    /// </summary>
    [Serializable]
    public class WeightedItemList
    {
        public List<WeightedItem> Items = new List<WeightedItem>();

        public static WeightedItemList Parse(string value)
        {
            var list = new WeightedItemList();

            if (string.IsNullOrWhiteSpace(value))
            {
                return list;
            }

            // 使用分号分隔不同的权重项
            var parts = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                list.Items.Add(WeightedItem.Parse(part.Trim()));
            }

            return list;
        }

        /// <summary>
        /// 根据权重随机选择一个物品
        /// </summary>
        public string GetRandomItem()
        {
            int totalWeight = 0;
            foreach (var item in Items)
            {
                totalWeight += item.Weight;
            }

            int random = UnityEngine.Random.Range(0, totalWeight);
            int current = 0;

            foreach (var item in Items)
            {
                current += item.Weight;
                if (random < current)
                {
                    return item.ItemName;
                }
            }

            return Items.Count > 0 ? Items[0].ItemName : "";
        }
    }

    #endregion
}
