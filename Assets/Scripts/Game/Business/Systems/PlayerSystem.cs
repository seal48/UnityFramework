using GameFramework.Net.Protocol;

namespace GameFramework.Business
{
    /// <summary>
    /// 玩家系统：负责 player.info.push（等级 / 经验 / 金币 / 属性 / 背包种类数）。
    /// 拿到数据 → 更新 PlayerModel → 发 PlayerInfoChangedEvent 给界面。
    /// </summary>
    public sealed class PlayerSystem : GameSystem
    {
        private readonly PlayerModel model;

        public PlayerSystem(PlayerModel model)
        {
            this.model = model;
        }

        protected override void OnBind()
        {
            Subscribe<PlayerInfoPush>(ProtocolId.PlayerInfoPush, OnPlayerInfoPush);
        }

        private void OnPlayerInfoPush(PlayerInfoPush push)
        {
            if (push == null) return;

            HandledCount++;
            model.ApplyInfo(push);

            // 这里就是"数据处理"的位置：以后要算属性、判红点、比对新旧值，都写在这儿，
            // 界面不需要关心，它只等这一条逻辑事件
            Publish(new PlayerInfoChangedEvent
            {
                PlayerId = model.PlayerId,
                PlayerName = model.PlayerName,
                Level = model.Level,
                Exp = model.Exp,
                Gold = model.Gold,
                BagKinds = model.BagKinds,
                Version = model.Version
            });
        }
    }
}
