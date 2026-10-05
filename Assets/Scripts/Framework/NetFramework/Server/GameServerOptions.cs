using GameFramework.Net.Protocol;
using GameFramework.Net.Server.Persistence;
using GameFramework.Net.Threading;
using System;
using System.Collections.Generic;

namespace GameFramework.Net.Server
{
    /// <summary>服务器配置。</summary>
    public sealed class GameServerOptions
    {
        /// <summary>服务器名字，登录成功时回给客户端。</summary>
        public string ServerName = "localserver";

        /// <summary>监听地址。0.0.0.0 表示监听所有网卡；本地测试用 127.0.0.1。</summary>
        public string Host = NetConfig.DefaultHost;

        /// <summary>监听端口。填 0 表示由系统分配空闲端口（实际端口看 GameServer.Port）。</summary>
        public int Port = NetConfig.DefaultPort;

        public int Backlog = NetConfig.DefaultListenBacklog;

        /// <summary>最大同时连接数，超出的连接会被立即断开。</summary>
        public int MaxConnections = NetConfig.DefaultMaxConnections;

        /// <summary>消息内容序列化器，前后端必须一致。</summary>
        public IMessageSerializer Serializer = JsonMessageSerializer.Instance;

        /// <summary>日志出口，默认输出到控制台。</summary>
        public INetLogger Logger;

        /// <summary>处理函数在哪个线程执行。后端默认就在接收线程上跑（简单直接）。</summary>
        public IDispatcher Dispatcher;

        /// <summary>是否打印收到的每条消息（调试用）。</summary>
        public bool LogReceivedFrames = true;

        /// <summary>是否打印发出的每条消息（调试用）。</summary>
        public bool LogSentFrames;

        /// <summary>定时线程的间隔（毫秒）。服务端 Delay / Repeat 的精度上限就是它，默认 15ms。</summary>
        public int TimerIntervalMs = 15;

        /// <summary>
        /// 这些协议的消息不打印帧日志。默认排除心跳（5 秒一条，打出来会刷屏）。
        /// 想看到心跳日志就把它从集合里移除。
        /// </summary>
        public HashSet<string> FrameLogExclusions = new HashSet<string>(StringComparer.Ordinal)
        {
            Protocols.NameOf(ProtocolId.SystemHeartbeat),
            Protocols.NameOf(ProtocolId.SystemHeartbeatAck)
        };

        /// <summary>是否自动注册内置协议（login.request + system.ping / system.heartbeat / system.bye）。</summary>
        public bool RegisterBuiltinProtocols = true;

        /// <summary>
        /// 登录校验器。不配置时登录一律失败（服务器会打日志提示），
        /// 不配置时默认用 AccountService（账号存在下面的 AccountStore 里），
        /// 接第三方鉴权时可以自己实现 ILoginValidator 塞进来。
        /// </summary>
        public ILoginValidator LoginValidator;

        /// <summary>
        /// 账号/玩家数据存储。不设置时会按 AccountStorePath 自动创建一个 JSON 存储。
        /// 独立后端进程建议换成 SqliteAccountStore（单文件数据库、有事务和索引）。
        /// </summary>
        public IAccountStore AccountStore;

        /// <summary>没显式指定 AccountStore 时，JSON 账号文件的路径（相对/绝对都行）。</summary>
        public string AccountStorePath = "GameData/accounts.json";

        /// <summary>登录时账号不存在就自动注册（只建议本地开发打开）。</summary>
        public bool AutoRegisterOnLogin;

        /// <summary>
        /// 是否要求先登录：开启时，未登录的连接除 login.request / system.bye 以外的消息会被丢弃并打警告。
        /// </summary>
        public bool RequireLogin = true;

        /// <summary>
        /// 未登录也允许发送的协议（默认：注册、登录、告别、ping）。
        /// 心跳不在里面 —— 它意味着已经登录了。
        /// </summary>
        public HashSet<ProtocolId> PreLoginProtocols = new HashSet<ProtocolId>
        {
            ProtocolId.RegisterRequest,
            ProtocolId.LoginRequest,
            ProtocolId.SystemBye,
            ProtocolId.SystemPing
        };

        /// <summary>登录失败后是否直接断开这条连接。</summary>
        public bool CloseOnLoginFailure = true;

        /// <summary>是否开放客户端注册（register.request）。线上服务器可以关掉，只让运营后台开号。</summary>
        public bool AllowRegister = true;
    }
}
