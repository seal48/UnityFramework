using System;

namespace GameFramework.Net.Protocol
{
    /// <summary>包头里的长度字段非法（越界/负数）时抛出，调用方应当断开连接。</summary>
    public sealed class FrameFormatException : Exception
    {
        public FrameFormatException(string message) : base(message) { }
    }
}
