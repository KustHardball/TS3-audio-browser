using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TsBrowser
{
	public sealed class FavoriteLink
	{
		public string Title { get; set; } = "";
		public string Url { get; set; } = "";

		public static List<FavoriteLink> CreateDefaults()
		{
			return new List<FavoriteLink>
			{
				new FavoriteLink { Title = "YouTube", Url = "https://www.youtube.com" },
				new FavoriteLink { Title = "YouTube Music", Url = "https://music.youtube.com" },
				new FavoriteLink { Title = "SoundCloud", Url = "https://soundcloud.com" },
				new FavoriteLink { Title = "Spotify", Url = "https://open.spotify.com" }
			};
		}
	}

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
		public bool ProxyEnabled { get; set; }
		public string ProxyHost { get; set; } = "";
		public int ProxyPort { get; set; } = 1080;
		public bool ProxyBypassLocal { get; set; } = true;
		public List<FavoriteLink> Favorites { get; set; }

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
					{
						if (cfg.Favorites == null)
							cfg.Favorites = FavoriteLink.CreateDefaults();
						if (cfg.ProxyPort < 1 || cfg.ProxyPort > 65535)
							cfg.ProxyPort = 1080;
						return cfg;
					}
				}
			}
			catch
			{
				// Битый файл настроек не должен мешать запуску окна.
			}

			var created = new AppConfig();
			created.Favorites = FavoriteLink.CreateDefaults();
			return created;
		}

		public void Save()
		{
			Directory.CreateDirectory(DataDirectory);
			var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(ConfigPath, json);
		}
	}
}
