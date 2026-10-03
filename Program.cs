using System;
using System.IO;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace TsBrowser
{
	internal static class Program
	{
		[STAThread]
		static int Main()
		{
			ApplicationConfiguration.Initialize();

			var cacheRoot = AppConfig.DataDirectory;
			Directory.CreateDirectory(cacheRoot);

			var settings = new CefSettings
			{
				CachePath = Path.Combine(cacheRoot, "cef-cache"),
				RootCachePath = cacheRoot,
				LogSeverity = LogSeverity.Warning,
				LogFile = Path.Combine(cacheRoot, "cef.log")
			};
			settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

			if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
			{
				MessageBox.Show(
					"Не удалось запустить встроенный браузер. Код: " + Cef.GetExitCode()
					+ "\nЕсли окно сразу закрывается, установите Visual C++ Redistributable x64.\nЛог: " + settings.LogFile,
					"Браузер в TeamSpeak",
					MessageBoxButtons.OK,
					MessageBoxIcon.Error);
				return 1;
			}

			Application.Run(new MainForm(AppConfig.Load()));
			Cef.Shutdown();
			return 0;
		}
	}
}
