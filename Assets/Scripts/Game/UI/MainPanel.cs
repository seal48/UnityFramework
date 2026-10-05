using GameFramework.Business;
using GameFramework.UI;
using UnityEngine.UI;

/// <summary>
/// 大厅主界面。也是"界面怎么接后端推送"的示例：
/// 它不订阅任何协议名，只 Listen 业务事件（PlayerInfoChangedEvent / BagChangedEvent），
/// 服务端协议怎么改都影响不到这个文件。
///
/// 预制体：Assets/Prefabs/MainPanel.prefab（地址由 [UIPanel] 默认规则自动拼出来）。
/// </summary>
[UIPanel("MainPanel", UILayer.Normal)]
public partial class MainPanel : Panel
{
    private int bagEventCount;

    /// <summary>本次显示之后收到过几条推送（0 = 还没收到，提示语要说"已同步，等新的推送"）。</summary>
    private int eventsSinceShow;

    protected override void OnInit()
    {
        // 节点字段（accountText / levelText / ...）由 MainPanel.Bindings.g.cs 生成并赋值

        // 服务端推来玩家数据 → PlayerSystem 处理完 → 发这个事件
        Listen<PlayerInfoChangedEvent>(OnPlayerInfoChanged);

        // 服务端推来背包变化 → BagSystem 处理完 → 发这个事件
        Listen<BagChangedEvent>(OnBagChanged);

        // 掉线
        Listen<PlayerOfflineEvent>(OnOffline);

        RefreshAll();
    }

    protected override void OnShow(object userData)
    {
        eventsSinceShow = 0;
        RefreshAll();
    }

    private void OnPlayerInfoChanged(PlayerInfoChangedEvent evt)
    {
        eventsSinceShow++;
        Show("收到服务端推送：玩家数据已同步（版本 {0}）", evt.Version);
        RefreshAll();
    }

    private void OnBagChanged(BagChangedEvent evt)
    {
        bagEventCount++;
        eventsSinceShow++;

        var deltaText = evt.Delta > 0 ? "+" + evt.Delta : evt.Delta.ToString();
        Show("收到服务端推送：道具 {0} {1}，现在 {2} 个（第 {3} 次推送）",
            evt.ItemId, deltaText, evt.Count, bagEventCount);

        RefreshAll();
    }

    private void OnOffline(PlayerOfflineEvent evt)
    {
        eventsSinceShow++;
        Show("掉线了：{0}", evt.Reason);
        RefreshAll();
    }

    /// <summary>界面只负责"把数据画出来"，一行逻辑都不写。</summary>
    private void RefreshAll()
    {
        var player = GameController.Instance != null ? GameController.Instance.Business : null;
        var model = player != null ? player.Player : null;

        if (model == null)
        {
            SetText(accountText, "玩家：--");
            SetText(levelText, "等级：--");
            SetText(goldText, "金币：--");
            SetText(bagText, "背包：--");
            SetText(pushText, "推送：--");
            return;
        }

        SetText(accountText, "玩家：" + (model.PlayerName ?? "--") + "（" + (model.PlayerId ?? "--") + "）");
        SetText(levelText, "等级：" + model.Level + "    经验：" + model.Exp);
        SetText(goldText, "金币：" + model.Gold);

        var bag = model.Bag;
        if (bag.Count == 0)
        {
            SetText(bagText, "背包：空（共 " + model.BagKinds + " 种）");
        }
        else
        {
            var builder = new System.Text.StringBuilder("背包：");
            foreach (var pair in bag)
                builder.Append(pair.Key).Append(" ×").Append(pair.Value).Append("  ");
            SetText(bagText, builder.ToString());
        }

        SetText(pushText, "累计收到推送：" + model.PushCount + " 条    数据版本：" + model.Version);

        // 界面上还没收到过推送时，提示语要说清楚当前是"已经同步好了"还是"什么都没有"
        if (eventsSinceShow == 0)
            SetText(tipText, model.Version > 0
                ? "已同步服务端下发的玩家数据，等待新的推送…（服务端改数据会自动推）"
                : "等待服务端推送…");
    }

    private void Show(string format, params object[] args)
    {
        SetText(tipText, string.Format(format, args));
    }

    private static void SetText(Text text, string value)
    {
        if (text != null) text.text = value;
    }
}
