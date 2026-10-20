using System;
using System.IO;

namespace PCL_CE_Patcher.Core
{
    public static class ConfigService
    {
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCL CE Patcher");

        private static readonly string LogPath = Path.Combine(AppDataPath, "latest.log");

        static ConfigService()
        {
            if (!Directory.Exists(AppDataPath)) Directory.CreateDirectory(AppDataPath);
            try { File.WriteAllText(LogPath, $"[System] Patcher Started at {DateTime.Now}\n"); } catch { }
        }

        public static void Log(string message, string level = "INFO")
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(logLine);
            try { File.AppendAllText(LogPath, logLine + Environment.NewLine); } catch { }
        }
    }
}
