#if UNITY_EDITOR || !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GameFramework.Net;

namespace GameFramework.ServerHost
{
    /// <summary>
    /// 后端进程内置的静态文件服务：把热更资源目录用 HTTP 暴露出去，
    /// 这样「逻辑服 + 资源站」只用一个进程、一份启动脚本，不必再单独开 Node 静态服务器。
    ///
    /// 刻意不用 HttpListener：Windows 上监听非 localhost 前缀需要管理员权限或 netsh 授权。
    /// 这里基于 TcpListener 实现最小可用的 HTTP/1.1 静态文件服务，零权限要求、跨平台。
    /// 只在独立后端进程编译（Unity 侧被 #if 排除）。
    /// </summary>
    public sealed class StaticFileServer : IDisposable
    {
        private static readonly Dictionary<string, string> ContentTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".txt", "text/plain; charset=utf-8" },
                { ".json", "application/json; charset=utf-8" },
                { ".js", "text/javascript; charset=utf-8" },
                { ".hash", "text/plain; charset=utf-8" },
                { ".version", "text/plain; charset=utf-8" },
                { ".bytes", "application/octet-stream" },
                { ".bundle", "application/octet-stream" },
                { ".unity3d", "application/octet-stream" },
            };

        private readonly INetLogger logger;
        private readonly string rootDirectory;
        private readonly TcpListener listener;
        private volatile bool running;

        /// <summary>实际监听的端口（传 0 时由系统分配）。</summary>
        public int Port { get; private set; }

        /// <summary>资源根目录（绝对路径）。</summary>
        public string RootDirectory { get { return rootDirectory; } }

        public StaticFileServer(string rootDirectory, int port, INetLogger logger, string bindAddress = null)
        {
            this.logger = logger ?? NullNetLogger.Instance;
            this.rootDirectory = Path.GetFullPath(rootDirectory);

            IPAddress address = null;
            if (!string.IsNullOrEmpty(bindAddress))
                IPAddress.TryParse(bindAddress, out address);

            listener = new TcpListener(address ?? IPAddress.Any, port);
        }

        /// <summary>开始监听。失败返回 false，error 里是原因（比如端口被占用）。</summary>
        public bool TryStart(out string error)
        {
            error = null;
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var endpoint = listener.LocalEndpoint as IPEndPoint;
            if (endpoint != null)
                Port = endpoint.Port;

            running = true;
            var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "StaticFileServer" };
            thread.Start();
            return true;
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!running) return;
                    continue;
                }

                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private void HandleClient(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                using (var stream = client.GetStream())
                {
                    // 静态资源只可能是 GET / HEAD，读到头部就够了，body 不读
                    var requestLine = ReadLine(stream);
                    if (requestLine == null) return;

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        WriteStatus(stream, 400, "Bad Request");
                        return;
                    }

                    var method = parts[0];
                    var rawPath = parts[1];

                    while (true)
                    {
                        var line = ReadLine(stream);
                        if (line == null || line.Length == 0) break;
                    }

                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteStatus(stream, 405, "Method Not Allowed");
                        return;
                    }

                    ServeFile(stream, rawPath, string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                logger.Warn("静态资源请求出错：" + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        private void ServeFile(NetworkStream stream, string rawPath, bool headOnly)
        {
            var filePath = ResolveFilePath(rawPath);
            if (filePath == null || !File.Exists(filePath))
            {
                logger.Warn("静态资源 404：" + rawPath);
                WriteStatus(stream, 404, "Not Found");
                return;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
            }
            catch (Exception ex)
            {
                logger.Warn("静态资源读取失败 " + rawPath + "：" + ex.Message);
                WriteStatus(stream, 500, "Internal Server Error");
                return;
            }

            string contentType;
            if (!ContentTypes.TryGetValue(info.Extension, out contentType))
                contentType = "application/octet-stream";

            var header = new StringBuilder();
            header.Append("HTTP/1.1 200 OK\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(info.Length).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Connection: close\r\n\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);

            if (!headOnly)
            {
                using (var file = File.OpenRead(filePath))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                        stream.Write(buffer, 0, read);
                }
            }

            stream.Flush();
        }

        /// <summary>把 URL 路径映射成根目录下的文件路径；越界（../）返回 null。</summary>
        private string ResolveFilePath(string rawPath)
        {
            var path = rawPath;
            var queryIndex = path.IndexOf('?');
            if (queryIndex >= 0) path = path.Substring(0, queryIndex);

            path = Uri.UnescapeDataString(path).Replace('\\', '/').TrimStart('/');
            if (path.Length == 0) return null;

            var combined = Path.GetFullPath(Path.Combine(rootDirectory, path));

            var root = rootDirectory.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? rootDirectory
                : rootDirectory + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            return combined;
        }

        private static string ReadLine(Stream stream)
        {
            var buffer = new List<byte>(128);
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0)
                    return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());

                if (value == '\n')
                {
                    if (buffer.Count > 0 && buffer[buffer.Count - 1] == '\r')
                        buffer.RemoveAt(buffer.Count - 1);
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }

                buffer.Add((byte)value);
                if (buffer.Count > 8192) return null;
            }
        }

        private static void WriteStatus(Stream stream, int code, string reason)
        {
            var body = Encoding.UTF8.GetBytes(code + " " + reason);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + code + " " + reason + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch (Exception) { }
        }
    }
}
#endif
