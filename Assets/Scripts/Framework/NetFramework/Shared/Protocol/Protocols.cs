using System;
using System.Collections.Generic;
using System.Text;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 全部消息（协议）的编号。
    /// ★ 前端、后端、测试都用这一份，新增消息只改这个文件 ★
    ///
    /// 加一条新消息：
    ///   1) 在下面 ProtocolId 里加一个成员，比如 PlayerMove；
    ///   2) 在 Protocols.Names 表里加一行 { ProtocolId.PlayerMove, "player.move" }。
    /// 别的文件不用动：收发、注册处理函数、校验、日志都会自动认得它。
    /// </summary>
    public enum ProtocolId
    {
        /// <summary>没登记过的协议名（收到未知名字时用它表示）。</summary>
        Unknown = 0,

        // ===== 登录 =====
        RegisterRequest,
        RegisterResponse,
        LoginRequest,
        LoginResponse,

        // ===== 框架内置的系统协议（建议保留）=====
        SystemPing,
        SystemPong,
        SystemHeartbeat,
        SystemHeartbeatAck,
        SystemBye,

        // ===== 业务协议：照这个格式加你自己的 =====
        // PlayerMove,

        // ===== 业务协议：服务端主动推送 =====
        // 服务端不等客户端请求，自己把数据推下来（登录同步、数值变化…）
        // 客户端由 ProtocolHub 收下来，再分给各个 System 处理
        PlayerInfoPush,
        BagChangedPush,
    }

    /// <summary>
    /// 协议名总表，以及"编号 ↔ 线上名字"的互转。
    /// 线上名字就是写进包头的那串 UTF-8 字节，改名字只改这里的字符串。
    /// </summary>
    public static class Protocols
    {
        /// <summary>左边是代码里用的编号，右边是真正发到网络上的名字。</summary>
        private static readonly Dictionary<ProtocolId, string> Names = new Dictionary<ProtocolId, string>
        {
            // ---- 登录 ----
            { ProtocolId.RegisterRequest,  "register.request" },
            { ProtocolId.RegisterResponse, "register.response" },
            { ProtocolId.LoginRequest,  "login.request" },
            { ProtocolId.LoginResponse, "login.response" },

            // ---- 系统协议 ----
            { ProtocolId.SystemPing,    "system.ping" },
            { ProtocolId.SystemPong,    "system.pong" },
            { ProtocolId.SystemHeartbeat,    "system.heartbeat" },
            { ProtocolId.SystemHeartbeatAck, "system.heartbeat.ack" },
            { ProtocolId.SystemBye,          "system.bye" },

            // ---- 业务协议（改成你自己的）----

            // ---- 业务协议：服务端主动推送 ----
            { ProtocolId.PlayerInfoPush, "player.info.push" },
            { ProtocolId.BagChangedPush, "player.bag.changed" },
        };

        private static readonly Dictionary<string, ProtocolId> Ids = BuildReverseTable();
        private static readonly string[] AllNamesArray = BuildNameArray();

        static Protocols()
        {
#if UNITY_EDITOR || DEBUG
            // 编辑器/调试构建下启动即自检：漏登记、重名、名字过长都会立刻报出来，
            // 不会等到联调时才发现"某条消息发出去没人处理"。
            if (!Validate(out var error))
                throw new InvalidOperationException("协议表配置有误：" + error);
#endif
        }

        /// <summary>所有登记过的协议名（顺序与 ProtocolId 一致，可用来做调试面板/校验）。</summary>
        public static IReadOnlyList<string> AllNames => AllNamesArray;

        /// <summary>编号 → 线上名字。</summary>
        public static string NameOf(ProtocolId id)
        {
            if (Names.TryGetValue(id, out var name)) return name;
            throw new KeyNotFoundException(
                $"ProtocolId.{id} 还没有在 Protocols.Names 表里登记（加一行就可以用了）");
        }

        public static bool TryGetName(ProtocolId id, out string name) => Names.TryGetValue(id, out name);

        /// <summary>线上名字 → 编号；没登记过返回 ProtocolId.Unknown。</summary>
        public static ProtocolId IdOf(string name)
            => name != null && Ids.TryGetValue(name, out var id) ? id : ProtocolId.Unknown;

        public static bool TryGetId(string name, out ProtocolId id)
            => Ids.TryGetValue(name ?? string.Empty, out id);

        public static bool IsKnown(string name) => name != null && Ids.ContainsKey(name);

        /// <summary>
        /// 校验协议表：枚举成员是否都登记了、名字是否重复、是否符合包头长度限制、互转是否闭环。
        /// </summary>
        public static bool Validate(out string error)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (ProtocolId id in Enum.GetValues(typeof(ProtocolId)))
            {
                if (id == ProtocolId.Unknown) continue;

                if (!Names.TryGetValue(id, out var name))
                {
                    error = $"ProtocolId.{id} 没有在 Protocols.Names 里登记";
                    return false;
                }

                if (string.IsNullOrEmpty(name))
                {
                    error = $"ProtocolId.{id} 的名字是空的";
                    return false;
                }

                if (!seen.Add(name))
                {
                    error = $"协议名重复：'{name}'（同一个名字被登记了两次）";
                    return false;
                }

                var byteCount = Encoding.UTF8.GetByteCount(name);
                if (byteCount < NetConfig.MinProtocolNameBytes || byteCount > NetConfig.MaxProtocolNameBytes)
                {
                    error = $"协议名 '{name}' 有 {byteCount} 字节，超出包头允许的 "
                            + $"{NetConfig.MinProtocolNameBytes}~{NetConfig.MaxProtocolNameBytes} 字节";
                    return false;
                }

                if (Ids.TryGetValue(name, out var roundTrip) && roundTrip != id)
                {
                    error = $"协议名 '{name}' 反查出来的编号是 {roundTrip}，与 {id} 不一致";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static Dictionary<string, ProtocolId> BuildReverseTable()
        {
            var table = new Dictionary<string, ProtocolId>(Names.Count, StringComparer.Ordinal);
            foreach (var pair in Names) table[pair.Value] = pair.Key;
            return table;
        }

        private static string[] BuildNameArray()
        {
            var list = new List<string>(Names.Count);
            foreach (ProtocolId id in Enum.GetValues(typeof(ProtocolId)))
            {
                if (id == ProtocolId.Unknown) continue;
                if (Names.TryGetValue(id, out var name)) list.Add(name);
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// 让收发和注册都能直接传编号，不用再手写字符串：
    ///   peer.Send(ProtocolId.DemoEcho, new EchoRequest { ... });
    ///   router.Register(ProtocolId.DemoEcho, (msg, peer) => { ... });
    /// 需要动态协议名（配置驱动、未知协议转发）时继续用字符串重载即可。
    /// </summary>
    public static class ProtocolIdExtensions
    {
        public static void Send(this IMessagePeer peer, ProtocolId protocol, object payload = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            peer.Send(Protocols.NameOf(protocol), payload);
        }
    }
}
