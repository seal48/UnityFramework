using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>场景管理初始化：资源系统 + UI 就绪后建好，之后切场景就能带进度条了。</summary>
    public sealed class ProcedureInitScene : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化场景管理。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化场景管理…");
            controller.InitScene(OnSceneReady);
        }

        private void OnSceneReady(bool success, string message)
        {
            if (!success)
            {
                GameLog.Error(LogTag.Startup, "场景管理初始化失败：" + message);
                return;
            }

            GameLog.Info(LogTag.Startup, "场景管理就绪：" + message);
            ChangeState<ProcedureInitAudio>();
        }
    }
}