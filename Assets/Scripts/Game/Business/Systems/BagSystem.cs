using GameFramework.Net.Protocol;

namespace GameFramework.Business
{
    /// <summary>
    /// 背包系统：只负责 player.bag.changed（背包增量）。
    /// 和 PlayerSystem 分开写，是为了说明"一条推送对应一个系统"这个规矩：
    /// 服务端以后把背包拆成独立协议、独立背包服务，客户端这边只动这个文件。
    /// </summary>
    public sealed class BagSystem : GameSystem
    {
        private readonly PlayerModel model;

        public BagSystem(PlayerModel model)
        {
            this.model = model;
        }

        protected override void OnBind()
        {
            Subscribe<BagChangedPush>(ProtocolId.BagChangedPush, OnBagChangedPush);
        }

        private void OnBagChangedPush(BagChangedPush push)
        {
            if (push == null) return;

            HandledCount++;
            model.ApplyBag(push);

            Publish(new BagChangedEvent
            {
                ItemId = push.ItemId,
                Count = push.Count,
                Delta = push.Delta,
                Added = push.Added,
                BagKinds = push.BagKinds,
                Version = push.Version
            });
        }
    }
}
