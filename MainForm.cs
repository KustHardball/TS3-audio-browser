using System;
using System.Drawing;
using System.Windows.Forms;
using CefSharp.WinForms;

namespace TsBrowser
{
	public sealed class MainForm : Form
	{
		readonly AppConfig config;
		readonly PcmRing ring = new PcmRing();
		readonly LocalMonitor monitor = new LocalMonitor();
		readonly ChromiumWebBrowser browser;
		readonly TextBox urlBox;
		readonly TextBox serverBox;
		readonly TextBox serverPasswordBox;
		readonly TextBox channelBox;
		readonly TextBox channelPasswordBox;
		readonly TextBox nickBox;
		readonly NumericUpDown levelBox;
		readonly CheckBox listenBox;
		readonly Button connectButton;
		readonly Label statusLabel;
		readonly Timer statusTimer;
		TeamSpeakSession session;
		bool connecting;

		public MainForm(AppConfig config)
		{
			this.config = config;
			Text = "Браузер в TeamSpeak";
			Width = 1100;
			Height = 760;
			MinimumSize = new Size(860, 560);
			StartPosition = FormStartPosition.CenterScreen;
			Font = new Font("Segoe UI", 9f);

			var top = new TableLayoutPanel
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				ColumnCount = 8,
				Padding = new Padding(8, 8, 8, 4)
			};
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));

			urlBox = new TextBox { Dock = DockStyle.Fill, Text = config.StartUrl };
			var openButton = new Button { Text = "Открыть", AutoSize = true };
			openButton.Click += (_, __) => Navigate();
			urlBox.KeyDown += (_, e) =>
			{
				if (e.KeyCode == Keys.Enter)
				{
					e.SuppressKeyPress = true;
					Navigate();
				}
			};

			top.Controls.Add(new Label { Text = "Адрес", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
			top.SetColumnSpan(urlBox, 6);
			top.Controls.Add(urlBox, 1, 0);
			top.Controls.Add(openButton, 7, 0);

			serverBox = new TextBox { Dock = DockStyle.Fill, Text = config.Server };
			serverPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ServerPassword, UseSystemPasswordChar = true };
			channelBox = new TextBox { Dock = DockStyle.Fill, Text = config.Channel };
			channelPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ChannelPassword, UseSystemPasswordChar = true };

			top.Controls.Add(new Label { Text = "Сервер", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
			top.Controls.Add(serverBox, 1, 1);
			top.Controls.Add(new Label { Text = "Пароль", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
			top.Controls.Add(serverPasswordBox, 3, 1);
			top.Controls.Add(new Label { Text = "Канал", AutoSize = true, Anchor = AnchorStyles.Left }, 4, 1);
			top.Controls.Add(channelBox, 5, 1);
			top.Controls.Add(new Label { Text = "Пароль канала", AutoSize = true, Anchor = AnchorStyles.Left }, 6, 1);
			top.Controls.Add(channelPasswordBox, 7, 1);

			nickBox = new TextBox { Dock = DockStyle.Fill, Text = config.Nickname };
			levelBox = new NumericUpDown { Minimum = 1, Maximum = 12, Value = Math.Min(12, Math.Max(1, config.SecurityLevel)), Width = 56 };
			connectButton = new Button { Text = "Подключить", AutoSize = true };
			listenBox = new CheckBox { Text = "Слышать у себя", AutoSize = true, Checked = config.ListenLocally, Anchor = AnchorStyles.Left };
			connectButton.Click += async (_, __) => await ToggleConnection();
			listenBox.CheckedChanged += (_, __) =>
			{
				monitor.Enabled = listenBox.Checked;
				config.ListenLocally = listenBox.Checked;
			};
			monitor.Enabled = config.ListenLocally;

			top.Controls.Add(new Label { Text = "Ник", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
			top.Controls.Add(nickBox, 1, 2);
			top.Controls.Add(new Label { Text = "Уровень личности", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);
			top.Controls.Add(levelBox, 3, 2);
			top.Controls.Add(connectButton, 4, 2);
			top.SetColumnSpan(listenBox, 3);
			top.Controls.Add(listenBox, 5, 2);

			statusLabel = new Label
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				Padding = new Padding(8, 0, 8, 8),
				Text = "Страница ещё молчит. Откройте ролик или радио, затем подключитесь к серверу. Канал должен быть с кодеком Opus Music."
			};

			var capture = new BrowserAudioCapture(ring, monitor);
			browser = new ChromiumWebBrowser(string.IsNullOrWhiteSpace(config.StartUrl) ? "about:blank" : config.StartUrl)
			{
				Dock = DockStyle.Fill,
				AudioHandler = capture
			};
			browser.AddressChanged += (_, e) =>
			{
				if (IsDisposed)
					return;
				BeginInvoke(new Action(() => urlBox.Text = e.Address));
			};

			Controls.Add(browser);
			Controls.Add(statusLabel);
			Controls.Add(top);

			statusTimer = new Timer { Interval = 500 };
			statusTimer.Tick += (_, __) => RefreshMeter();
			statusTimer.Start();
		}

		void Navigate()
		{
			var url = urlBox.Text.Trim();
			if (url.Length == 0)
				return;
			if (!url.Contains("://"))
				url = "https://" + url;
			browser.Load(url);
		}

		async System.Threading.Tasks.Task ToggleConnection()
		{
			if (connecting)
				return;

			connecting = true;
			connectButton.Enabled = false;
			try
			{
				if (session != null)
				{
					SetStatus("Отключаюсь…");
					await session.Disconnect();
					session.Dispose();
					session = null;
					connectButton.Text = "Подключить";
					SetStatus("Отключено. Личность сохранена, при следующем входе UID будет тем же.");
					return;
				}

				ReadConfigFromForm();
				config.Save();
				SetStatus("Подключаюсь. Если уровень личности ещё не набран, это может занять несколько секунд…");

				var created = new TeamSpeakSession(ring);
				try
				{
					created.Status += message =>
					{
						if (IsDisposed)
							return;
						BeginInvoke(new Action(() => SetStatus(message)));
					};
					await created.Connect(config);
					session = created;
					connectButton.Text = "Отключить";
					SetStatus("В канале как «" + config.Nickname + "». UID: " + created.Uid
						+ ". Слушайте этого клиента из обычного TeamSpeak. Если звук двоится, снимите «Слышать у себя» и приглушите бота у себя в клиенте.");
				}
				catch
				{
					created.Dispose();
					throw;
				}
			}
			catch (Exception ex)
			{
				SetStatus(ex.Message);
				connectButton.Text = "Подключить";
			}
			finally
			{
				connecting = false;
				connectButton.Enabled = true;
			}
		}

		void ReadConfigFromForm()
		{
			config.Server = serverBox.Text.Trim();
			config.ServerPassword = serverPasswordBox.Text;
			config.Channel = channelBox.Text.Trim();
			config.ChannelPassword = channelPasswordBox.Text;
			config.Nickname = nickBox.Text.Trim();
			config.SecurityLevel = (int)levelBox.Value;
			config.StartUrl = urlBox.Text.Trim();
			config.ListenLocally = listenBox.Checked;
		}

		void RefreshMeter()
		{
			if (statusLabel.Tag is string pinned && (DateTime.UtcNow - lastStatus).TotalSeconds < 8)
				return;

			var link = session == null ? "не в сети" : "в сети, UID " + session.Uid;
			statusLabel.Text = link + " · пакетов звука: " + ring.CapturedPackets + " · буфер " + ring.BufferedMilliseconds + " мс";
		}

		DateTime lastStatus = DateTime.MinValue;

		void SetStatus(string text)
		{
			lastStatus = DateTime.UtcNow;
			statusLabel.Tag = "pinned";
			statusLabel.Text = text;
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			statusTimer.Stop();
			ReadConfigFromForm();
			try { config.Save(); } catch { }
			try { session?.Dispose(); } catch { }
			try { monitor.Dispose(); } catch { }
			base.OnFormClosing(e);
		}
	}
}
