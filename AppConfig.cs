using System;
using System.IO;
using System.Text.Json;

namespace TsBrowser
{
	public sealed class AppConfig
	{
		public string Server { get; set; } = "";
		public string ServerPassword { get; set; } = "";
		public string Channel { get; set; } = "";
		public string ChannelPassword { get; set; } = "";
		public string Nickname { get; set; } = "Браузер-радио";
		public int SecurityLevel { get; set; } = 8;
		public string PrivateKey { get; set; } = "";
		public ulong KeyOffset { get; set; }
		public string StartUrl { get; set; } = "https://www.youtube.com";
		public bool ListenLocally { get; set; } = true;

		public static string DataDirectory =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TsBrowser");

		public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

		public static AppConfig Load()
		{
			try
			{
				if (File.Exists(ConfigPath))
				{
					var json = File.ReadAllText(ConfigPath);
					var cfg = JsonSerializer.Deserialize<AppConfig>(json);
					if (cfg != null)
						return cfg;
				}
			}
			catch
			{
				// Битый файл настроек не должен мешать запуску окна.
			}

			return new AppConfig();
		}

		public void Save()
		{
			Directory.CreateDirectory(DataDirectory);
			var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(ConfigPath, json);
		}
	}
}
