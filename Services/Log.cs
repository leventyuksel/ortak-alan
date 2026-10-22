namespace OrtakAlan;

static class Log
{
    static readonly object Gate = new();
    public static string FilePath { get; set; } = "";

    public static void Write(Exception ex) => Write(ex.ToString());

    public static void Write(string message)
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Günlük yazılamazsa uygulama çalışmaya devam eder.
        }
    }
}
