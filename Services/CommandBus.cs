namespace OrtakAlan;

static class CommandBus
{
    public const string EventName = @"Local\OrtakAlan-Komut";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrtakAlan",
        "komut.txt");

    public static void Send(string peerId, string shareId) => Write(peerId + "\t" + shareId);

    public static bool ShowChat() => Write("sohbet");

    static bool Write(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text);
            using var signal = EventWaitHandle.OpenExisting(EventName);
            signal.Set();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
    }
}
