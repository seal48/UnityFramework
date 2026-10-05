namespace GameFramework.Event
{
    /// <summary>
    /// 事件标记接口。能被 EventBus 派发的类型都要实现它，这样搜一下就能知道工程里有哪些事件。
    ///
    /// 约定：
    /// - 优先用 struct 定义事件。派发时不产生 GC，而且事件天生是「快照」，订阅方改不到原件；
    /// - 需要继承 / 多态、或者字段很大的事件才用 class；
    /// - 命名一律 XxxEvent，放业务自己的目录下（框架不关心事件长什么样）。
    ///
    /// 例子：
    ///   public struct PlayerLevelUpEvent : IGameEvent
    ///   {
    ///       public int PlayerId;
    ///       public int Level;
    ///   }
    /// </summary>
    public interface IGameEvent
    {
    }
}
