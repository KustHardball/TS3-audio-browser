using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CefSharp.WinForms;

namespace TsBrowser
{
	public sealed class MainForm : Form
	{
		const int WmHotkey = 0x0312;
		const uint ModAlt = 0x0001;
		const uint ModControl = 0x0002;
		const uint ModShift = 0x0004;
		const uint ModNoRepeat = 0x4000;

		readonly AppConfig config;
		readonly PcmRing browserRing = new PcmRing();
		readonly PcmRing outputRing = new PcmRing();
		readonly LocalMonitor monitor = new LocalMonitor();
		readonly AudioMixer mixer;
		readonly ChromiumWebBrowser browser;
		readonly TextBox urlBox;
		readonly Button connectButton;
		readonly TrackBar browserVolume;
		readonly TrackBar soundVolume;
		readonly Label statusLabel;
		readonly Timer statusTimer;
		readonly FlowLayoutPanel favoritesBar;
		readonly FlowLayoutPanel soundBar;
		readonly ToolTip tips = new ToolTip();
		readonly HttpClient icons = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
		readonly List<int> hotkeyIds = new List<int>();
		string pageTitle = "";
		TeamSpeakSession session;
		bool connecting;

		[DllImport("user32.dll")]
		static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

		[DllImport("user32.dll")]
		static extern bool UnregisterHotKey(IntPtr hWnd, int id);

		public MainForm(AppConfig config)
		{
			this.config = config;
			monitor.Enabled = config.ListenLocally;
			mixer = new AudioMixer(browserRing, outputRing, monitor, config.BrowserVolume, config.SoundVolume);
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

			soundBar = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				WrapContents = true,
				MinimumSize = new Size(200, 52),
				Margin = new Padding(0, 4, 0, 0)
			};
			var addSound = new Button
			{
				Text = "Добавить звук",
				AutoSize = true,
				Height = 48,
				Margin = new Padding(0, 2, 6, 2),
				AccessibleName = "Добавить звук"
			};
			tips.SetToolTip(addSound, "mp3, wav, ogg, flac, m4a, aac, wma. Кнопка играет сразу. Правый клик задаёт клавишу, она срабатывает и из игры.");
			addSound.Click += (_, __) => AddSounds();
			soundBar.Controls.Add(addSound);
			top.SetColumnSpan(soundBar, 8);
			top.Controls.Add(soundBar, 0, 2);
			RebuildSounds();

			browserVolume = new TrackBar
			{
				Minimum = 0,
				Maximum = 100,
				Value = config.BrowserVolume,
				TickFrequency = 25,
				Width = 130,
				Height = 32,
				AutoSize = false,
				Margin = new Padding(0, 0, 12, 0),
				AccessibleName = "Громкость браузера"
			};
			soundVolume = new TrackBar
			{
				Minimum = 0,
				Maximum = 100,
				Value = config.SoundVolume,
				TickFrequency = 25,
				Width = 130,
				Height = 32,
				AutoSize = false,
				Margin = new Padding(0, 0, 12, 0),
				AccessibleName = "Громкость саундбара"
			};
			browserVolume.ValueChanged += (_, __) =>
			{
				config.BrowserVolume = browserVolume.Value;
				mixer.SetVolumes(config.BrowserVolume, config.SoundVolume);
			};
			soundVolume.ValueChanged += (_, __) =>
			{
				config.SoundVolume = soundVolume.Value;
				mixer.SetVolumes(config.BrowserVolume, config.SoundVolume);
			};
			connectButton = new Button { Text = "Подключить", AutoSize = true, Margin = new Padding(8, 4, 6, 3) };
			var settingsButton = new Button { Text = "Настройки", AutoSize = true, Margin = new Padding(0, 4, 3, 3), AccessibleName = "Настройки" };
			connectButton.Click += async (_, __) => await ToggleConnection();
			settingsButton.Click += (_, __) => OpenSettings();

			var controls = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				WrapContents = false,
				Margin = new Padding(0, 4, 0, 0)
			};
			controls.Controls.Add(new Label { Text = "Браузер", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
			controls.Controls.Add(browserVolume);
			controls.Controls.Add(new Label { Text = "Саундбар", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
			controls.Controls.Add(soundVolume);
			controls.Controls.Add(connectButton);
			controls.Controls.Add(settingsButton);
			top.SetColumnSpan(controls, 8);
			top.Controls.Add(controls, 0, 3);

			statusLabel = new Label
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				Padding = new Padding(8, 0, 8, 8),
				Text = "Страница ещё молчит. Откройте ролик или радио, затем подключитесь к серверу. Канал должен быть с кодеком Opus Music."
			};

			var capture = new BrowserAudioCapture(browserRing);
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
			Shown += (_, __) =>
			{
				ApplySavedProxy();
				ReregisterHotkeys();
				PreloadSounds();
			};
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

		void ApplySavedProxy()
		{
			BrowserProxy.Apply(config, message =>
			{
				if (IsDisposed)
					return;
				BeginInvoke(new Action(() => SetStatus(message)));
			});
		}

		void OpenSettings()
		{
			using (var dialog = new SettingsForm(config))
			{
				if (dialog.ShowDialog(this) != DialogResult.OK)
					return;
				dialog.CopyTo(config);
			}

			monitor.Enabled = config.ListenLocally;
			try { config.Save(); } catch { }
			ApplySavedProxy();
			if (session != null)
				SetStatus("Сервер, канал и ник применятся после переподключения.");
		}

		async void AddSounds()
		{
			using (var dialog = new OpenFileDialog
			{
				Title = "Нарезки для саундбара",
				Filter = "Аудио|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.wma|Все файлы|*.*",
				Multiselect = true
			})
			{
				if (dialog.ShowDialog(this) != DialogResult.OK)
					return;

				if (config.Sounds.Count + dialog.FileNames.Length > 32)
				{
					SetStatus("В саундбаре не больше 32 нарезок.");
					return;
				}

				foreach (var path in dialog.FileNames)
				{
					SetStatus("Загружаю " + Path.GetFileName(path) + "…");
					try
					{
						var pcm = await System.Threading.Tasks.Task.Run(() => SoundDecoder.Decode(path));
						mixer.Cache(path, pcm);
						config.Sounds.Add(new SoundClip
						{
							Name = Path.GetFileNameWithoutExtension(path),
							Path = path
						});
					}
					catch (Exception ex)
					{
						SetStatus(ex.Message);
					}
				}
			}

			RebuildSounds();
			ReregisterHotkeys();
			try { config.Save(); } catch { }
		}

		void RebuildSounds()
		{
			for (int i = soundBar.Controls.Count - 1; i >= 1; i--)
			{
				var old = soundBar.Controls[i];
				soundBar.Controls.RemoveAt(i);
				old.Dispose();
			}

			foreach (var clip in config.Sounds)
			{
				var item = clip;
				var button = new Button
				{
					Size = new Size(148, 48),
					Margin = new Padding(0, 2, 6, 2),
					Text = ClipCaption(item),
					TextAlign = ContentAlignment.MiddleCenter,
					AccessibleName = item.Name
				};
				tips.SetToolTip(button, item.Path + "\n" + SoundHotkey.Format(item) + "\nСрабатывает и когда игра на переднем плане.");
				button.Click += (_, __) => PlayClip(item);
				var menu = new ContextMenuStrip();
				menu.Items.Add("Клавиша…", null, (_, __) => AssignHotkey(item));
				menu.Items.Add("Удалить", null, (_, __) => RemoveSound(item));
				button.ContextMenuStrip = menu;
				soundBar.Controls.Add(button);
			}
		}

		static string ClipCaption(SoundClip clip)
		{
			var name = clip.Name ?? "";
			if (name.Length > 22)
				name = name.Substring(0, 21) + "…";
			return name + "\n" + SoundHotkey.Format(clip);
		}

		void PlayClip(SoundClip clip)
		{
			try
			{
				mixer.Play(clip.Path);
			}
			catch (Exception ex)
			{
				SetStatus(ex.Message);
			}
		}

		void HandlePrivateCommand(string text)
		{
			if (!TryParseSoundSlot(text, out int slot))
				return;
			if (IsDisposed || !IsHandleCreated)
				return;

			try
			{
				BeginInvoke(new Action(() => PlayPrivateSlot(slot)));
			}
			catch (InvalidOperationException)
			{
			}
		}

		void PlayPrivateSlot(int slot)
		{
			if (slot < 1 || slot > config.Sounds.Count)
				return;

			var clip = config.Sounds[slot - 1];
			try
			{
				if (!mixer.TryPlay(clip.Path))
					return;
				SetStatus("Личка «" + clip.Name + "».");
			}
			catch (Exception ex)
			{
				SetStatus(ex.Message);
			}
		}

		static bool TryParseSoundSlot(string text, out int slot)
		{
			slot = 0;
			if (string.IsNullOrWhiteSpace(text))
				return false;
			text = text.Trim();
			if (text.Length < 2 || text[0] != '!')
				return false;
			return int.TryParse(text.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out slot)
				&& slot >= 1;
		}

		void AssignHotkey(SoundClip clip)
		{
			using (var dialog = new HotkeyDialog(clip))
			{
				if (dialog.ShowDialog(this) != DialogResult.OK)
					return;

				if (dialog.Cleared)
				{
					clip.KeyCode = 0;
					clip.Ctrl = clip.Alt = clip.Shift = false;
				}
				else
				{
					var key = (Keys)dialog.KeyCode;
					bool functionKey = key >= Keys.F1 && key <= Keys.F24;
					if (!dialog.Ctrl && !dialog.Alt && !dialog.Shift && !functionKey)
					{
						SetStatus("Добавьте Ctrl, Alt или Shift. Клавиша без модификатора перестанет печататься везде, пока программа запущена.");
						return;
					}

					var trial = new SoundClip
					{
						KeyCode = dialog.KeyCode,
						Ctrl = dialog.Ctrl,
						Alt = dialog.Alt,
						Shift = dialog.Shift
					};
					foreach (var other in config.Sounds)
					{
						if (!ReferenceEquals(other, clip) && SoundHotkey.Same(other, trial))
						{
							SetStatus("Это сочетание уже стоит на «" + other.Name + "».");
							return;
						}
					}

					clip.KeyCode = dialog.KeyCode;
					clip.Ctrl = dialog.Ctrl;
					clip.Alt = dialog.Alt;
					clip.Shift = dialog.Shift;
				}
			}

			RebuildSounds();
			ReregisterHotkeys();
			try { config.Save(); } catch { }
		}

		void RemoveSound(SoundClip clip)
		{
			config.Sounds.Remove(clip);
			RebuildSounds();
			ReregisterHotkeys();
			try { config.Save(); } catch { }
		}

		async void PreloadSounds()
		{
			foreach (var clip in config.Sounds.ToArray())
			{
				if (IsDisposed)
					return;
				try
				{
					var pcm = await System.Threading.Tasks.Task.Run(() => SoundDecoder.Decode(clip.Path));
					if (!IsDisposed)
						mixer.Cache(clip.Path, pcm);
				}
				catch
				{
					// Кнопка останется. Ошибка покажется в момент запуска.
				}
			}
		}

		void ReregisterHotkeys()
		{
			if (!IsHandleCreated)
				return;

			foreach (var id in hotkeyIds)
				UnregisterHotKey(Handle, id);
			hotkeyIds.Clear();

			for (int i = 0; i < config.Sounds.Count; i++)
			{
				var clip = config.Sounds[i];
				if (clip.KeyCode == 0)
					continue;

				uint mods = ModNoRepeat;
				if (clip.Ctrl) mods |= ModControl;
				if (clip.Alt) mods |= ModAlt;
				if (clip.Shift) mods |= ModShift;
				int id = 200 + i;
				if (RegisterHotKey(Handle, id, mods, (uint)clip.KeyCode))
					hotkeyIds.Add(id);
				else
					SetStatus("Клавиша занята другим приложением: " + SoundHotkey.Format(clip));
			}
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WmHotkey)
			{
				int index = m.WParam.ToInt32() - 200;
				if (index >= 0 && index < config.Sounds.Count)
					PlayClip(config.Sounds[index]);
			}

			base.WndProc(ref m);
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

				var created = new TeamSpeakSession(outputRing);
				try
				{
					created.Status += message =>
					{
						if (IsDisposed)
							return;
						BeginInvoke(new Action(() => SetStatus(message)));
					};
					created.PrivateCommand += HandlePrivateCommand;
					await created.Connect(config);
					session = created;
					connectButton.Text = "Отключить";
					SetStatus("В канале как «" + config.Nickname + "». UID: " + created.Uid
						+ ". Личное сообщение !1 запускает первую нарезку. Пока саундбар играет, следующая команда пропускается.");
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
			config.StartUrl = urlBox.Text.Trim();
			config.BrowserVolume = browserVolume.Value;
			config.SoundVolume = soundVolume.Value;
		}

		void RefreshMeter()
		{
			if (statusLabel.Tag is string pinned && (DateTime.UtcNow - lastStatus).TotalSeconds < 8)
				return;

			var link = session == null ? "не в сети" : "в сети, UID " + session.Uid;
			statusLabel.Text = link + " · пакетов звука: " + browserRing.CapturedPackets + " · буфер " + outputRing.BufferedMilliseconds + " мс";
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
			if (IsHandleCreated)
			{
				foreach (var id in hotkeyIds)
					UnregisterHotKey(Handle, id);
				hotkeyIds.Clear();
			}

			ReadConfigFromForm();
			try { config.Save(); } catch { }
			try { mixer.Dispose(); } catch { }
			try { session?.Dispose(); } catch { }
			try { monitor.Dispose(); } catch { }
			try { icons.Dispose(); } catch { }
			tips.Dispose();
			base.OnFormClosing(e);
		}
	}
}
