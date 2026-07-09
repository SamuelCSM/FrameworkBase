# 本地化 / 多语言使用指南

## 定位

框架层的多语言取词与国际化基建，零业务依赖。负责：按当前语言从 `language` 配表取文案、
取词失败安全回退（绝不把原始 key 吐给玩家）、复数（CLDR plural）、书写方向（RTL）判定、
结果码 → 文案通用解析、开发期伪本地化。

**边界**：框架只做"方向决策"和"取哪条文案"，不做**字形整形**（阿拉伯语连字、双向重排由
TextMeshPro 负责）。翻译内容本身放在 `language` 配表（Excel → SQLite），不进代码。

## 核心概念

| 概念 | 说明 |
|---|---|
| `language` 配表 | 一行一个 key，每种语言一列（`zh_cn` / `en_us` / `ru_ru` …）。翻译内容的唯一来源。 |
| key 前缀 `#1` | 程序控制文案（Loading 状态、错误提示、动态拼接）。代码里主动 `Language.Get("#1_xxx")`。 |
| key 前缀 `#2` | UI 静态文本自动翻译，给 TextMeshProEx 在 Prefab 上直接挂。 |
| 取词回退 | 当前语言列为空 → 回退默认语言列 → 仍为空 → 返回原 key，保证不崩、不显示空白。 |
| `LanguageType` | 代码侧安全选语言的枚举；映射到配表列名见 `Language.ToCode`。 |

## 基础取词

```csharp
// 简单取词（找不到返回原 key，永不抛异常）
string title = Language.Get("#1_main_title");

// 带参数（string.Format 规则，格式错误时安全返回未格式化文案）
string welcome = Language.Get("#1_welcome_player", playerName);
string coins = Language.Get("#1_coins_amount", 1280); // "你有 {0} 金币"

// UI 自动翻译：只有 #2 前缀会翻，普通串原样返回（避免误翻玩家名/版本号）
string label = Language.ResolveAutoText(text);
```

## 切换语言

```csharp
// 用枚举切（推荐，编译期安全）
Language.SetLanguage(LanguageType.RuRu);

// 或用代码切（支持 ru-RU / ru_ru 等写法，内部会规范化）
Language.SetLanguage("ja_jp");

// 当前语言
string cur = Language.CurrentLanguage;          // "ru_ru"
LanguageType curType = Language.CurrentLanguageType;
```

切语言会通过 `GameMessage.LanguageChanged` 广播；TextMeshProEx 等已订阅，自动刷新已显示文本。
手动刷新用 `Language.Refresh()`（配表热更替换后也调它）。

### 支持的语言

`LanguageType` 是框架能翻译到的语言全集（简中/繁中/英/日/韩/法/德/西/葡(巴西)/俄/阿拉伯/泰/越/印尼/土）。
**具体某项目开放哪几种由 app 决定**——只要在 `language` 配表建了对应列即可；没建列的语言取词时
回退默认语言，不会崩。新增语言见文末「扩展新语言」。

## 复数（Plural）

不同语言的复数形态数量不同：中文只有 1 种，英语 2 种（one/other），俄语 4 种
（one/few/many/other），阿拉伯语 6 种。`GetPlural` 按当前语言的 **CLDR 规则**自动选变体。

### 配表约定

给同一条复数文案建**多列变体**，key 用 `{keyBase}_{类别}` 后缀：

| key | zh_cn | en_us | ru_ru |
|---|---|---|---|
| `#1_apple_count_one`   | `{0} 个苹果` | `{0} apple`  | `{0} яблоко` |
| `#1_apple_count_few`   | —           | —            | `{0} яблока` |
| `#1_apple_count_many`  | —           | —            | `{0} яблок`  |
| `#1_apple_count_other` | `{0} 个苹果` | `{0} apples` | `{0} яблока` |

**至少建 `_other`**（其余变体缺失时回退到它）。中文这类无复数变化的语言只需 `_other` 一条。

### 代码

```csharp
// keyBase 不带类别后缀；count 既参与复数判定，也作为默认 {0}
string txt = Language.GetPlural("#1_apple_count", 5);
//   en → "5 apples"   ru → "5 яблок"   zh → "5 个苹果"   ar → 走 many/other

// 自定义格式化参数（不想用 count 当 {0} 时）
string txt2 = Language.GetPlural("#1_reward_days", days, days, bonusName);
```

三级兜底：`{keyBase}_{类别}` → `{keyBase}_other` → `keyBase`（绝不吐残缺 key）。

> 想脱离配表直接判类别（写工具、单测），用纯逻辑 `PluralRules.Select(lang, number)`
> → 返回 `PluralCategory`。未登记的语言安全退化为 `Other`。

## 书写方向（RTL）

阿拉伯语、希伯来语等从右到左书写。框架给出**方向决策**，UI 层据此镜像布局。

```csharp
// 当前语言方向
if (Language.IsCurrentRightToLeft)
    layoutGroup.reverseArrangement = true;   // 例：翻转横向排列

TextDirection dir = Language.CurrentDirection; // LeftToRight / RightToLeft

// 给动态文本（用户名、聊天、搜索词）自动定向：
// 即便 UI 语言是英文，一段阿拉伯语用户名也应右对齐
bool rtl = TextDirectionResolver.ContainsRightToLeft(userInput);
text.alignment = rtl ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
```

> **框架不做字形整形**：阿拉伯字母连写、双向算法重排由 TextMeshPro 处理。
> 框架只回答"这段该往哪个方向排、该左对齐还是右对齐"。

## 结果码 → 文案

协议结果码（房间码、登录码等）统一走 `LocalizedResult`，无需为每个结果域写一个方法：

```csharp
// key 约定：#1_{枚举名snake_case}_{code}，缺失回退 #1_{域}_unknown
string msg = LocalizedResult.Of<RoomResultCode>(code);
// RoomResultCode.Full(=3) → 查 "#1_room_result_code_3"，缺则 "#1_room_result_code_unknown"
```

新增结果枚举**无需改框架**，只在 `language` 表补 `#1_{域}_*` 行即可。

## 伪本地化（开发期查漏）

不等真翻译就暴露"写死没走本地化"和"字体缺字/UI 截断"问题：

```csharp
// 仅 Editor / Development Build 生效，正式包零开销
PseudoLocalizer.Enabled = true;
Language.Refresh();
// "Welcome" → "⟦Ẃéĺćóḿé·~·⟧"
//   · 拉丁字母加重音 → 字体缺字一眼可见
//   · 追加 ~30% 长度 → 提前暴露 UI 截断（德/俄语更长）
//   · 前后 ⟦⟧ 界标 → 没被包住的屏幕文本 = 写死没走 Language
//   · {0}/{1:N0} 占位符原样保留，不影响 Format
```

## 扩展新语言

1. **加枚举**：`LanguageType` 补一项（如 `PlPl`），写好 XML 注释标出列名。
2. **加映射**：`Language.cs` 的 `CodeByType` 表补一行 `{ LanguageType.PlPl, "pl_pl" }`
   （双向转换由此单一源派生，只改这一处）。
3. **加配表列**：`language` 表加 `pl_pl` 列，填翻译；复数语言按上面的约定补 `_one/_few/_many/_other` 变体。
4. **复数规则**（可选）：若该语言的复数家族尚未内置，在 `PluralRules.Select` 的分派表补一条；
   不补则安全退化为 `other`（单一变体）。
5. **RTL**（可选）：若是从右到左语言，在 `TextDirectionResolver.RtlLanguages` 加主子标签。

## 相关类型

| 类型 | 职责 |
|---|---|
| `Language` | 取词入口、切语言、`GetPlural`、方向便捷属性。 |
| `LanguageType` | 语言枚举（代码侧安全选语言）。 |
| `PluralRules` / `PluralCategory` | CLDR 复数分类（纯逻辑，可单测）。 |
| `TextDirectionResolver` / `TextDirection` | 书写方向判定（纯逻辑，可单测）。 |
| `LocalizedResult` | 结果码 → 文案通用解析。 |
| `PseudoLocalizer` | 开发期伪本地化。 |
