using GameFramework.UI;
using UnityEngine;

/// <summary>
/// 调试用入口：Play 模式下自动打开一个面板，并按快捷键开关它，方便不开业务代码就能看到 UI 效果。
/// 真机上没有键盘，靠 OpenOnStart 来显示界面。正式版把这个脚本和场景里挂它的物体一起删掉即可。
/// </summary>
public class UIDebugLauncher : MonoBehaviour
{
    [Tooltip("要打开的面板名，对应 [UIPanel] 里登记的名字")]
    public string PanelName = "LoginPanel";

    [Tooltip("启动后自动打开这个面板")]
    public bool OpenOnStart = true;

    [Tooltip("开关面板的快捷键（仅键盘平台有效）")]
    public KeyCode ToggleKey = KeyCode.F1;

    private bool opened;

    private void Update()
    {
        UIManager ui = UIManager.Current;
        if (ui == null || !ui.IsInitialized || string.IsNullOrEmpty(PanelName))
            return;

        // UI 初始化是异步的，这里每帧试一次，就绪后才真正打开
        if (OpenOnStart && !opened)
        {
            opened = true;
            if (!ui.IsOpen(PanelName))
                ui.Open(PanelName);
            return;
        }

        if (Input.GetKeyDown(ToggleKey))
        {
            if (ui.IsOpen(PanelName))
                ui.Close(PanelName);
            else
                ui.Open(PanelName);
        }
    }
}
