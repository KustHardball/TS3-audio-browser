using System;
using System.Collections.Generic;
using CefSharp;

namespace TsBrowser
{
	/// <summary>
	/// SOCKS5 только для встроенного браузера. Соединение TeamSpeak сюда не входит.
	/// </summary>
	public static class BrowserProxy
	{
		public const string LocalBypassList = "localhost;127.0.0.1;[::1];<local>;10.0.0.0/8;172.16.0.0/12;192.168.0.0/16";

		public static void Apply(AppConfig config, Action<string> report)
		{
			var preference = BuildPreference(config, out var description, out var validationError);
			if (validationError != null)
			{
				report(validationError);
				return;
			}

			Cef.UIThreadTaskFactory.StartNew(() =>
			{
				string error;
				var ok = Cef.GetGlobalRequestContext().SetPreference("proxy", preference, out error);
				report(ok ? description : "Прокси не применился: " + error);
			});
		}

		public static Dictionary<string, object> BuildPreference(AppConfig config, out string description, out string validationError)
		{
			description = "Прокси выключен. Браузер ходит напрямую.";
			validationError = null;
			if (!config.ProxyEnabled)
			{
				return new Dictionary<string, object>
				{
					["mode"] = "direct"
				};
			}

			var host = (config.ProxyHost ?? "").Trim();
			if (host.Length == 0)
			{
				validationError = "Укажите адрес SOCKS5.";
				return null;
			}

			var port = config.ProxyPort;
			if (port < 1 || port > 65535)
			{
				validationError = "Порт SOCKS5 должен быть от 1 до 65535.";
				return null;
			}

			var preference = new Dictionary<string, object>
			{
				["mode"] = "fixed_servers",
				["server"] = "socks5://" + host + ":" + port.ToString()
			};

			if (config.ProxyBypassLocal)
			{
				preference["bypass_list"] = LocalBypassList;
				description = "SOCKS5 " + host + ":" + port + ". Локальные адреса идут напрямую, TeamSpeak прокси не использует.";
			}
			else
			{
				description = "SOCKS5 " + host + ":" + port + " для всех адресов браузера. TeamSpeak по-прежнему напрямую.";
			}

			return preference;
		}
	}
}
