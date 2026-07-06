# 自定义类型解析指南

## 概述

Excel 工具支持多种方式来解析自定义类型，让配置表更加友好和灵活。

## 解析优先级

当遇到自定义类型时，解析器会按以下顺序尝试：

1. **CustomTypeParser 特性** - 使用特性指定的解析器
2. **静态 Parse 方法** - 调用类型的 `static T Parse(string)` 方法
3. **JSON 反序列化** - 使用 Unity 的 JsonUtility（保留功能）

## 方式一：静态 Parse 方法（推荐）

最简单直接的方式，在自定义类型中实现静态 Parse 方法。

### 示例 1：物品奖励（键值对格式）

```csharp
// Excel 格式：1001:10 表示物品ID为1001，数量为10
public class ItemReward
{
    public int ItemId { get; set; }
    public int Count { get; set; }

    // 实现静态 Parse 方法
    public static ItemReward Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ItemReward();
        }

        var parts = value.Split(':');
        if (parts.Length != 2)
        {
            throw new FormatException($"ItemReward 格式错误: {value}，期望格式: itemId:count");
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
```

**Excel 中的使用：**
```
| 奖励1      | 奖励2      | 奖励3      |
|-----------|-----------|-----------|
| 1001:10   | 1002:5    | 1003:1    |
```

### 示例 2：多个奖励（列表格式）

```csharp
// Excel 格式：1001:10,1002:5,1003:1 表示多个奖励
public class RewardList
{
    public List<ItemReward> Rewards { get; set; }

    public static RewardList Parse(string value)
    {
        var list = new RewardList { Rewards = new List<ItemReward>() };

        if (string.IsNullOrWhiteSpace(value))
        {
            return list;
        }

        var parts = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            list.Rewards.Add(ItemReward.Parse(part.Trim()));
        }

        return list;
    }
}
```

**Excel 中的使用：**
```
| 奖励列表                    |
|---------------------------|
| 1001:10,1002:5,1003:1     |
| 2001:20;2002:10           |
```

### 示例 3：范围值

```csharp
// Excel 格式：100-200 表示范围从100到200
public class IntRange
{
    public int Min { get; set; }
    public int Max { get; set; }

    public static IntRange Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new IntRange();
        }

        var parts = value.Split(new[] { '-', '~' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length == 1)
        {
            // 单个值，Min 和 Max 相同
            int val = int.Parse(parts[0].Trim());
            return new IntRange { Min = val, Max = val };
        }
        else if (parts.Length == 2)
        {
            return new IntRange
            {
                Min = int.Parse(parts[0].Trim()),
                Max = int.Parse(parts[1].Trim())
            };
        }

        throw new FormatException($"IntRange 格式错误: {value}，期望格式: min-max 或 value");
    }

    public int GetRandom()
    {
        return UnityEngine.Random.Range(Min, Max + 1);
    }
}
```

**Excel 中的使用：**
```
| 伤害范围  | 金币奖励  |
|----------|----------|
| 100-200  | 50-100   |
| 300      | 1000     |
```

## 方式二：CustomTypeParser 特性

当需要更复杂的解析逻辑，或者无法修改目标类型时，可以使用自定义解析器。

### 步骤 1：创建解析器类

```csharp
using Editor.ExcelTool;

public class SkillEffectParser : ICustomTypeParser
{
    public object Parse(string value, Type targetType)
    {
        // 格式：effectType|param1|param2
        // 示例：Damage|100|Fire
        
        var parts = value.Split('|');
        if (parts.Length < 1)
        {
            throw new FormatException("SkillEffect 格式错误");
        }

        var effect = new SkillEffect
        {
            EffectType = parts[0].Trim()
        };

        if (parts.Length > 1)
        {
            effect.Param1 = parts[1].Trim();
        }

        if (parts.Length > 2)
        {
            effect.Param2 = parts[2].Trim();
        }

        return effect;
    }

    public bool CanParse(Type targetType)
    {
        return targetType == typeof(SkillEffect);
    }
}
```

### 步骤 2：在类型上添加特性

```csharp
[CustomTypeParser(typeof(SkillEffectParser))]
public class SkillEffect
{
    public string EffectType { get; set; }
    public string Param1 { get; set; }
    public string Param2 { get; set; }
}
```

**Excel 中的使用：**
```
| 技能效果              |
|---------------------|
| Damage|100|Fire     |
| Heal|50|            |
| Buff|Speed|10       |
```

## 方式三：使用内置解析器

工具提供了一些常用类型的解析器。

### Vector2/Vector3

```csharp
public class MonsterConfig
{
    public Vector2 SpawnPosition { get; set; }  // 格式：x,y
    public Vector3 PatrolPoint { get; set; }    // 格式：x,y,z
}
```

**Excel 中的使用：**
```
| 出生位置  | 巡逻点        |
|----------|--------------|
| 100,200  | 10,0,20      |
| 150:250  | 15:0:25      |
```

### Color

```csharp
public class UIConfig
{
    public Color BackgroundColor { get; set; }
}
```

**Excel 中的使用：**
```
| 背景颜色      |
|-------------|
| #FF0000     |  (红色，十六进制)
| 255,0,0     |  (红色，RGB 0-255)
| 1.0,0,0     |  (红色，归一化 0-1)
| 0.5,0.5,0.5,0.8 | (半透明灰色，RGBA)
```

## 方式四：JSON 格式（保留）

虽然不太友好，但对于复杂嵌套结构仍然有用。

```csharp
public class ComplexData
{
    public int Id { get; set; }
    public string Name { get; set; }
    public List<int> Values { get; set; }
}
```

**Excel 中的使用：**
```
| 复杂数据                                    |
|-------------------------------------------|
| {"Id":1,"Name":"Test","Values":[1,2,3]}  |
```

## 最佳实践

### 1. 选择合适的格式

- **简单键值对**：使用 `:` 分隔，如 `itemId:count`
- **多个值**：使用 `,` 分隔，如 `value1,value2,value3`
- **多层结构**：使用不同分隔符，如 `1001:10,1002:5` 或 `type|param1|param2`
- **范围**：使用 `-` 或 `~`，如 `100-200`

### 2. 提供友好的错误信息

```csharp
public static ItemReward Parse(string value)
{
    var parts = value.Split(':');
    if (parts.Length != 2)
    {
        throw new FormatException(
            $"ItemReward 格式错误: '{value}'\n" +
            $"期望格式: itemId:count\n" +
            $"示例: 1001:10"
        );
    }
    // ...
}
```

### 3. 处理空值和默认值

```csharp
public static ItemReward Parse(string value)
{
    // 空值返回默认对象
    if (string.IsNullOrWhiteSpace(value))
    {
        return new ItemReward { ItemId = 0, Count = 0 };
    }
    // ...
}
```

### 4. 支持多种分隔符

```csharp
// 同时支持 : 和 = 作为分隔符
var parts = value.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
```

### 5. 添加 ToString 方法

方便调试和日志输出：

```csharp
public override string ToString()
{
    return $"{ItemId}:{Count}";
}
```

## 完整示例：技能配置

```csharp
// 技能配置类
[Table("skill_config")]
public class SkillConfig
{
    [PrimaryKey]
    public int Id { get; set; }
    
    public string Name { get; set; }
    
    // 伤害范围：100-200
    public IntRange Damage { get; set; }
    
    // 消耗：1001:10 (物品ID:数量)
    public ItemReward Cost { get; set; }
    
    // 多个效果：Damage|100|Fire,Buff|Speed|10
    public List<SkillEffect> Effects { get; set; }
    
    // 施法位置：0,0,0
    public Vector3 CastPosition { get; set; }
}

// IntRange 类
public class IntRange
{
    public int Min { get; set; }
    public int Max { get; set; }

    public static IntRange Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new IntRange();

        var parts = value.Split(new[] { '-', '~' });
        if (parts.Length == 1)
        {
            int val = int.Parse(parts[0].Trim());
            return new IntRange { Min = val, Max = val };
        }
        
        return new IntRange
        {
            Min = int.Parse(parts[0].Trim()),
            Max = int.Parse(parts[1].Trim())
        };
    }
}

// ItemReward 类
public class ItemReward
{
    public int ItemId { get; set; }
    public int Count { get; set; }

    public static ItemReward Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new ItemReward();

        var parts = value.Split(':');
        return new ItemReward
        {
            ItemId = int.Parse(parts[0].Trim()),
            Count = int.Parse(parts[1].Trim())
        };
    }
}

// SkillEffect 类
public class SkillEffect
{
    public string EffectType { get; set; }
    public string Param1 { get; set; }
    public string Param2 { get; set; }

    public static SkillEffect Parse(string value)
    {
        var parts = value.Split('|');
        return new SkillEffect
        {
            EffectType = parts[0].Trim(),
            Param1 = parts.Length > 1 ? parts[1].Trim() : "",
            Param2 = parts.Length > 2 ? parts[2].Trim() : ""
        };
    }
}

// 效果列表（支持多个效果）
public class SkillEffectList
{
    public List<SkillEffect> Effects { get; set; }

    public static SkillEffectList Parse(string value)
    {
        var list = new SkillEffectList { Effects = new List<SkillEffect>() };
        
        if (string.IsNullOrWhiteSpace(value))
            return list;

        var parts = value.Split(',');
        foreach (var part in parts)
        {
            list.Effects.Add(SkillEffect.Parse(part.Trim()));
        }

        return list;
    }
}
```

**Excel 表格：**

| Id | Name      | Damage  | Cost     | Effects                    | CastPosition |
|----|-----------|---------|----------|----------------------------|--------------|
| 1  | 火球术    | 100-150 | 1001:10  | Damage\|100\|Fire          | 0,0,0        |
| 2  | 治疗术    | 50-80   | 1002:5   | Heal\|50                   | 0,1,0        |
| 3  | 加速术    | 0       | 1003:1   | Buff\|Speed\|10,Buff\|Jump\|5 | 0,0,0     |

## 调试技巧

### 1. 添加日志

```csharp
public static ItemReward Parse(string value)
{
    Debug.Log($"[ItemReward] 开始解析: {value}");
    
    var parts = value.Split(':');
    var result = new ItemReward
    {
        ItemId = int.Parse(parts[0].Trim()),
        Count = int.Parse(parts[1].Trim())
    };
    
    Debug.Log($"[ItemReward] 解析结果: ItemId={result.ItemId}, Count={result.Count}");
    return result;
}
```

### 2. 单元测试

```csharp
[Test]
public void TestItemRewardParse()
{
    var reward = ItemReward.Parse("1001:10");
    Assert.AreEqual(1001, reward.ItemId);
    Assert.AreEqual(10, reward.Count);
}
```

## 常见问题

### Q: 如何处理可选字段？

A: 在 Parse 方法中检查数组长度：

```csharp
public static MyType Parse(string value)
{
    var parts = value.Split(':');
    return new MyType
    {
        Required = int.Parse(parts[0]),
        Optional = parts.Length > 1 ? int.Parse(parts[1]) : 0  // 默认值
    };
}
```

### Q: 如何支持多种格式？

A: 在 Parse 方法中检测格式：

```csharp
public static ItemReward Parse(string value)
{
    if (value.Contains(":"))
    {
        // 格式1: itemId:count
        var parts = value.Split(':');
        return new ItemReward { ItemId = int.Parse(parts[0]), Count = int.Parse(parts[1]) };
    }
    else if (value.Contains(","))
    {
        // 格式2: itemId,count
        var parts = value.Split(',');
        return new ItemReward { ItemId = int.Parse(parts[0]), Count = int.Parse(parts[1]) };
    }
    else
    {
        // 格式3: 只有itemId，count默认为1
        return new ItemReward { ItemId = int.Parse(value), Count = 1 };
    }
}
```

### Q: 如何处理嵌套类型？

A: 递归调用 Parse 方法：

```csharp
public class Quest
{
    public int Id { get; set; }
    public List<ItemReward> Rewards { get; set; }

    public static Quest Parse(string value)
    {
        // 格式: questId|reward1,reward2,reward3
        var parts = value.Split('|');
        var quest = new Quest
        {
            Id = int.Parse(parts[0]),
            Rewards = new List<ItemReward>()
        };

        if (parts.Length > 1)
        {
            var rewardParts = parts[1].Split(',');
            foreach (var rewardStr in rewardParts)
            {
                quest.Rewards.Add(ItemReward.Parse(rewardStr.Trim()));
            }
        }

        return quest;
    }
}
```

## 总结

推荐使用顺序：
1. **静态 Parse 方法** - 最简单直接，适合大多数情况
2. **CustomTypeParser 特性** - 适合复杂逻辑或无法修改目标类型
3. **JSON 格式** - 仅用于非常复杂的嵌套结构

选择合适的分隔符和格式，让策划在 Excel 中配置更加友好！
