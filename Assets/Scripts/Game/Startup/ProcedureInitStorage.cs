using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 本地存储初始化：读设置 / 账号 / 缓存，并把已保存的设置应用到引擎。
    /// 放在最前面 —— 登录要用缓存里的 token，设置也要在进游戏之前生效。
    /// 存储失败只降级（内存模式），不会卡住启动：它是体验问题，不是硬依赖。
    /// </summary>
    public sealed class ProcedureInitStorage : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化本地存储。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化本地存储…");
            controller.InitStorage(OnStorageReady);
        }

        private void OnStorageReady(bool success, string message)
        {
            var controller = GameController.Instance;

            if (!success)
                GameLog.Warn(LogTag.Startup, "本地存储不可用，本次运行不会保存设置和账号信息：" + message);
            else
                GameLog.Info(LogTag.Startup, "本地存储就绪：" + message);

            // 不管成没成都要应用一次：读不到存档就用默认值，帧率 / 音量总得有个值
            if (controller != null && controller.Storage != null && controller.Storage.IsInitialized)
                controller.Storage.ApplySettings();

            ChangeState<ProcedureInitResource>();
        }
    }
}