using System;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class BridgeBuildInfo
    {
        private static int _forcedApiYear;

        /// <summary>
        /// 强制设定 API 年份（由各年份 AdapterEntry 在 OnStartup 中调用）。
        /// 若未调用，则从 RevitAPI 程序集版本自动推导。
        /// </summary>
        public static void SetApiYear(int year)
        {
            _forcedApiYear = year;
        }

        public static string RevitVersion
        {
            get
            {
                if (_forcedApiYear > 0)
                {
                    return _forcedApiYear.ToString();
                }
                Version apiVersion = typeof(Element).Assembly.GetName().Version;
                return apiVersion == null ? "unknown" : (2000 + apiVersion.Major).ToString();
            }
        }

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
