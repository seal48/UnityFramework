using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 启动阶段：进程级准备（网络调度器；以后的环境配置 / 日志系统也放这儿）。
    /// 这里只做「快」的事，耗时的都放到后面的流程里，免得卡住启动。
    /// 帧率 / 画质 / 音量不在这儿写死，交给 ProcedureInitStorage 读本地设置后应用。
    /// </summary>
    public sealed class ProcedureLaunch : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，启动流程无法继续。");
                return;
            }

            // 帧率 / 画质 / 音量统一由 ProcedureInitStorage 读本地设置后应用，这里不再写死

            // 网络框架这里只创建调度器，真正连服务器是在登录时
            if (controller.gameClient != null)
                controller.gameClient.Init();

            ChangeState<ProcedureInitStorage>();
        }
    }
}
