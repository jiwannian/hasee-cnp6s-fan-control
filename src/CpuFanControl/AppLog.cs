using System.IO;
using System.Text.Json;

namespace CpuFanControl;

internal static class AppLog
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CpuFanControl", "Logs");
    private static readonly object Gate = new();

    public static void Write(string action, object? data = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                string line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, action, data });
                File.AppendAllText(Path.Combine(DirectoryPath, DateTime.Today.ToString("yyyy-MM-dd") + ".jsonl"), line + "\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
