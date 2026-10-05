using System;
using GameFramework.Net.Protocol;
using GameFramework.Net.Server.Persistence;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 登录校验接口。框架默认用 AccountService（账号存在 JSON 或 SQLite 里）；
    /// 要接外部鉴权（HTTP/OAuth/第三方 SDK）就自己实现这个接口，塞进 GameServerOptions.LoginValidator。
    /// </summary>
    public interface ILoginValidator
    {
        LoginResponse Validate(LoginRequest request, IMessagePeer peer);
    }

    /// <summary>把 login.request 挂到服务器上。</summary>
    public static class LoginProtocols
    {
        public static void Register(GameServer server, ILoginValidator validator)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            server.Router.RegisterOrReplace(ProtocolId.LoginRequest, (message, peer) =>
            {
                var request = message.Deserialize<LoginRequest>(peer.Serializer);
                var session = PeerSession.GetOrCreate(peer);

                if (session.LoggedIn)
                {
                    peer.Send(ProtocolId.LoginResponse, Failure(LoginReasons.AlreadyLoggedIn, "这条连接已经登录过了"));
                    return;
                }

                LoginResponse response;
                try
                {
                    response = validator != null
                        ? validator.Validate(request, peer)
                        : Failure(LoginReasons.Rejected, "服务器没有配置登录校验器");
                }
                catch (Exception ex)
                {
                    server.Logger.Error($"[{peer.PeerId}] 登录校验异常", ex);
                    response = Failure(LoginReasons.Rejected, "服务器内部错误");
                }

                response.ServerName = server.ServerName;
                response.ProtocolVersion = SystemProtocols.ProtocolVersion;
                response.ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (!response.Success)
                {
                    peer.Send(ProtocolId.LoginResponse, response);
                    server.Logger.Warn(
                        $"[{peer.PeerId}] 登录失败：account='{request?.Account}'，原因={response.Reason}（{response.Message}）");

                    if (server.Options.CloseOnLoginFailure) peer.Close("登录失败：" + response.Reason);
                    return;
                }

                session.LoggedIn = true;
                session.Account = request?.Account;
                session.PlayerId = response.PlayerId;
                session.SessionId = response.SessionId;
                session.LoginAtUtc = DateTime.UtcNow;

                peer.Send(ProtocolId.LoginResponse, response);
                server.Logger.Info(
                    $"[{peer.PeerId}] 登录成功：account='{session.Account}'，playerId='{session.PlayerId}'，sessionId='{session.SessionId}'");

                // 回包发完再通知业务侧：这样业务侧可以立刻往这条连接推初始数据，
                // 客户端一定是"先收到 login.response，再收到推送"
                server.RaiseClientLoggedIn(peer);
            });
        }

        /// <summary>构造一个登录成功的结果。</summary>
        public static LoginResponse Success(string playerId)
        {
            return new LoginResponse
            {
                Success = true,
                PlayerId = string.IsNullOrEmpty(playerId) ? Guid.NewGuid().ToString("N") : playerId,
                SessionId = Guid.NewGuid().ToString("N")
            };
        }

        /// <summary>构造一个登录失败的结果。</summary>
        public static LoginResponse Failure(string reason, string message)
        {
            return new LoginResponse
            {
                Success = false,
                Reason = reason,
                Message = message
            };
        }
    }

    /// <summary>把 register.request 挂到服务器上。</summary>
    public static class RegisterProtocols
    {
        public static void Register(GameServer server, AccountService accounts)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            server.Router.RegisterOrReplace(ProtocolId.RegisterRequest, (message, peer) =>
            {
                var request = message.Deserialize<RegisterRequest>(peer.Serializer);
                var response = new RegisterResponse();

                var session = PeerSession.Get(peer);

                if (session != null && session.LoggedIn)
                {
                    response.Success = false;
                    response.Reason = RegisterReasons.Rejected;
                    response.Message = "已经登录了，不需要再注册";
                }
                else if (!server.Options.AllowRegister)
                {
                    response.Success = false;
                    response.Reason = RegisterReasons.RegisterClosed;
                    response.Message = "服务器未开放注册";
                }
                else if (accounts == null)
                {
                    response.Success = false;
                    response.Reason = RegisterReasons.Rejected;
                    response.Message = "服务器没有配置账号存储";
                }
                else
                {
                    var result = accounts.Register(
                        request?.Account,
                        request?.Password,
                        string.IsNullOrWhiteSpace(request?.PlayerName) ? null : request.PlayerName);

                    response.Success = result.Success;
                    response.Reason = result.Reason;
                    response.Message = result.Message;
                    response.PlayerId = result.PlayerId;

                    if (result.Success)
                        server.Logger.Info($"[{peer.PeerId}] 注册成功：account='{request?.Account}'，playerId='{result.PlayerId}'");
                    else
                        server.Logger.Warn(
                            $"[{peer.PeerId}] 注册失败：account='{request?.Account}'，原因={result.Reason}（{result.Message}）");
                }

                response.ServerName = server.ServerName;
                response.ProtocolVersion = SystemProtocols.ProtocolVersion;
                response.ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                peer.Send(ProtocolId.RegisterResponse, response);
            });
        }
    }
}
