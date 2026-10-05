using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 大厅：游戏正式内容常驻阶段。以后打开大厅主界面、开始接收业务消息都在这里。
    /// </summary>
    public sealed class ProcedureLobby : ProcedureBase
    {
        /// <summary>大厅主界面名（[UIPanel] 里注册的名字）。</summary>
        public const string MainPanelName = "MainPanel";

        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller != null && controller.UI != null)
                controller.UI.Open(MainPanelName);

            GameLog.Info(LogTag.Startup, "已进入大厅，等待服务端推送玩家数据…");
        }
    }
}
