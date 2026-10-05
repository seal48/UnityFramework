namespace GameFramework.Log
{
    /// <summary>
    /// 日志级别。数值越大越啰嗦，过滤规则是「级别数值 &lt;= 阈值」就输出，
    /// 所以阈值设成 Info 会放行 Info / Warn / Error / Fatal，只挡掉 Debug。
    /// </summary>
    public enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warn = 3,
        Info = 4,
        Debug = 5,
    }
}