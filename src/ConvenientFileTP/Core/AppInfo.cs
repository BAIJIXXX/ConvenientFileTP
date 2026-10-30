namespace ConvenientFileTP.Core
{
    /// <summary>
    /// 应用身份与版本信息。当前仍是测试版，版本号采用 0.x.y-beta 形式，
    /// 避免把尚未稳定的版本标成 1.x。
    /// </summary>
    public static class AppInfo
    {
        public const string Name = "ConvenientFileTP";
        public const string Version = "0.1.0-beta";
        public const string DisplayVersion = "v" + Version;
        public const string Stage = "Beta";
    }
}
