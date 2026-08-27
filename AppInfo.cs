static class AppInfo
{
    public const string Version = "0.6.0";
    public const int BridgePort = 17843;

    public static string TempFolder => Path.Combine(Path.GetTempPath(), "HuddleAudioCapture");

    public static string BridgeUrl => $"http://127.0.0.1:{BridgePort}";

    public static string BridgeTokenFilePath => Path.Combine(TempFolder, "bridge-token.txt");
}
