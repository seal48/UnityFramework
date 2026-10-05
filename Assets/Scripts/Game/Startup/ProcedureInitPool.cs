using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>对象池初始化：资源系统就绪后创建池根节点，并按配置预热。放在 UI 之前，界面一打开就能用。</summary>
    public sealed class ProcedureInitPool : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化对象池。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化对象池…");
            controller.InitPool(OnPoolReady);
        }

        private void OnPoolReady(bool success, string message)
        {
            if (!success)
            {
                GameLog.Error(LogTag.Startup, "对象池初始化失败：" + message);
                return;
            }

            GameLog.Info(LogTag.Startup, "对象池就绪：" + message);
            ChangeState<ProcedureInitUI>();
        }
    }
}