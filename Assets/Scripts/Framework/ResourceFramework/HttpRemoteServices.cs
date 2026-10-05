using YooAsset;

namespace GameFramework.Resource
{
    /// <summary>
    /// 远端资源地址拼接。YooAsset 会用
    /// "{HostServerURL}/{PackageName}/{PackageVersion}/{FileName}" 的形式来问我们要完整 URL，
    /// 所以这里只需要负责把根地址和文件名拼起来。
    /// </summary>
    public sealed class HttpRemoteServices : IRemoteServices
    {
        private readonly string _mainRoot;
        private readonly string _fallbackRoot;

        public HttpRemoteServices(string mainRoot, string fallbackRoot)
        {
            _mainRoot = Normalize(mainRoot);
            _fallbackRoot = Normalize(string.IsNullOrEmpty(fallbackRoot) ? mainRoot : fallbackRoot);
        }

        public string GetRemoteMainURL(string fileName)
        {
            return _mainRoot + "/" + fileName;
        }

        public string GetRemoteFallbackURL(string fileName)
        {
            return _fallbackRoot + "/" + fileName;
        }

        private static string Normalize(string root)
        {
            if (string.IsNullOrEmpty(root))
                return string.Empty;
            return root.TrimEnd('/');
        }
    }
}
