using System;
using System.Drawing;
using System.Windows.Forms;

namespace TsBrowser
{
	public sealed class SettingsForm : Form
	{
		readonly TextBox serverBox;
		readonly TextBox serverPasswordBox;
		readonly TextBox channelBox;
		readonly TextBox channelPasswordBox;
		readonly TextBox nickBox;
		readonly NumericUpDown levelBox;
		readonly CheckBox listenBox;
		readonly CheckBox proxyBox;
		readonly TextBox proxyHostBox;
		readonly NumericUpDown proxyPortBox;
		readonly CheckBox proxyBypassBox;

		public SettingsForm(AppConfig config)
		{
			Text = "Настройки";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(640, 390);
			Font = new Font("Segoe UI", 9f);

			var root = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 4,
				Padding = new Padding(12),
				AutoSize = true
			};
			root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

			serverBox = new TextBox { Dock = DockStyle.Fill, Text = config.Server, AccessibleName = "Сервер" };
			serverPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ServerPassword, UseSystemPasswordChar = true, AccessibleName = "Пароль сервера" };
			channelBox = new TextBox { Dock = DockStyle.Fill, Text = config.Channel, AccessibleName = "Канал" };
			channelPasswordBox = new TextBox { Dock = DockStyle.Fill, Text = config.ChannelPassword, UseSystemPasswordChar = true, AccessibleName = "Пароль канала" };
			nickBox = new TextBox { Dock = DockStyle.Fill, Text = config.Nickname, AccessibleName = "Ник" };
			levelBox = new NumericUpDown
			{
				Minimum = 1,
				Maximum = 12,
				Value = Math.Min(12, Math.Max(1, config.SecurityLevel)),
				Width = 64,
				AccessibleName = "Уровень личности"
			};
			listenBox = new CheckBox
			{
				Text = "Слышать у себя",
				AutoSize = true,
				Checked = config.ListenLocally,
				AccessibleName = "Слышать у себя"
			};

			AddRow(root, 0, "Сервер", serverBox, "Пароль", serverPasswordBox);
			AddRow(root, 1, "Канал", channelBox, "Пароль канала", channelPasswordBox);
			AddRow(root, 2, "Ник", nickBox, "Уровень личности", levelBox);
			root.SetColumnSpan(listenBox, 3);
			root.Controls.Add(listenBox, 1, 3);

			proxyBox = new CheckBox { Text = "SOCKS5", AutoSize = true, Checked = config.ProxyEnabled, AccessibleName = "SOCKS5" };
			proxyHostBox = new TextBox { Dock = DockStyle.Fill, Text = config.ProxyHost, AccessibleName = "Адрес прокси" };
			proxyPortBox = new NumericUpDown
			{
				Minimum = 1,
				Maximum = 65535,
				Value = Math.Min(65535, Math.Max(1, config.ProxyPort)),
				Width = 80,
				AccessibleName = "Порт прокси"
			};
			proxyBypassBox = new CheckBox
			{
				Text = "Локальные адреса напрямую",
				AutoSize = true,
				Checked = config.ProxyBypassLocal,
				AccessibleName = "Локальные адреса напрямую"
			};

			var proxyTitle = new Label
			{
				Text = "Прокси только для браузера. TeamSpeak ходит напрямую.",
				AutoSize = true,
				Margin = new Padding(0, 14, 0, 6)
			};
			root.SetColumnSpan(proxyTitle, 4);
			root.Controls.Add(proxyTitle, 0, 4);
			root.Controls.Add(proxyBox, 0, 5);
			root.SetColumnSpan(proxyHostBox, 3);
			root.Controls.Add(proxyHostBox, 1, 5);
			root.Controls.Add(new Label { Text = "Порт", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
			root.Controls.Add(proxyPortBox, 1, 6);
			root.SetColumnSpan(proxyBypassBox, 2);
			root.Controls.Add(proxyBypassBox, 2, 6);

			var note = new Label
			{
				Text = "Горячие клавиши саундбара регистрируются в Windows и срабатывают, когда игра на переднем плане. Если игра забирает сочетание, выберите другое.",
				AutoSize = true,
				MaximumSize = new Size(600, 0),
				Margin = new Padding(0, 16, 0, 8)
			};
			root.SetColumnSpan(note, 4);
			root.Controls.Add(note, 0, 7);

			var buttons = new FlowLayoutPanel
			{
				FlowDirection = FlowDirection.RightToLeft,
				Dock = DockStyle.Bottom,
				AutoSize = true,
				Padding = new Padding(12, 0, 12, 12)
			};
			var ok = new Button { Text = "ОК", DialogResult = DialogResult.OK, AutoSize = true };
			var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
			buttons.Controls.Add(ok);
			buttons.Controls.Add(cancel);

			AcceptButton = ok;
			CancelButton = cancel;
			Controls.Add(root);
			Controls.Add(buttons);
		}

		static void AddRow(TableLayoutPanel root, int row, string leftLabel, Control left, string rightLabel, Control right)
		{
			root.Controls.Add(new Label { Text = leftLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 3) }, 0, row);
			root.Controls.Add(left, 1, row);
			root.Controls.Add(new Label { Text = rightLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 8, 3) }, 2, row);
			root.Controls.Add(right, 3, row);
		}

		public void CopyTo(AppConfig config)
		{
			config.Server = serverBox.Text.Trim();
			config.ServerPassword = serverPasswordBox.Text;
			config.Channel = channelBox.Text.Trim();
			config.ChannelPassword = channelPasswordBox.Text;
			config.Nickname = nickBox.Text.Trim();
			config.SecurityLevel = (int)levelBox.Value;
			config.ListenLocally = listenBox.Checked;
			config.ProxyEnabled = proxyBox.Checked;
			config.ProxyHost = proxyHostBox.Text.Trim();
			config.ProxyPort = (int)proxyPortBox.Value;
			config.ProxyBypassLocal = proxyBypassBox.Checked;
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (DialogResult == DialogResult.OK && proxyBox.Checked && proxyHostBox.Text.Trim().Length == 0)
			{
				MessageBox.Show(this, "Укажите адрес SOCKS5 или снимите галочку.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
				e.Cancel = true;
				DialogResult = DialogResult.None;
			}

			base.OnFormClosing(e);
		}
	}

	public sealed class HotkeyDialog : Form
	{
		public bool Ctrl { get; private set; }
		public bool Alt { get; private set; }
		public bool Shift { get; private set; }
		public int KeyCode { get; private set; }
		public bool Cleared { get; private set; }

		public HotkeyDialog(SoundClip clip)
		{
			Text = "Клавиша";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			KeyPreview = true;
			ClientSize = new Size(420, 150);
			Font = new Font("Segoe UI", 9f);

			var label = new Label
			{
				Dock = DockStyle.Fill,
				Text = "Нажмите сочетание для «" + clip.Name + "».\nCtrl, Alt и Shift можно держать вместе с клавишей. Escape — отмена.\nСейчас: " + SoundHotkey.Format(clip)
			};
			var clear = new Button { Text = "Убрать клавишу", AutoSize = true, Dock = DockStyle.Bottom };
			clear.Click += (_, __) =>
			{
				Cleared = true;
				DialogResult = DialogResult.OK;
			};
			Controls.Add(label);
			Controls.Add(clear);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Escape)
			{
				DialogResult = DialogResult.Cancel;
				return;
			}

			if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu
				|| e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
				return;

			Ctrl = e.Control;
			Alt = e.Alt;
			Shift = e.Shift;
			KeyCode = (int)e.KeyCode;
			DialogResult = DialogResult.OK;
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}

	public static class SoundHotkey
	{
		public static string Format(SoundClip clip)
		{
			if (clip == null || clip.KeyCode == 0)
				return "без клавиши";

			var parts = new System.Collections.Generic.List<string>();
			if (clip.Ctrl) parts.Add("Ctrl");
			if (clip.Alt) parts.Add("Alt");
			if (clip.Shift) parts.Add("Shift");
			parts.Add(KeyName((Keys)clip.KeyCode));
			return string.Join("+", parts);
		}

		public static bool Same(SoundClip a, SoundClip b)
		{
			return a.KeyCode != 0 && a.KeyCode == b.KeyCode && a.Ctrl == b.Ctrl && a.Alt == b.Alt && a.Shift == b.Shift;
		}

		static string KeyName(Keys key)
		{
			if (key >= Keys.D0 && key <= Keys.D9)
				return ((int)(key - Keys.D0)).ToString();
			if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
				return "Num" + (int)(key - Keys.NumPad0);
			return key.ToString();
		}
	}
}
