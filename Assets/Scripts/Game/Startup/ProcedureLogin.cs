using GameFramework.Log;
using GameFramework.Net.Client.Unity;
using GameFramework.Net.Protocol;
using GameFramework.Procedure;
using GameFramework.UI;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 登录阶段：打开登录界面，等玩家登录成功。
    /// 登录失败就留在本阶段（界面自己提示），成功才切到进大厅。
    /// </summary>
    public sealed class ProcedureLogin : ProcedureBase
    {
        public const string LoginPanelName = "LoginPanel";

        private GameClientBehaviour client;

        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null) return;

            client = controller.gameClient;
            if (client != null)
            {
                client.LoggedIn += OnLoggedIn;
                client.LoginFailed += OnLoginFailed;
            }

            if (controller.UI != null)
                controller.UI.Open(LoginPanelName, null, PrefillLoginPanel);

            LogRememberedAccount(controller);

            GameLog.Info(LogTag.Startup, "等待登录…");
        }

        /// <summary>用存档里的上次账号 / 记住的密码预填登录界面，手动登录少打几个字。</summary>
        private void PrefillLoginPanel(Panel panel)
        {
            var controller = GameController.Instance;
            if (controller == null || controller.Storage == null)
                return;

            var login = panel as LoginPanel;
            if (login == null)
                return;

            var account = controller.Storage.Account;

            if (login.accountInput != null && !string.IsNullOrEmpty(account.LastAccount))
                login.accountInput.text = account.LastAccount;

            if (login.passwordInput != null && account.HasStoredPassword())
                login.passwordInput.text = account.Password;
        }

        public override void OnLeave(ProcedureBase to)
        {
            if (client == null) return;

            client.LoggedIn -= OnLoggedIn;
            client.LoginFailed -= OnLoginFailed;
            client = null;
        }

        private void OnLoggedIn(GameClientBehaviour behaviour)
        {
            RememberSession(behaviour);

            GameLog.Info(LogTag.Startup, "登录成功，准备进入大厅。");
            ChangeState<ProcedureEnterLobby>();
        }

        /// <summary>把这次的登录会话写进本地存档：上次账号 / 玩家 id / 服务器。</summary>
        private void RememberSession(GameClientBehaviour behaviour)
        {
            var controller = GameController.Instance;
            if (controller == null || controller.Storage == null || behaviour == null)
                return;

            var storage = controller.Storage;
            var session = behaviour.Session;

            storage.Account.LastAccount = behaviour.LastLoginAccount ?? "";
            storage.Account.PlayerId = session != null ? (session.PlayerId ?? "") : "";
            storage.Account.ServerHost = behaviour.ServerAddress;

            // 记住密码：开关决定存 / 清。存的是混淆不是加密，安全说明见 StorageFramework README
            if (storage.Settings.RememberPassword)
            {
                if (!string.IsNullOrEmpty(behaviour.LastLoginPassword))
                    storage.Account.Password = behaviour.LastLoginPassword;
            }
            else
            {
                storage.Account.ClearRememberedPassword();
            }

            storage.Save(storage.AccountFile);

            GameLog.Info(LogTag.Startup, "会话已写进本地存档：账号='" + storage.Account.LastAccount +
                      "'，玩家='" + storage.Account.PlayerId + "'，服务器='" + storage.Account.ServerHost + "'");
        }

        /// <summary>进登录阶段时把上次账号读出来：存档读回验证，另外以后给登录界面做预填。</summary>
        private void LogRememberedAccount(GameController controller)
        {
            if (controller == null || controller.Storage == null)
                return;

            var account = controller.Storage.Account;
            if (string.IsNullOrEmpty(account.LastAccount))
                return;

            GameLog.Info(LogTag.Startup, "读到上次登录账号：'" + account.LastAccount +
                      "'（玩家 '" + account.PlayerId + "'，服务器 '" + account.ServerHost + "'）");
        }

        private void OnLoginFailed(GameClientBehaviour behaviour, LoginResponse response)
        {
            var reason = response != null ? (response.Message ?? response.Reason) : "没有收到回包";
            GameLog.Warn(LogTag.Startup, "登录失败：" + reason + "，留在登录界面。");
        }
    }
}
