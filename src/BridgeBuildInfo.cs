using System;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class BridgeBuildInfo
    {
        public static string RevitVersion
        {
            get
            {
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
