using System;
using System.Collections.Generic;
using System.Text;

namespace Framework
{
    /// <summary>
    /// 结果码 → 本地化文案的通用解析器（框架层，零业务依赖）。
    /// 约定 key：<c>{prefix}_{domain}_{code}</c>，缺失回退 <c>{prefix}_{domain}_unknown</c>，杜绝把原始 key 吐给玩家。
    /// </summary>
    /// <remarks>
    /// <para><b>domain</b> 由结果枚举类型名机械推导（PascalCase → snake_case），例如 <c>RoomResultCode</c> → <c>room_result_code</c>。
    /// 因此新增任何结果枚举都<b>无需改动本类</b>——只要定义枚举（建议写进 proto Enum 生成双端）并在 language 表补
    /// <c>{prefix}_{domain}_*</c> 行即可，避免"每个结果域加一个方法"的线性膨胀。</para>
    /// <para><b>prefix</b> 默认 <c>#1</c>（本项目"程序控制文案"key 约定），是 app 策略而非框架内核，故开放为可选参数。</para>
    /// </remarks>
    public static class LocalizedResult
    {
        /// <summary>默认 key 前缀：#1 = 程序控制文案（本项目多语言 key 约定）。</summary>
        public const string DefaultPrefix = "#1";

        /// <summary>枚举类型 → domain 名缓存（避免重复反射与字符串转换；UI 主线程访问）。</summary>
        private static readonly Dictionary<Type, string> DomainCache = new Dictionary<Type, string>();

        /// <summary>
        /// 按结果码整型值解析为本地化文案（结果码常以协议 int 形态到达，调用处显式指定枚举类型）。
        /// </summary>
        /// <typeparam name="TEnum">结果枚举类型（domain 由其类型名推导）。</typeparam>
        /// <param name="code">结果码整型值。</param>
        /// <param name="prefix">key 前缀，默认 <see cref="DefaultPrefix"/>。</param>
        /// <returns>本地化文案。</returns>
        public static string Of<TEnum>(int code, string prefix = DefaultPrefix) where TEnum : struct, Enum
            => Resolve(DomainOf(typeof(TEnum)), code, prefix);

        /// <summary>
        /// 按结果枚举值解析为本地化文案。
        /// </summary>
        /// <typeparam name="TEnum">结果枚举类型。</typeparam>
        /// <param name="code">结果枚举值。</param>
        /// <param name="prefix">key 前缀，默认 <see cref="DefaultPrefix"/>。</param>
        /// <returns>本地化文案。</returns>
        public static string Of<TEnum>(TEnum code, string prefix = DefaultPrefix) where TEnum : struct, Enum
            => Of<TEnum>(Convert.ToInt32(code), prefix);

        /// <summary>
        /// 按 domain + code 解析：命中 <c>{prefix}_{domain}_{code}</c> 即返回，否则回退 <c>{prefix}_{domain}_unknown</c>。
        /// </summary>
        /// <param name="domain">结果域名。</param>
        /// <param name="code">结果码整型值。</param>
        /// <param name="prefix">key 前缀。</param>
        /// <returns>本地化文案。</returns>
        public static string Resolve(string domain, int code, string prefix = DefaultPrefix)
        {
            return Language.TryGet($"{prefix}_{domain}_{code}", out string text)
                ? text
                : Language.Get($"{prefix}_{domain}_unknown");
        }

        /// <summary>取枚举类型对应的 domain 名（带缓存）。</summary>
        /// <param name="enumType">枚举类型。</param>
        /// <returns>snake_case 域名。</returns>
        private static string DomainOf(Type enumType)
        {
            if (!DomainCache.TryGetValue(enumType, out string domain))
            {
                domain = ToSnakeCase(enumType.Name);
                DomainCache[enumType] = domain;
            }

            return domain;
        }

        /// <summary>PascalCase → snake_case（在每个大写字母前插下划线，首字母除外）。</summary>
        /// <param name="name">类型名。</param>
        /// <returns>snake_case 名。</returns>
        private static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        sb.Append('_');
                    }

                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}
