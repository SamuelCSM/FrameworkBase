using System;
using System.Threading;

namespace Framework.Security
{
    /// <summary>
    /// 反作弊值类型的公共设施：篡改上报与实例密钥发生器。
    /// <para>
    /// 定位：对抗 GameGuardian 类<b>内存搜索改值</b>——明文 int 在内存里搜"当前金币数"三次就能锁定地址。
    /// <see cref="AntiCheatInt"/> 等类型把真值异或实例密钥后存储（内存里搜不到明文），
    /// 另存校验和，直改混淆字段会在下次读取时校验失败并触发 <see cref="TamperDetected"/>。
    /// </para>
    /// <para>
    /// 边界（务必知晓）：这是<b>提高门槛</b>不是不可破——注入级作弊（hook 读写路径）挡不住，
    /// 强对抗要接专业方案；权威数据永远以服务端为准，本类型只保护"客户端自持的运行时数值"
    /// （单机数值、离线进度、本地校验用值）。持久化时取 <c>Value</c> 明文走加密存档
    /// （SaveManager AES），别把混淆态序列化落盘。
    /// </para>
    /// </summary>
    public static class AntiCheat
    {
        private static int _keyCounter = unchecked((int)0x9E3779B9);

        /// <summary>
        /// 检测到内存篡改时回调（参数为类型名，如 "AntiCheatInt"）。业务在此上报埋点/踢下线。
        /// 回调在读值线程同步触发，别做重活。默认无订阅（静默返回被篡改后的解码值）。
        /// </summary>
        public static event Action<string> TamperDetected;

        /// <summary>生成非零实例密钥（计数器混时钟，无锁）。</summary>
        internal static int NextKey()
        {
            int key = Interlocked.Add(ref _keyCounter, unchecked((int)0x61C88647)) ^ Environment.TickCount;
            return key != 0 ? key : 1;
        }

        internal static void ReportTamper(string typeName)
        {
            TamperDetected?.Invoke(typeName);
        }

        /// <summary>校验和混合函数：值与密钥双输入，直改任一存储字段都会失配。</summary>
        internal static int Mix(int value, int key)
        {
            unchecked
            {
                uint h = (uint)(value ^ key) * 0x85EBCA6Bu;
                h ^= h >> 13;
                h *= 0xC2B2AE35u;
                return (int)(h ^ (h >> 16));
            }
        }
    }

    /// <summary>
    /// 内存混淆 int：真值异或实例密钥存储 + 校验和防直改。用法与 int 一致（隐式互转）。
    /// <c>default</c> 实例读出 0 且不报篡改（未初始化态合法）；整结构清零攻击只能把值归零，
    /// 对金币/战力类数值属自残，不在防护目标内。
    /// </summary>
    public struct AntiCheatInt : IEquatable<AntiCheatInt>
    {
        private int _key;
        private int _obfuscated;
        private int _checksum;

        public AntiCheatInt(int value)
        {
            _key = AntiCheat.NextKey();
            _obfuscated = value ^ _key;
            _checksum = AntiCheat.Mix(value, _key);
        }

        /// <summary>当前值。读取时校验存储完整性，失配触发 <see cref="AntiCheat.TamperDetected"/>。</summary>
        public int Value
        {
            get
            {
                if (_key == 0)
                    return 0; // default 未初始化态

                int value = _obfuscated ^ _key;
                if (AntiCheat.Mix(value, _key) != _checksum)
                    AntiCheat.ReportTamper(nameof(AntiCheatInt));
                return value;
            }
            set
            {
                if (_key == 0)
                    _key = AntiCheat.NextKey();
                _obfuscated = value ^ _key;
                _checksum = AntiCheat.Mix(value, _key);
            }
        }

        public static implicit operator int(AntiCheatInt v) => v.Value;
        public static implicit operator AntiCheatInt(int v) => new AntiCheatInt(v);

        public bool Equals(AntiCheatInt other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AntiCheatInt other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }

    /// <summary>内存混淆 long：语义同 <see cref="AntiCheatInt"/>，用于货币/经验等可能溢出 int 的数值。</summary>
    public struct AntiCheatLong : IEquatable<AntiCheatLong>
    {
        private int _key;
        private long _obfuscated;
        private int _checksum;

        public AntiCheatLong(long value)
        {
            _key = AntiCheat.NextKey();
            long key64 = ((long)_key << 32) | (uint)_key;
            _obfuscated = value ^ key64;
            _checksum = AntiCheat.Mix((int)value ^ (int)(value >> 32), _key);
        }

        public long Value
        {
            get
            {
                if (_key == 0)
                    return 0;

                long key64 = ((long)_key << 32) | (uint)_key;
                long value = _obfuscated ^ key64;
                if (AntiCheat.Mix((int)value ^ (int)(value >> 32), _key) != _checksum)
                    AntiCheat.ReportTamper(nameof(AntiCheatLong));
                return value;
            }
            set
            {
                if (_key == 0)
                    _key = AntiCheat.NextKey();
                long key64 = ((long)_key << 32) | (uint)_key;
                _obfuscated = value ^ key64;
                _checksum = AntiCheat.Mix((int)value ^ (int)(value >> 32), _key);
            }
        }

        public static implicit operator long(AntiCheatLong v) => v.Value;
        public static implicit operator AntiCheatLong(long v) => new AntiCheatLong(v);

        public bool Equals(AntiCheatLong other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AntiCheatLong other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }

    /// <summary>内存混淆 float：按位异或混淆，语义同 <see cref="AntiCheatInt"/>。</summary>
    public struct AntiCheatFloat : IEquatable<AntiCheatFloat>
    {
        private int _key;
        private int _obfuscatedBits;
        private int _checksum;

        public AntiCheatFloat(float value)
        {
            _key = AntiCheat.NextKey();
            int bits = BitConverter.SingleToInt32Bits(value);
            _obfuscatedBits = bits ^ _key;
            _checksum = AntiCheat.Mix(bits, _key);
        }

        public float Value
        {
            get
            {
                if (_key == 0)
                    return 0f;

                int bits = _obfuscatedBits ^ _key;
                if (AntiCheat.Mix(bits, _key) != _checksum)
                    AntiCheat.ReportTamper(nameof(AntiCheatFloat));
                return BitConverter.Int32BitsToSingle(bits);
            }
            set
            {
                if (_key == 0)
                    _key = AntiCheat.NextKey();
                int bits = BitConverter.SingleToInt32Bits(value);
                _obfuscatedBits = bits ^ _key;
                _checksum = AntiCheat.Mix(bits, _key);
            }
        }

        public static implicit operator float(AntiCheatFloat v) => v.Value;
        public static implicit operator AntiCheatFloat(float v) => new AntiCheatFloat(v);

        public bool Equals(AntiCheatFloat other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is AntiCheatFloat other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }
}
