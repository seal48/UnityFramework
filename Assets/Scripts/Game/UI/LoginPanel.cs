using GameFramework.Config;
using GameFramework.Log;
using GameFramework.Net.Client.Unity;
using GameFramework.Net.Protocol;
using GameFramework.ServerSelect;
using GameFramework.UI;
using UnityEngine;

/// <summary>
/// 登录界面。也是 Panel 基类的用法示例：生命周期按需重载。
///
/// **节点不用自己取**：字段（loginButton / accountInput / serverSelectText ...）由
/// <c>LoginPanel.Bindings.g.cs</c> 生成，在 <see cref="Panel.BindNodes"/> 里统一赋值，
/// 所以 OnInit 里直接当字段用就行。改了预制体节点 → 重新生成（Tools/UI/绑定）→
/// 用错的地方编译期就报错。
///
/// 预制体放在 Assets/Prefabs/LoginPanel.prefab，地址由 [UIPanel] 的默认规则自动拼出来。
/// 选服：顶部 ServerSelectButton 显示当前服，点击打开 SelectServerPanel 换服；登录时用当前服的 host/port。
/// </summary>
[UIPanel("LoginPanel", UILayer.Normal)]
public partial class LoginPanel : Panel
{
    protected override void OnInit()
    {
        loginButton.onClick.AddListener(Login);
        registerButton.onClick.AddListener(Register);

        // 选服入口：显示当前服名，点击打开选服弹窗
        if (serverSelectButton != null)
            serverSelectButton.onClick.AddListener(OpenServerSelect);

        // 选服弹窗里换了服 → 刷新这里的显示（面板隐藏期间也收得到，正好）
        Listen<ServerChangedEvent>(OnServerChanged);
    }

    private void OnServerChanged(ServerChangedEvent evt)
    {
        RefreshServerDisplay();
    }

    protected override void OnShow(object userData)
    {
        RefreshServerDisplay();
    }

    protected override void OnClose()
    {
        if (loginButton != null)
            loginButton.onClick.RemoveListener(Login);

        if (registerButton != null)
            registerButton.onClick.RemoveListener(Register);

        if (serverSelectButton != null)
            serverSelectButton.onClick.RemoveListener(OpenServerSelect);
    }

    private void RefreshServerDisplay()
    {
        if (serverSelectText == null) return;

        var select = GameController.Instance != null ? GameController.Instance.ServerSelect : null;
        var current = select != null ? select.Current : null;

        if (current == null)
        {
            serverSelectText.text = "选择服务器";
            return;
        }

        string status = "";
        switch (current.Status)
        {
            case ServerStatus.Normal: status = ""; break;
            case ServerStatus.Maintenance: status = "（维护）"; break;
            case ServerStatus.New: status = "（新区）"; break;
        }

        serverSelectText.text = current.Name + status;
    }

    private void OpenServerSelect()
    {
        if (GameController.Instance != null && GameController.Instance.UI != null)
            GameController.Instance.UI.Open(SelectServerPanel.Name);
    }

    private void Login()
    {
        // 动画用法示例：按节点名直接对组件做动画（这里让按钮弹一下）
        Anim.Pop("LoginButton");

        var gc = GameController.Instance;
        if (gc == null) return;

        // 用当前选的服的 host/port 登录
        if (gc.ServerSelect != null && gc.ServerSelect.Current != null)
        {
            var server = gc.ServerSelect.Current;

            // 维护中的服不给登，提示玩家换服（选服按钮就在上面）
            if (server.Status == ServerStatus.Maintenance)
            {
                GameLog.Warn(LogTag.Panel, $"「{server.Name}」正在维护，不能登录。{server.Announce}");
                return;
            }

            gc.gameClient.SetServer(server.Host, server.Port);
        }

        gc.gameClient.Login(accountInput.text, passwordInput.text);
    }

    private void Register()
    {
        var gc = GameController.Instance;
        if (gc == null) return;

        if (gc.ServerSelect != null && gc.ServerSelect.Current != null
            && gc.ServerSelect.Current.Status == ServerStatus.Maintenance)
        {
            GameLog.Warn(LogTag.Panel, $"「{gc.ServerSelect.Current.Name}」正在维护，不能注册。");
            return;
        }

        RegisterResponse response = new RegisterResponse();
        gc.gameClient.Register(accountInput.text, passwordInput.text, out response);
    }
}
