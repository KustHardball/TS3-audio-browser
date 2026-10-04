using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using TSLib.Audio;

namespace TsBrowser
{
	/// <summary>
	/// Декодированный голос канала режется на короткие куски и отдаётся faster-whisper на видеокарте.
	/// Одна непрерывная фраза даёт один запуск: повторные окна той же фразы не занимают следующую.
	/// Пока один кусок считается, более новый для того же человека вытесняет старый в очереди.
	/// </summary>
	public sealed class PhraseListener : IDisposable
	{
		const int Rate = 16000;
		const int RmsGate = 450;
		const int FirstSpeech = 6400;   // 0,4 с — первый заход, чтобы не ждать конца фразы
		const int Hop = 4800;           // 0,3 с между следующими заходами
		const int Window = 12800;       // 0,8 с контекста, модель смотрит окно в 2 с
		const int SilenceFlush = 1600;  // 0,1 с тишины — дослать конец короткого слова
		const int MinFlush = 4800;
		const int GapMs = 180;          // пакеты пропали — человек отпустил кнопку, фраза кончилась
		readonly object stateGate = new object();
		readonly Dictionary<int, Speaker> speakers = new Dictionary<int, Speaker>();
		readonly object jobGate = new object();
		readonly Dictionary<int, PendingJob> pending = new Dictionary<int, PendingJob>();
		readonly Dictionary<int, int> consumedGeneration = new Dictionary<int, int>();
		readonly AutoResetEvent jobReady = new AutoResetEvent(false);
		readonly PhraseVoiceTap tap;
		readonly Action<int> onSlot;
		readonly Action<string> onStatus;
		AppConfig config;
		Thread worker;
		Process process;
		Stream stdin;
		StreamReader stdout;
		volatile bool running;
		volatile bool enabled;
		int ownClientId = -1;
		int promptPending;
		string promptText = "";
		int epoch;
		bool playbackLatched;

		/// <summary>Саундбар уже играет нарезку: фразы в это время не ставим в очередь.</summary>
		public Func<bool> ClipPlaying;

		public IAudioPassiveConsumer Tap => tap;

		public int OwnClientId
		{
			get => ownClientId;
			set => ownClientId = value;
		}

		public PhraseListener(AppConfig config, Action<int> onSlot, Action<string> onStatus)
		{
			this.config = config;
			this.onSlot = onSlot;
			this.onStatus = onStatus;
			tap = new PhraseVoiceTap(this);
		}

		public void Apply(AppConfig config)
		{
			this.config = config;
			enabled = config.PhraseListen && config.Phrases != null && config.Phrases.Count > 0;
			promptText = BuildPrompt(config);
			if (!enabled)
			{
				StopWorker();
				return;
			}

			Interlocked.Exchange(ref promptPending, 1);
			EnsureWorker();
			jobReady.Set();
		}

		public void Dispose()
		{
			enabled = false;
			StopWorker();
			jobReady.Dispose();
		}

		void EnsureWorker()
		{
			lock (stateGate)
			{
				if (running)
					return;
				running = true;
				worker = new Thread(Loop) { IsBackground = true, Name = "phrase-listen" };
				worker.Start();
			}
		}

		void StopWorker()
		{
			running = false;
			jobReady.Set();
			try { worker?.Join(1500); } catch { }
			KillProcess();
			worker = null;
		}

		void Loop()
		{
			onStatus("Загружаю распознавание фраз на видеокарту…");
			if (!StartProcess())
			{
				running = false;
				return;
			}

			onStatus("Слушаю фразы в канале.");
			while (running)
			{
				PollGaps();
				bool hasWork;
				lock (jobGate)
					hasWork = pending.Count > 0 || promptPending != 0;
				if (!hasWork)
					jobReady.WaitOne(80);
				if (!running)
					break;

				if (Interlocked.Exchange(ref promptPending, 0) == 1)
					SendPrompt();

				PendingJob job;
				lock (jobGate)
				{
					if (pending.Count == 0)
						continue;
					var enumerator = pending.GetEnumerator();
					enumerator.MoveNext();
					job = enumerator.Current.Value;
					pending.Remove(job.ClientId);
				}

				if (IsStale(job))
					continue;
				if (!WriteAudio(job.ClientId, job.Pcm))
					break;
				if (!ReadResult(job))
					break;
			}

			KillProcess();
		}

		bool StartProcess()
		{
			var python = FindPython();
			var script = Path.Combine(AppContext.BaseDirectory, "phrase_listen.py");
			if (python == null || !File.Exists(script))
			{
				onStatus(python == null
					? "Распознавание не запущено: в папке программы нет папки python."
					: "Распознавание не запущено: нет phrase_listen.py рядом с программой.");
				return false;
			}

			var psi = new ProcessStartInfo
			{
				FileName = python,
				Arguments = "\"" + script + "\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			var pythonDir = Path.GetDirectoryName(python);
			var torchCandidates = new[]
			{
				Path.Combine(pythonDir, "Lib", "site-packages", "torch", "lib"),
				Path.GetFullPath(Path.Combine(pythonDir, "..", "Lib", "site-packages", "torch", "lib"))
			};
			foreach (var torchLib in torchCandidates)
			{
				if (!Directory.Exists(torchLib))
					continue;
				psi.Environment["PATH"] = torchLib + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
				break;
			}
			psi.Environment["PYTHONIOENCODING"] = "utf-8";
			psi.Environment["PYTHONNOUSERSITE"] = "1";

			try
			{
				process = Process.Start(psi);
				stdin = process.StandardInput.BaseStream;
				stdout = process.StandardOutput;
				process.ErrorDataReceived += (_, e) => { };
				process.BeginErrorReadLine();
				var line = stdout.ReadLine();
				if (line == null || !line.StartsWith("ready", StringComparison.Ordinal))
				{
					var detail = line == null ? "процесс закрылся" : line;
					onStatus("Распознавание не запустилось: " + detail);
					KillProcess();
					return false;
				}
				SendPrompt();
				return true;
			}
			catch (Exception ex)
			{
				onStatus("Распознавание не запустилось: " + ex.Message);
				KillProcess();
				return false;
			}
		}

		void SendPrompt()
		{
			var bytes = Encoding.UTF8.GetBytes(promptText ?? "");
			WriteFrame(unchecked((int)0xFFFFFFFF), bytes);
			var line = stdout.ReadLine();
			if (line == null)
				running = false;
		}

		bool WriteAudio(int clientId, byte[] pcm)
		{
			try
			{
				WriteFrame(clientId, pcm);
				return true;
			}
			catch
			{
				return false;
			}
		}

		void WriteFrame(int clientId, byte[] payload)
		{
			var header = new byte[8];
			BitConverter.GetBytes(clientId).CopyTo(header, 0);
			BitConverter.GetBytes(payload.Length).CopyTo(header, 4);
			stdin.Write(header, 0, 8);
			if (payload.Length > 0)
				stdin.Write(payload, 0, payload.Length);
			stdin.Flush();
		}

		bool ReadResult(PendingJob job)
		{
			string line;
			try
			{
				line = stdout.ReadLine();
			}
			catch
			{
				return false;
			}

			if (line == null)
			{
				onStatus("Распознавание остановилось.");
				return false;
			}

			var tab = line.IndexOf('\t');
			if (tab < 0)
				return true;
			var text = line.Substring(tab + 1).Trim();
			if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
			{
				onStatus("Распознавание: " + text);
				return true;
			}

			if (text.Length == 0 || job.Epoch != Volatile.Read(ref epoch))
				return true;
			Match(job.ClientId, job.Generation, text);
			return true;
		}

		void Match(int clientId, int generation, string heard)
		{
			var heardNorm = Normalize(heard);
			if (heardNorm.Length == 0)
				return;

			List<PhraseTrigger> list;
			lock (stateGate)
			{
				if (consumedGeneration.TryGetValue(clientId, out var consumed) && generation <= consumed)
					return;
				list = config.Phrases;
			}
			if (list == null)
				return;

			var slots = new List<int>();
			foreach (var phrase in list)
			{
				var key = Normalize(phrase.Text);
				if (key.Length < 2)
					continue;
				if (heardNorm.IndexOf(key, StringComparison.Ordinal) < 0)
					continue;
				slots.Add(phrase.Slot);
			}
			if (slots.Count == 0)
				return;

			// Фраза уже сыграла или пришла, пока нарезка играет: это окно закрыто.
			// Следующая фраза — другое поколение и не ждёт таймера.
			var busy = IsClipPlaying();
			var play = !busy;
			lock (stateGate)
			{
				if (consumedGeneration.TryGetValue(clientId, out var consumed) && generation <= consumed)
					return;
				consumedGeneration[clientId] = generation;
				if (speakers.TryGetValue(clientId, out var speaker))
				{
					if (speaker.Generation == generation && !speaker.Closed)
						speaker.Triggered = true;
					else if (speaker.Generation > generation)
						play = false;
				}
			}

			lock (jobGate)
			{
				if (pending.TryGetValue(clientId, out var queued) && queued.Generation <= generation)
					pending.Remove(clientId);
			}

			if (!play)
				return;
			foreach (var slot in slots)
				onSlot(slot);
		}

		public static string Normalize(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return "";
			var sb = new StringBuilder(text.Length);
			foreach (var ch in text.ToLowerInvariant())
			{
				var c = ch == 'ё' ? 'е' : ch;
				if (char.IsLetterOrDigit(c))
					sb.Append(c);
				else if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
					sb.Append(' ');
			}

			return sb.ToString().Trim();
		}

		static string BuildPrompt(AppConfig config)
		{
			if (config.Phrases == null || config.Phrases.Count == 0)
				return "";
			var sb = new StringBuilder();
			foreach (var phrase in config.Phrases)
			{
				if (string.IsNullOrWhiteSpace(phrase.Text))
					continue;
				if (sb.Length > 0)
					sb.Append(", ");
				sb.Append(phrase.Text.Trim());
			}

			return sb.ToString();
		}

		bool IsClipPlaying()
		{
			var check = ClipPlaying;
			return check != null && check();
		}

		void PollGaps()
		{
			if (IsClipPlaying())
			{
				LatchPlayback();
				return;
			}

			UnlockPlayback();
			var now = DateTime.UtcNow;
			List<PendingJob> jobs = null;
			lock (stateGate)
			{
				foreach (var pair in speakers)
				{
					var job = DetachStale(pair.Key, pair.Value, now);
					if (job == null)
						continue;
					if (jobs == null)
						jobs = new List<PendingJob>();
					jobs.Add(job);
				}
			}

			if (jobs == null)
				return;
			foreach (var job in jobs)
				Offer(job);
		}

		void LatchPlayback()
		{
			bool rise = false;
			lock (stateGate)
			{
				if (!playbackLatched)
				{
					playbackLatched = true;
					Interlocked.Increment(ref epoch);
					rise = true;
				}

				foreach (var speaker in speakers.Values)
				{
					if (speaker.Closed)
						continue;
					speaker.ResetAudio();
					speaker.Closed = true;
				}
			}

			if (!rise)
				return;
			lock (jobGate)
				pending.Clear();
		}

		void UnlockPlayback()
		{
			lock (stateGate)
				playbackLatched = false;
		}

		bool IsStale(PendingJob job)
		{
			lock (stateGate)
			{
				if (job.Epoch != epoch)
					return true;
				return consumedGeneration.TryGetValue(job.ClientId, out var consumed) && job.Generation <= consumed;
			}
		}

		PendingJob DetachStale(int clientId, Speaker speaker, DateTime now)
		{
			if (speaker.Closed || !speaker.InSpeech || speaker.LastPacket == DateTime.MinValue)
				return null;
			if ((now - speaker.LastPacket).TotalMilliseconds < GapMs)
				return null;

			PendingJob job = null;
			if (!speaker.Triggered && speaker.Speech >= MinFlush && speaker.SinceSubmit > 0)
				job = MakeJob(clientId, speaker);
			speaker.ResetAudio();
			speaker.Closed = true;
			return job;
		}

		PendingJob MakeJob(int clientId, Speaker speaker)
		{
			var pcm = speaker.TailPcm(Math.Min(Window, speaker.Count));
			speaker.SinceSubmit = 0;
			speaker.Submitted = true;
			speaker.Seq++;
			return new PendingJob
			{
				ClientId = clientId,
				Generation = speaker.Generation,
				Epoch = Volatile.Read(ref epoch),
				Seq = speaker.Seq,
				Pcm = pcm
			};
		}

		void Offer(PendingJob job)
		{
			lock (jobGate)
			{
				if (pending.TryGetValue(job.ClientId, out var existing))
				{
					if (existing.Generation > job.Generation)
						return;
					if (existing.Generation == job.Generation && existing.Seq > job.Seq)
						return;
				}

				pending[job.ClientId] = job;
			}

			jobReady.Set();
		}

		void Accept(int clientId, short[] samples, int count)
		{
			if (!enabled || count <= 0 || clientId == ownClientId)
				return;
			if (IsClipPlaying())
			{
				LatchPlayback();
				return;
			}

			UnlockPlayback();
			long energy = 0;
			for (int i = 0; i < count; i++)
				energy += Math.Abs(samples[i]);
			int rms = (int)(energy / count);

			PendingJob flush = null;
			PendingJob live = null;
			lock (stateGate)
			{
				if (!speakers.TryGetValue(clientId, out var speaker))
				{
					if (speakers.Count >= 8)
						return;
					speaker = new Speaker();
					speakers[clientId] = speaker;
				}

				var now = DateTime.UtcNow;
				flush = DetachStale(clientId, speaker, now);
				speaker.LastPacket = now;
				if (speaker.Closed)
				{
					speaker.Generation++;
					speaker.Closed = false;
					speaker.Triggered = false;
				}

				speaker.Add(samples, count);
				if (rms >= RmsGate)
				{
					speaker.InSpeech = true;
					speaker.Silence = 0;
					speaker.Speech += count;
				}
				else if (speaker.InSpeech)
				{
					speaker.Silence += count;
				}

				speaker.SinceSubmit += count;
				bool ended = speaker.InSpeech && speaker.Silence >= SilenceFlush && speaker.Speech >= MinFlush;
				bool hop = !speaker.Triggered
					&& speaker.InSpeech
					&& speaker.Speech >= FirstSpeech
					&& speaker.SinceSubmit >= (speaker.Submitted ? Hop : FirstSpeech);
				if (speaker.Triggered)
				{
					if (ended)
					{
						speaker.ResetAudio();
						speaker.Closed = true;
					}
				}
				else if (ended || hop)
				{
					live = MakeJob(clientId, speaker);
					if (ended)
					{
						speaker.ResetAudio();
						speaker.Closed = true;
					}
				}
			}

			if (flush != null)
				Offer(flush);
			if (live != null)
				Offer(live);
		}

		void KillProcess()
		{
			lock (jobGate)
			{
				KillProcessCore();
			}
		}

		void KillProcessCore()
		{
			try
			{
				if (process != null && !process.HasExited)
					process.Kill();
			}
			catch { }

			try { stdin?.Dispose(); } catch { }
			try { stdout?.Dispose(); } catch { }
			try { process?.Dispose(); } catch { }
			stdin = null;
			stdout = null;
			process = null;
		}

		static string FindPython()
		{
			var candidates = new[]
			{
				Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
				Path.Combine(AppContext.BaseDirectory, "python", "Scripts", "python.exe")
			};
			foreach (var path in candidates)
			{
				if (File.Exists(path))
					return path;
			}

			return null;
		}

		sealed class PendingJob
		{
			public int ClientId;
			public int Generation;
			public int Epoch;
			public int Seq;
			public byte[] Pcm;
		}

		sealed class Speaker
		{
			readonly short[] buffer = new short[Rate * 2];
			int write;
			public int Count { get; private set; }
			public int Speech;
			public int Silence;
			public int SinceSubmit;
			public int Generation;
			public int Seq;
			public bool InSpeech;
			public bool Submitted;
			public bool Triggered;
			public bool Closed;
			public DateTime LastPacket;

			public void Add(short[] samples, int count)
			{
				for (int i = 0; i < count; i++)
				{
					buffer[write] = samples[i];
					write = (write + 1) % buffer.Length;
					if (Count < buffer.Length)
						Count++;
				}
			}

			public byte[] TailPcm(int samples)
			{
				if (samples > Count)
					samples = Count;
				var bytes = new byte[samples * 2];
				int start = write - samples;
				if (start < 0)
					start += buffer.Length;
				for (int i = 0; i < samples; i++)
				{
					short value = buffer[(start + i) % buffer.Length];
					bytes[i * 2] = (byte)value;
					bytes[i * 2 + 1] = (byte)(value >> 8);
				}

				return bytes;
			}

			public void ResetAudio()
			{
				InSpeech = false;
				Speech = 0;
				Silence = 0;
				SinceSubmit = 0;
				Submitted = false;
				Triggered = false;
				Count = 0;
				write = 0;
			}
		}

		sealed class PhraseVoiceTap : IAudioPassiveConsumer
		{
			readonly PhraseListener owner;
			public PhraseVoiceTap(PhraseListener owner) => this.owner = owner;
			public bool Active => true;

			public void Write(Span<byte> data, Meta meta)
			{
				if (meta == null || data.Length < 12)
					return;
				int clientId = meta.In.Sender.Value;
				int frames = data.Length / 4;
				int usable = frames - (frames % 3);
				if (usable <= 0)
					return;
				var mono = new short[usable / 3];
				int dst = 0;
				for (int frame = 0; frame < usable; frame += 3)
				{
					int acc = 0;
					for (int k = 0; k < 3; k++)
					{
						int o = (frame + k) * 4;
						short left = (short)(data[o] | (data[o + 1] << 8));
						short right = (short)(data[o + 2] | (data[o + 3] << 8));
						acc += left + right;
					}

					mono[dst++] = (short)(acc / 6);
				}

				owner.Accept(clientId, mono, mono.Length);
			}
		}
	}
}
