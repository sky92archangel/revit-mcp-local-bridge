namespace RevitCommandBridge
{
    /// <summary>
    /// 提供 Revit 版本信息与文件队列根目录路径�?
    /// Provides Revit version information and the file queue root directory path.
    /// </summary>
    internal static class BridgeBuildInfo
    {
        private static int _forcedApiYear;

        /// <summary>
        /// 强制设定 API 年份（由各年�?AdapterEntry �?OnStartup 中调用）�?
        /// 若未调用，则�?RevitAPI 程序集版本自动推导�?
        /// Forces the API year (called by each year's AdapterEntry in OnStartup).
        /// Falls back to automatic derivation from the RevitAPI assembly version when not called.
        /// </summary>
        public static void SetApiYear(int year)
        {
            _forcedApiYear = year;
        }

        /// <summary>
        /// Revit API 版本号字符串（如 "2025"）。优先使用强制设定的年份，否则从程序集版�?Major 推导�?
        /// Revit API version string (e.g. "2025"). Uses the forced year if set, otherwise derives it from the assembly major version.
        /// </summary>
        public static string RevitVersion
        {
            get
            {
                if (_forcedApiYear > 0)
                {
                    return _forcedApiYear.ToString();
                }
                // Revit 的程序集主版本号 + 2000 即为发布年份
                // Revit assembly major version + 2000 equals the release year
                Version apiVersion = typeof(Element).Assembly.GetName().Version;
                return apiVersion == null ? "unknown" : (2000 + apiVersion.Major).ToString();
            }
        }

        /// <summary>
        /// 文件队列根目录路径：%LOCALAPPDATA%\RevitCommandBridge\{RevitVersion}�?
        /// File queue root directory path: %LOCALAPPDATA%\RevitCommandBridge\{RevitVersion}.
        /// </summary>
        public static string QueueRootDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitCommandBridge",
                    RevitVersion);
            }
        }
    }
}
