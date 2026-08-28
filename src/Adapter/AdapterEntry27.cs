namespace RevitCommandBridge
{
    public sealed class RevitCommandBridgeApp27 : RevitCommandBridgeApp
    {
        public override Result OnStartup(UIControlledApplication application)
        {
            BridgeBuildInfo.SetApiYear(2027);
            return base.OnStartup(application);
        }

        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
