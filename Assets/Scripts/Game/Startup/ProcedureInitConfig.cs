using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>配置表初始化：资源系统就绪后，把 Assets/ConfigData 下的表全部读进来，然后去初始化本地化。</summary>
    public sealed class ProcedureInitConfig : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法加载配置表。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始加载配置表…");
            controller.InitConfig(OnConfigReady);
        }

        private void OnConfigReady(bool success, string message)
        {
            if (!success)
            {
                GameLog.Error(LogTag.Startup, "配置表加载失败：" + message);
                return;
            }

            GameLog.Info(LogTag.Startup, "配置表就绪：" + message);
            ChangeState<ProcedureInitLocalization>();
        }
    }
}