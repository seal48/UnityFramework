using GameFramework.Config;
using GameFramework.UI;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 选服弹窗：列出区服表所有服（服名 + 状态），点一个选中并关闭。
/// 打开方式：GameController.Instance.UI.Open(SelectServerPanel.Name)。
/// 选中后 LoginPanel 会通过「回到登录面板刷新」感知（OnShow 里重读 Current）。
///
/// 预制体：Assets/Prefabs/SelectServerPanel.prefab
///   节点约定：Title（标题）、CloseButton（关闭）、Content（列表容器，Item 克隆自它下面的 ServerItem）
/// </summary>
[UIPanel(Name, UILayer.Popup)]
public partial class SelectServerPanel : Panel
{
    /// <summary>面板注册名。</summary>
    public const string Name = "SelectServerPanel";

    /// <summary>列表项之间的垂直间距（像素）。</summary>
    private const float ItemSpacing = 8f;

    private Transform content;
    private GameObject itemTemplate;

    protected override void OnInit()
    {
        // title / closeButton 由 SelectServerPanel.Bindings.g.cs 生成并赋值；
        // content / itemTemplate 用 Get<Transform> 取（节点上没有可绑定的 UI 组件）
        content = Get<Transform>("Content");
        itemTemplate = FindItemTemplate();

        if (closeButton != null)
            closeButton.onClick.AddListener(Close);

        BuildList();
    }

    protected override void OnShow(object userData)
    {
        BuildList();
    }

    protected override void OnClose()
    {
        if (closeButton != null)
            closeButton.onClick.RemoveListener(Close);
    }

    /// <summary>模板节点：Content 下第一个带 ServerItem 名字/组件的子节点，克隆它生成列表项。</summary>
    private GameObject FindItemTemplate()
    {
        if (content == null) return null;
        foreach (Transform child in content)
        {
            if (child.name.IndexOf("ServerItem", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return child.gameObject;
        }
        return content.childCount > 0 ? content.GetChild(0).gameObject : null;
    }

    private void BuildList()
    {
        if (content == null || itemTemplate == null)
        {
            Debug.LogWarning("[SelectServerPanel] 缺少 Content / ServerItem 模板节点，无法构建列表");
            return;
        }

        var select = GameController.Instance != null ? GameController.Instance.ServerSelect : null;
        var servers = select != null ? select.All : null;
        if (servers == null)
        {
            Debug.LogWarning("[SelectServerPanel] 区服表为空");
            return;
        }

        // 清掉旧的列表项（保留模板）
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            if (content.GetChild(i).gameObject != itemTemplate)
                Destroy(content.GetChild(i).gameObject);
        }

        // 用模板克隆一个激活的项（模板本身保持隐藏，或者我们把模板也当第一项——这里隐藏模板）
        itemTemplate.SetActive(false);

        int index = 0;
        foreach (var server in servers)
        {
            var item = Instantiate(itemTemplate, content);
            item.SetActive(true);
            item.name = "ServerItem_" + server.Id;

            // 排版：模板锚点在顶部（anchorMin/Max.y=1, pivot.y=1），从上往下依次排开。
            // 不设位置的话所有项都会叠在 (0,0) —— 视觉上只看得到最后一个。
            var rect = item.GetComponent<RectTransform>();
            if (rect != null)
            {
                float height = rect.sizeDelta.y > 0f ? rect.sizeDelta.y : 56f;
                rect.anchoredPosition = new Vector2(0f, -index * (height + ItemSpacing));
            }

            var nameText = item.transform.Find("Name") != null ? item.transform.Find("Name").GetComponent<Text>() : null;
            var statusText = item.transform.Find("Status") != null ? item.transform.Find("Status").GetComponent<Text>() : null;

            if (nameText != null)
                nameText.text = server.Name + (server.IsRecommended ? "  <color=#FFD700>推荐</color>" : "");

            if (statusText != null)
                statusText.text = StatusText(server);

            int capturedId = server.Id;
            var button = item.GetComponent<Button>();
            if (button == null) button = item.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => OnServerClicked(capturedId));

            index++;
        }
    }

    private void OnServerClicked(int id)
    {
        var select = GameController.Instance != null ? GameController.Instance.ServerSelect : null;
        if (select == null) return;

        if (select.Select(id))
        {
            // 选完关掉弹窗；LoginPanel 的 OnShow 会重读当前服显示
            Close();
        }
    }

    private static string StatusText(ServerListConfig server)
    {
        switch (server.Status)
        {
            case ServerStatus.Normal: return "<color=#4CAF50>正常</color>";
            case ServerStatus.Maintenance: return "<color=#F44336>维护</color>";
            case ServerStatus.New: return "<color=#FF9800>新区</color>";
            default: return server.Status.ToString();
        }
    }
}
