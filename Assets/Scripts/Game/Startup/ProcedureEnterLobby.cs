using GameFramework.Log;
using GameFramework.Procedure;
using GameFramework.Scenes;

namespace GameFramework.Startup
{
    /// <summary>
    /// 进大厅：关掉登录界面，用场景管理异步切到大厅场景（带进度条），加载完再进入常驻的大厅流程。
    /// </summary>
    public sealed class ProcedureEnterLobby : ProcedureBase
    {
        /// <summary>大厅场景（短名，真实地址 = SceneInitOptions.SceneFolder + 它 + .unity）。</summary>
        public const string LobbySceneName = "Lobby";

        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null || controller.Scene == null)
            {
                GameLog.Error(LogTag.Startup, "场景管理不可用，没法切到大厅场景。");
                return;
            }

            if (controller.UI != null && controller.UI.IsOpen(ProcedureLogin.LoginPanelName))
                controller.UI.Close(ProcedureLogin.LoginPanelName);

            GameLog.Info(LogTag.Startup, "开始加载大厅场景…");

            SceneLoadOptions options = new SceneLoadOptions();
            options.Tip = "正在进入大厅…";

            controller.Scene.LoadAsync(LobbySceneName, options, OnLobbyLoaded);
        }

        private void OnLobbyLoaded(bool success, string error)
        {
            if (!success)
            {
                GameLog.Error(LogTag.Startup, "大厅场景加载失败：" + error);
                return;
            }

            GameLog.Info(LogTag.Startup, "大厅场景就绪。");
            ChangeState<ProcedureLobby>();
        }
    }
}