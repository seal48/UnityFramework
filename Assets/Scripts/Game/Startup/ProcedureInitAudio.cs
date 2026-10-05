using GameFramework.Log;
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>音频初始化：资源系统就绪后建 [Audio] 根节点、铺好 BGM / 音效通道。放在 UI 之后，界面一打开就能播声音。</summary>
    public sealed class ProcedureInitAudio : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                GameLog.Error(LogTag.Startup, "找不到 GameController，无法初始化音频。");
                return;
            }

            GameLog.Info(LogTag.Startup, "开始初始化音频…");
            controller.InitAudio(OnAudioReady);
        }

        private void OnAudioReady(bool success, string message)
        {
            if (!success)
            {
                GameLog.Error(LogTag.Startup, "音频初始化失败：" + message);
                return;
            }

            GameLog.Info(LogTag.Startup, "音频就绪：" + message);
            ChangeState<ProcedureInitPlatform>();
        }
    }
}