namespace LeadjoyBlade.Core.Devices;

/// <summary>把关键连接过程写入程序目录 logs\*.log，便于离线排查。</summary>
public static class DiagnosticsLog
{
    private static readonly object Sync = new();

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Write(string fileName, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Sync)
            {
                File.AppendAllText(Path.Combine(LogDirectory, fileName),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
