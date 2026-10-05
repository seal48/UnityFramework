using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 平台适配层初始化：安全区 / 返回键 / 权限 / 断线重连。
    /// 依赖 UI（安全区要动 Canvas）、存储（重连凭据）、网络（重连），所以排在音频之后、登录之前。
    /// </summary>
    public sealed class ProcedureInitPlatform : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化平台适配层。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化平台适配层…");
            controller.InitPlatform((success, message) =>
            {
                if (!success)
                {
                    GameLog.Error(LogTag.Startup, "平台适配层初始化失败：" + message);
                    return;
                }

                GameLog.Info(LogTag.Startup, "平台适配层就绪：" + message);
                ChangeState<ProcedureLogin>();
            });
        }
    }
}
