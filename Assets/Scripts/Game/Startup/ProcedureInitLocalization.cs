using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 本地化初始化：登记语言表、读语言设置、加载字体。
    /// 依赖配置表（LocZh / LocEn / LocFont），所以排在 InitConfig 之后。
    /// </summary>
    public sealed class ProcedureInitLocalization : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化本地化。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化本地化…");
            controller.InitLocalization((success, message) =>
            {
                if (!success)
                {
                    GameLog.Error(LogTag.Startup, "本地化初始化失败：" + message);
                    return;
                }

                GameLog.Info(LogTag.Startup, "本地化就绪：" + message);
                ChangeState<ProcedureInitPool>();
            });
        }
    }
}
