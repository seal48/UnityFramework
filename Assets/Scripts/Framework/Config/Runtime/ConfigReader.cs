using System;
using System.IO;
using System.Text;

namespace GameFramework.Config
{
    /// <summary>配置表读取失败：文件头不对 / 版本不符 / 表结构与代码不一致。</summary>
    public sealed class ConfigFormatException : Exception
    {
        public ConfigFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// 配置表二进制读取器，客户端和服务端共用。
    ///
    /// 文件结构（小端）：
    ///   int32 magic         0x47464331
    ///   int32 formatVersion 格式版本，读写不一致直接报错
    ///   int32 schemaHash    字段名+类型的哈希，防止"表改了但没重新导出 / 没重新编译"
    ///   int32 rowCount
    ///   rowCount 行数据，按字段声明顺序排列
    ///
    /// 标量编码：int -&gt; int32，long -&gt; int64，float -&gt; float32，bool -&gt; byte(0/1)，
    ///           string -&gt; 7bit 长度 + UTF8，enum -&gt; int32
    /// 数组编码：int32 元素个数 + 各元素
    /// </summary>
    public sealed class ConfigReader : IDisposable
    {
        public const int Magic = 0x47464331;

        /// <summary>2：加入数组。改这里必须同步改 ConfigExporter 的输出。</summary>
        public const int FormatVersion = 2;

        private readonly MemoryStream stream;
        private readonly BinaryReader reader;
        private bool disposed;

        public ConfigReader(byte[] bytes, string tableName, int expectedSchemaHash)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            TableName = tableName ?? string.Empty;
            stream = new MemoryStream(bytes, false);
            reader = new BinaryReader(stream, Encoding.UTF8);

            int magic = reader.ReadInt32();
            if (magic != Magic)
                throw new ConfigFormatException($"[配置表] {TableName}：文件头不正确（0x{magic:X8}），可能不是配置表文件。");

            int version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new ConfigFormatException($"[配置表] {TableName}：格式版本不匹配（文件={version}，程序={FormatVersion}），请重新导出配置表。");

            SchemaHash = reader.ReadInt32();
            if (SchemaHash != expectedSchemaHash)
                throw new ConfigFormatException($"[配置表] {TableName}：表结构与代码不一致（文件={SchemaHash}，代码={expectedSchemaHash}），请重新导出配置表并重新编译。");

            RowCount = reader.ReadInt32();
            if (RowCount < 0)
                throw new ConfigFormatException($"[配置表] {TableName}：行数非法（{RowCount}）。");
        }

        public string TableName { get; private set; }
        public int SchemaHash { get; private set; }
        public int RowCount { get; private set; }

        /// <summary>还剩多少字节没读。导出工具用它检查"字段个数 / 顺序是否和 schema 对得上"。</summary>
        public long Remaining { get { return stream.Length - stream.Position; } }

        public int ReadInt() { return reader.ReadInt32(); }
        public long ReadLong() { return reader.ReadInt64(); }
        public float ReadFloat() { return reader.ReadSingle(); }
        public bool ReadBool() { return reader.ReadByte() != 0; }
        public string ReadString() { return reader.ReadString(); }

        public int[] ReadIntArray()
        {
            int count = ReadArrayCount();
            int[] array = new int[count];
            for (int i = 0; i < count; i++) array[i] = reader.ReadInt32();
            return array;
        }

        public long[] ReadLongArray()
        {
            int count = ReadArrayCount();
            long[] array = new long[count];
            for (int i = 0; i < count; i++) array[i] = reader.ReadInt64();
            return array;
        }

        public float[] ReadFloatArray()
        {
            int count = ReadArrayCount();
            float[] array = new float[count];
            for (int i = 0; i < count; i++) array[i] = reader.ReadSingle();
            return array;
        }

        public bool[] ReadBoolArray()
        {
            int count = ReadArrayCount();
            bool[] array = new bool[count];
            for (int i = 0; i < count; i++) array[i] = reader.ReadByte() != 0;
            return array;
        }

        public string[] ReadStringArray()
        {
            int count = ReadArrayCount();
            string[] array = new string[count];
            for (int i = 0; i < count; i++) array[i] = reader.ReadString();
            return array;
        }

        /// <summary>枚举数组：文件里按 int32 存，这里整体转成枚举数组。</summary>
        public T[] ReadEnumArray<T>() where T : struct, Enum
        {
            int count = ReadArrayCount();
            T[] array = new T[count];
            for (int i = 0; i < count; i++) array[i] = (T)Enum.ToObject(typeof(T), reader.ReadInt32());
            return array;
        }
        /// <summary>数组长度的合法性检查：每个元素至少占 1 字节，所以长度不可能超过剩余字节数。</summary>
        private int ReadArrayCount()
        {
            int count = reader.ReadInt32();
            long remaining = stream.Length - stream.Position;

            if (count < 0 || count > remaining)
                throw new ConfigFormatException($"[配置表] {TableName}：数组长度非法（{count}），文件已损坏或表结构与代码不一致。");

            return count;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            reader.Dispose();
            stream.Dispose();
        }
    }
}