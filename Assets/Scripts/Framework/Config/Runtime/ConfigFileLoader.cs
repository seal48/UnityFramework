using System;
using System.IO;

namespace GameFramework.Config
{
    /// <summary>
    /// 服务端 / 离线工具用的配置表加载：直接从磁盘读 .bytes。
    /// 和客户端共用同一份生成代码，两边解析出来的数据完全一致。
    /// </summary>
    public static class ConfigFileLoader
    {
        /// <summary>把 directory 下的所有表读进一个新的 ConfigDatabase（每个表一个 "表名.bytes"）。</summary>
        public static ConfigDatabase Load(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException(nameof(directory));
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("[配置表] 目录不存在：" + directory);

            ConfigDatabase database = new ConfigDatabase();
            database.Load(name =>
            {
                string path = Path.Combine(directory, name + ".bytes");
                if (!File.Exists(path)) throw new ConfigFormatException("[配置表] 缺少文件：" + path);
                return File.ReadAllBytes(path);
            });
            return database;
        }
    }
}