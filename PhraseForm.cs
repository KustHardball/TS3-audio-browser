using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace TsBrowser
{
	public sealed class PhraseForm : Form
	{
		readonly CheckBox enabledBox;
		readonly ListView list;
		readonly TextBox phraseBox;
		readonly NumericUpDown slotBox;

		public PhraseForm(AppConfig config)
		{
			Text = "Фразы";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			ClientSize = new Size(560, 480);
			Font = new Font("Segoe UI", 9f);
			KeyPreview = true;
			KeyDown += (_, e) =>
			{
				if (e.KeyCode != Keys.Delete || !list.Focused)
					return;
				e.Handled = true;
				e.SuppressKeyPress = true;
				RemoveSelected();
			};

			enabledBox = new CheckBox
			{
				Text = "Включить распознавание фраз",
				AutoSize = true,
				Checked = config.PhraseListen,
				AccessibleName = "Включить распознавание фраз",
				Margin = new Padding(0, 6, 8, 0)
			};
			var help = new Button
			{
				Text = "?",
				Width = 28,
				Height = 26,
				AccessibleName = "Справка по фразам",
				Margin = new Padding(0, 2, 0, 0)
			};
			help.Click += (_, __) => ShowHelp();

			var header = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				WrapContents = false,
				Padding = new Padding(12, 10, 12, 0)
			};
			header.Controls.Add(enabledBox);
			header.Controls.Add(help);

			var note = new Label
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				MaximumSize = new Size(536, 0),
				Padding = new Padding(12, 6, 12, 8),
				Text = "Галочка включает функцию целиком и держит модель на видеокарте. Фраза запускает кнопку саундбара с этим номером, счёт с единицы. Пока нарезка играет, следующая фраза пропускается."
			};

			list = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				HideSelection = false,
				MultiSelect = true,
				AccessibleName = "Список фраз"
			};
			list.Columns.Add("Фраза", 380);
			list.Columns.Add("Позиция", 80);
			if (config.Phrases != null)
			{
				foreach (var phrase in config.Phrases)
					AddRow(phrase.Text, phrase.Slot);
			}

			var removeBar = new FlowLayoutPanel
			{
				Dock = DockStyle.Bottom,
				AutoSize = true,
				Padding = new Padding(12, 4, 12, 0)
			};
			var remove = new Button
			{
				Text = "Удалить выбранную",
				AutoSize = true,
				AccessibleName = "Удалить фразу"
			};
			remove.Click += (_, __) => RemoveSelected();
			removeBar.Controls.Add(remove);

			phraseBox = new TextBox { Width = 280, AccessibleName = "Текст фразы" };
			slotBox = new NumericUpDown
			{
				Minimum = 1,
				Maximum = 32,
				Value = 1,
				Width = 56,
				AccessibleName = "Позиция саундбара"
			};
			var add = new Button { Text = "Добавить", AutoSize = true, AccessibleName = "Добавить фразу" };
			add.Click += (_, __) => AddPhrase();
			phraseBox.KeyDown += (_, e) =>
			{
				if (e.KeyCode == Keys.Enter)
				{
					e.SuppressKeyPress = true;
					AddPhrase();
				}
			};

			var edit = new FlowLayoutPanel
			{
				Dock = DockStyle.Bottom,
				AutoSize = true,
				Padding = new Padding(12, 8, 12, 4),
				WrapContents = false
			};
			edit.Controls.Add(new Label { Text = "Фраза", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
			edit.Controls.Add(phraseBox);
			edit.Controls.Add(new Label { Text = "Позиция", AutoSize = true, Margin = new Padding(8, 8, 6, 0) });
			edit.Controls.Add(slotBox);
			edit.Controls.Add(add);

			var buttons = new FlowLayoutPanel
			{
				Dock = DockStyle.Bottom,
				FlowDirection = FlowDirection.RightToLeft,
				AutoSize = true,
				Padding = new Padding(12, 0, 12, 12)
			};
			var ok = new Button { Text = "ОК", DialogResult = DialogResult.OK, AutoSize = true };
			var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
			buttons.Controls.Add(ok);
			buttons.Controls.Add(cancel);
			AcceptButton = ok;
			CancelButton = cancel;

			Controls.Add(list);
			Controls.Add(removeBar);
			Controls.Add(edit);
			Controls.Add(note);
			Controls.Add(header);
			Controls.Add(buttons);
		}

		void AddPhrase()
		{
			var text = phraseBox.Text.Trim();
			if (PhraseListener.Normalize(text).Length < 2)
				return;
			int slot = (int)slotBox.Value;
			foreach (ListViewItem item in list.Items)
			{
				if (string.Equals(PhraseListener.Normalize(item.Text), PhraseListener.Normalize(text), StringComparison.Ordinal))
				{
					item.SubItems[1].Text = slot.ToString(CultureInfo.InvariantCulture);
					phraseBox.Clear();
					return;
				}
			}

			AddRow(text, slot);
			phraseBox.Clear();
		}

		void AddRow(string text, int slot)
		{
			var item = new ListViewItem(text ?? "");
			item.SubItems.Add(slot.ToString(CultureInfo.InvariantCulture));
			list.Items.Add(item);
		}

		void RemoveSelected()
		{
			if (list.SelectedItems.Count == 0)
				return;
			var selected = new ListViewItem[list.SelectedItems.Count];
			list.SelectedItems.CopyTo(selected, 0);
			foreach (var item in selected)
				list.Items.Remove(item);
		}

		void ShowHelp()
		{
			using (var help = new Form())
			{
				help.Text = "Справка по фразам";
				help.FormBorderStyle = FormBorderStyle.FixedDialog;
				help.StartPosition = FormStartPosition.CenterParent;
				help.MinimizeBox = false;
				help.MaximizeBox = false;
				help.ShowInTaskbar = false;
				help.ClientSize = new Size(520, 420);
				help.Font = Font;

				var text = new TextBox
				{
					Dock = DockStyle.Fill,
					Multiline = true,
					ReadOnly = true,
					ScrollBars = ScrollBars.Vertical,
					BorderStyle = BorderStyle.None,
					Text = HelpText,
					TabStop = false
				};
				var close = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, AutoSize = true };
				var bar = new FlowLayoutPanel
				{
					Dock = DockStyle.Bottom,
					FlowDirection = FlowDirection.RightToLeft,
					AutoSize = true,
					Padding = new Padding(12, 8, 12, 12)
				};
				bar.Controls.Add(close);
				help.AcceptButton = close;
				help.CancelButton = close;
				help.Controls.Add(text);
				help.Controls.Add(bar);
				help.ShowDialog(this);
			}
		}

		public void CopyTo(AppConfig config)
		{
			config.PhraseListen = enabledBox.Checked;
			config.Phrases = new System.Collections.Generic.List<PhraseTrigger>();
			foreach (ListViewItem item in list.Items)
			{
				int slot = 1;
				int.TryParse(item.SubItems[1].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
				if (slot < 1)
					slot = 1;
				config.Phrases.Add(new PhraseTrigger { Text = item.Text, Slot = slot });
			}
		}

		const string HelpText =
			"Рекомендации\r\n" +
			"Пишите фразу так, как её произносят в канале: два-четыре слова. Одно слово и обрывки модель путает чаще.\r\n" +
			"Позиция — номер кнопки саундбара слева направо, счёт с единицы.\r\n" +
			"Проверка начинается примерно через 0,4 с речи, ответ видеокарты ещё около 0,1–0,3 с.\r\n" +
			"Пока нарезка играет, новая фраза пропускается. Когда нарезка кончилась, следующая фраза срабатывает сразу.\r\n" +
			"Нормально, когда в канале говорят четыре-пять человек. Свой голос этого клиента не слушаем.\r\n" +
			"Одно произнесение даёт один запуск. Очереди нет.\r\n" +
			"\r\n" +
			"Железо\r\n" +
			"Нужна видеокарта NVIDIA и свежий драйвер с сайта NVIDIA. Отдельный CUDA Toolkit ставить не нужно.\r\n" +
			"Рядом с программой нужна папка python: внутри Python 3.12, PyTorch со сборкой CUDA и пакет faster-whisper. Откуда их взять, написано в README.\r\n" +
			"Модель при первом включении сама скачивается в папку models рядом с программой, около 1,5 ГБ, нужен интернет. Её можно положить и вручную в models\\large-v3-turbo.\r\n" +
			"Пока галочка включена, модель занимает около 1,5 ГБ памяти видеокарты. В игре с генерацией кадров этот запас должен остаться свободным.\r\n" +
			"Карта не занята непрерывно: каждое окно речи — короткий проход, около 0,1 с.\r\n" +
			"Снятие галочки выгружает модель.";
	}
}
