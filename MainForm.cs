using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
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
		readonly FlowLayoutPanel favoritesBar;
		readonly CheckBox proxyBox;
		readonly TextBox proxyHostBox;
		readonly NumericUpDown proxyPortBox;
		readonly CheckBox proxyBypassBox;
		readonly ToolTip tips = new ToolTip();
		readonly HttpClient icons = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
		string pageTitle = "";
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

			favoritesBar = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				WrapContents = false,
				Padding = new Padding(0),
				Margin = new Padding(0)
			};
			var addFavorite = new Button { Text = "В избранное", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
			tips.SetToolTip(addFavorite, "Сохранить текущую страницу кнопкой быстрого доступа");
			addFavorite.Click += (_, __) => AddCurrentFavorite();
			favoritesBar.Controls.Add(addFavorite);
			top.SetColumnSpan(favoritesBar, 8);
			top.Controls.Add(favoritesBar, 0, 1);
			RebuildFavorites();

			serverBox = new TextBox { Dock = DockStyle.Fill, Text = config.Server };
			serverPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ServerPassword, UseSystemPasswordChar = true };
			channelBox = new TextBox { Dock = DockStyle.Fill, Text = config.Channel };
			channelPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ChannelPassword, UseSystemPasswordChar = true };

			top.Controls.Add(new Label { Text = "Сервер", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
			top.Controls.Add(serverBox, 1, 2);
			top.Controls.Add(new Label { Text = "Пароль", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);
			top.Controls.Add(serverPasswordBox, 3, 2);
			top.Controls.Add(new Label { Text = "Канал", AutoSize = true, Anchor = AnchorStyles.Left }, 4, 2);
			top.Controls.Add(channelBox, 5, 2);
			top.Controls.Add(new Label { Text = "Пароль канала", AutoSize = true, Anchor = AnchorStyles.Left }, 6, 2);
			top.Controls.Add(channelPasswordBox, 7, 2);

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

			top.Controls.Add(new Label { Text = "Ник", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
			top.Controls.Add(nickBox, 1, 3);
			top.Controls.Add(new Label { Text = "Уровень личности", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 3);
			top.Controls.Add(levelBox, 3, 3);
			top.Controls.Add(connectButton, 4, 3);
			top.SetColumnSpan(listenBox, 3);
			top.Controls.Add(listenBox, 5, 3);

			proxyBox = new CheckBox { Text = "SOCKS5", AutoSize = true, Checked = config.ProxyEnabled, Margin = new Padding(0, 8, 6, 3) };
			proxyHostBox = new TextBox { Text = config.ProxyHost, Width = 180, Margin = new Padding(0, 6, 6, 3) };
			proxyPortBox = new NumericUpDown
			{
				Minimum = 1,
				Maximum = 65535,
				Value = Math.Min(65535, Math.Max(1, config.ProxyPort)),
				Width = 72,
				Margin = new Padding(0, 6, 8, 3)
			};
			proxyBypassBox = new CheckBox
			{
				Text = "Локальные адреса напрямую",
				AutoSize = true,
				Checked = config.ProxyBypassLocal,
				Margin = new Padding(0, 8, 3, 3)
			};
			tips.SetToolTip(proxyHostBox, "Адрес SOCKS5, как в Firefox. Только для страниц браузера.");
			tips.SetToolTip(proxyBypassBox, "localhost, 127.0.0.1 и адреса локальной сети открываются напрямую. TeamSpeak этот прокси не использует.");
			proxyBox.CheckedChanged += (_, __) => ApplyProxyFromForm();
			proxyBypassBox.CheckedChanged += (_, __) => { if (proxyBox.Checked) ApplyProxyFromForm(); };
			proxyHostBox.Leave += (_, __) => { if (proxyBox.Checked) ApplyProxyFromForm(); };
			proxyPortBox.ValueChanged += (_, __) => { if (proxyBox.Checked) ApplyProxyFromForm(); };

			var proxyRow = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				WrapContents = false,
				Margin = new Padding(0)
			};
			proxyRow.Controls.Add(proxyBox);
			proxyRow.Controls.Add(proxyHostBox);
			proxyRow.Controls.Add(new Label { Text = "порт", AutoSize = true, Margin = new Padding(0, 8, 4, 3) });
			proxyRow.Controls.Add(proxyPortBox);
			proxyRow.Controls.Add(proxyBypassBox);
			top.SetColumnSpan(proxyRow, 8);
			top.Controls.Add(proxyRow, 0, 4);

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
			browser.TitleChanged += (_, e) => pageTitle = e.Title ?? "";

			Controls.Add(browser);
			Controls.Add(statusLabel);
			Controls.Add(top);

			statusTimer = new Timer { Interval = 500 };
			statusTimer.Tick += (_, __) => RefreshMeter();
			statusTimer.Start();
			Shown += (_, __) => ApplyProxyFromForm();
		}

		void RebuildFavorites()
		{
			for (int i = favoritesBar.Controls.Count - 1; i >= 1; i--)
			{
				var old = favoritesBar.Controls[i];
				favoritesBar.Controls.RemoveAt(i);
				old.Dispose();
			}

			foreach (var site in config.Favorites)
			{
				var link = site;
				var button = new Button
				{
					Size = new Size(44, 44),
					Margin = new Padding(4, 2, 0, 2),
					FlatStyle = FlatStyle.Flat,
					Image = LetterIcon(link.Title, ColorFor(link.Url)),
					Text = "",
					AccessibleName = link.Title,
					Tag = link
				};
				button.FlatAppearance.BorderSize = 0;
				tips.SetToolTip(button, link.Title + "\n" + link.Url);
				button.Click += (_, __) => OpenFavorite(link);
				var menu = new ContextMenuStrip();
				menu.Items.Add("Удалить", null, (_, __) => RemoveFavorite(link));
				button.ContextMenuStrip = menu;
				favoritesBar.Controls.Add(button);
				LoadFavicon(button, link.Url);
			}
		}

		void OpenFavorite(FavoriteLink link)
		{
			if (string.IsNullOrWhiteSpace(link.Url))
				return;
			urlBox.Text = link.Url;
			browser.Load(link.Url);
		}

		void AddCurrentFavorite()
		{
			var url = urlBox.Text.Trim();
			if (url.Length == 0 || url == "about:blank")
			{
				SetStatus("Сначала откройте страницу, потом добавьте её в избранное.");
				return;
			}
			if (!url.Contains("://"))
				url = "https://" + url;
			if (config.Favorites.Count >= 16)
			{
				SetStatus("В избранном уже 16 сайтов. Удалите лишний правой кнопкой по иконке.");
				return;
			}

			var title = string.IsNullOrWhiteSpace(pageTitle) ? url : pageTitle.Trim();
			config.Favorites.Add(new FavoriteLink { Title = title, Url = url });
			config.Save();
			RebuildFavorites();
		}

		void RemoveFavorite(FavoriteLink link)
		{
			config.Favorites.Remove(link);
			config.Save();
			RebuildFavorites();
		}

		async void LoadFavicon(Button button, string pageUrl)
		{
			try
			{
				var host = new Uri(pageUrl).Host;
				var candidates = new[]
				{
					"https://" + host + "/favicon.ico",
					"https://www.google.com/s2/favicons?sz=64&domain=" + host
				};
				foreach (var candidate in candidates)
				{
					try
					{
						var bytes = await icons.GetByteArrayAsync(candidate);
						using (var stream = new System.IO.MemoryStream(bytes))
						using (var image = Image.FromStream(stream))
						{
							var copy = new Bitmap(image, new Size(28, 28));
							if (IsDisposed || button.IsDisposed)
							{
								copy.Dispose();
								return;
							}
							BeginInvoke(new Action(() =>
							{
								if (button.IsDisposed)
								{
									copy.Dispose();
									return;
								}
								var previous = button.Image;
								button.Image = copy;
								previous?.Dispose();
							}));
							return;
						}
					}
					catch
					{
						// Следующий адрес иконки.
					}
				}
			}
			catch
			{
				// Остаётся буквенная иконка.
			}
		}

		static Bitmap LetterIcon(string title, Color back)
		{
			var bmp = new Bitmap(32, 32);
			using (var g = Graphics.FromImage(bmp))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.Clear(Color.Transparent);
				using (var brush = new SolidBrush(back))
					g.FillEllipse(brush, 0, 0, 31, 31);
				var text = Monogram(title);
				using (var font = new Font("Segoe UI", text.Length > 1 ? 8f : 11f, FontStyle.Bold, GraphicsUnit.Point))
				using (var fore = new SolidBrush(Color.White))
				{
					var size = g.MeasureString(text, font);
					g.DrawString(text, font, fore, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
				}
			}
			return bmp;
		}

		static string Monogram(string title)
		{
			if (string.IsNullOrWhiteSpace(title))
				return "?";
			var parts = title.Split(new[] { ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2)
				return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0]));
			return title.Substring(0, Math.Min(2, title.Length)).ToUpperInvariant();
		}

		static Color ColorFor(string url)
		{
			var host = url ?? "";
			if (host.IndexOf("music.youtube", StringComparison.OrdinalIgnoreCase) >= 0)
				return Color.FromArgb(180, 0, 0);
			if (host.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
				return Color.FromArgb(220, 30, 30);
			if (host.IndexOf("soundcloud", StringComparison.OrdinalIgnoreCase) >= 0)
				return Color.FromArgb(255, 85, 0);
			if (host.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0)
				return Color.FromArgb(29, 185, 84);
			var hash = 0;
			foreach (var ch in host)
				hash = hash * 31 + ch;
			return Color.FromArgb(40 + Math.Abs(hash % 160), 70 + Math.Abs(hash / 3 % 120), 140);
		}

		void ApplyProxyFromForm()
		{
			config.ProxyEnabled = proxyBox.Checked;
			config.ProxyHost = proxyHostBox.Text.Trim();
			config.ProxyPort = (int)proxyPortBox.Value;
			config.ProxyBypassLocal = proxyBypassBox.Checked;
			if (config.ProxyEnabled && config.ProxyHost.Length == 0)
			{
				SetStatus("Укажите адрес SOCKS5. Пока прокси не включён, локальный трафик и так идёт напрямую.");
				return;
			}

			BrowserProxy.Apply(config, message =>
			{
				if (IsDisposed)
					return;
				BeginInvoke(new Action(() => SetStatus(message)));
			});
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
			config.ProxyEnabled = proxyBox.Checked;
			config.ProxyHost = proxyHostBox.Text.Trim();
			config.ProxyPort = (int)proxyPortBox.Value;
			config.ProxyBypassLocal = proxyBypassBox.Checked;
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
			try { icons.Dispose(); } catch { }
			tips.Dispose();
			base.OnFormClosing(e);
		}
	}
}
