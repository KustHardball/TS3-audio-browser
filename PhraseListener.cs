using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TSLib.Audio;

namespace TsBrowser
{
	/// <summary>
	/// Голос канала копится, пока человек говорит, и уходит на распознавание после паузы.
	/// С заданной фразой сравнивается по словам: одна-две лишние буквы ещё считаются совпадением.
	/// Пока один кусок считается, более новый для того же человека вытесняет старый в очереди.
	/// </summary>
	public sealed class PhraseListener : IDisposable
	{
		const int Rate = 16000;
		const int RmsGate = 450;
		const int Window = 48000;       // до 3 с речи, фраза уходит целиком, а не первым обрывком
		const int SilenceFlush = 6400;  // 0,4 с тишины между словами ещё не конец фразы
		const int MinFlush = 4800;
		const int GapMs = 400;          // пакеты пропали — человек отпустил кнопку, фраза кончилась
		readonly object stateGate = new object();
		readonly Dictionary<int, Speaker> speakers = new Dictionary<int, Speaker>();
		readonly object jobGate = new object();
		readonly Dictionary<int, PendingJob> pending = new Dictionary<int, PendingJob>();
		readonly Dictionary<int, int> consumedGeneration = new Dictionary<int, int>();
		readonly object statusGate = new object();
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
		int phase;
		string faultText = "";
		string heardText = "";
		string noteText = "";
		DateTime heardAt;
		DateTime noteAt;
		int lastGpuMs;
		int computing;

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
				SetPhase(0, "");
				StopWorker();
				return;
			}

			SetPhase(1, "");
			Interlocked.Exchange(ref promptPending, 1);
			EnsureWorker();
			jobReady.Set();
		}

		public PhraseHud Hud()
		{
			return new PhraseHud(Compose(), Level());
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
			int failures = 0;
			while (running)
			{
				SetPhase(1, "");
				if (!StartProcess())
				{
					running = false;
					break;
				}

				SetPhase(2, "");
				if (Pump())
					break;

				KillProcess();
				failures++;
				if (!running || failures >= 2)
				{
					SetPhase(4, "процесс распознавания остановился");
					running = false;
					break;
				}

				Remember(null, "процесс упал, запускаю снова");
			}

			KillProcess();
		}

		bool Pump()
		{
			while (running)
			{
				PollGaps();
				bool hasWork;
				lock (jobGate)
					hasWork = pending.Count > 0 || promptPending != 0;
				if (!hasWork)
					jobReady.WaitOne(80);
				if (!running)
					return true;

				if (Interlocked.Exchange(ref promptPending, 0) == 1)
					SendPrompt();

				PendingJob job = TakeOldest();
				if (job == null)
					continue;
				if (AgeMs(job.WaitStamp) > 2500)
				{
					Remember(null, "очередь отстала, старый кусок сброшен");
					continue;
				}
				if (IsStale(job))
					continue;

				var clock = Stopwatch.StartNew();
				Volatile.Write(ref computing, 1);
				bool wrote = WriteAudio(job.ClientId, job.Pcm);
				bool read = wrote && ReadResult(job);
				Volatile.Write(ref computing, 0);
				lock (statusGate)
					lastGpuMs = (int)clock.ElapsedMilliseconds;
				if (!read)
					return false;
			}

			return true;
		}

		PendingJob TakeOldest()
		{
			lock (jobGate)
			{
				if (pending.Count == 0)
					return null;
				PendingJob best = null;
				foreach (var job in pending.Values)
				{
					if (best == null || job.WaitStamp < best.WaitStamp)
						best = job;
				}

				pending.Remove(best.ClientId);
				return best;
			}
		}

		bool StartProcess()
		{
			var python = FindPython();
			var script = Path.Combine(AppContext.BaseDirectory, "phrase_listen.py");
			if (python == null || !File.Exists(script))
			{
				SetPhase(4, python == null
					? "в папке программы нет папки python"
					: "нет phrase_listen.py рядом с программой");
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
					SetPhase(4, "не запустилось: " + detail);
					KillProcess();
					return false;
				}
				SendPrompt();
				return true;
			}
			catch (Exception ex)
			{
				SetPhase(4, "не запустилось: " + ex.Message);
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
			{
				running = false;
				SetPhase(4, "процесс распознавания остановился");
			}
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
				SetPhase(4, "процесс распознавания остановился");
				return false;
			}

			var parts = line.Split('\t');
			if (parts.Length < 2)
				return true;
			var text = parts[1].Trim();
			if (parts[0].StartsWith("error", StringComparison.OrdinalIgnoreCase))
			{
				Remember(null, "ошибка: " + text);
				return true;
			}

			double logprob = 0;
			double noSpeech = 0;
			bool hasScores = parts.Length >= 4
				&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out logprob)
				&& double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out noSpeech);
			if (text.Length == 0)
				return true;
			if (job.Epoch != Volatile.Read(ref epoch))
			{
				Remember(text, "поздно: играла нарезка");
				return true;
			}

			Match(job.ClientId, job.Generation, text, logprob, noSpeech, hasScores);
			return true;
		}

		void Match(int clientId, int generation, string heard, double logprob, double noSpeech, bool hasScores)
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

			string bestKey = null;
			int bestSlot = 0;
			int bestLength = 0;
			foreach (var phrase in list)
			{
				var key = Normalize(phrase.Text);
				if (key.Length < 2 || !PhraseClose(heardNorm, key))
					continue;
				if (key.Length <= bestLength)
					continue;
				bestLength = key.Length;
				bestKey = key;
				bestSlot = phrase.Slot;
			}

			if (bestKey == null)
			{
				Remember(heard, IsJunk(heardNorm) ? "шум модели" : "не фраза");
				return;
			}

			if (hasScores && (noSpeech > 0.6 || logprob < -1.0))
			{
				Remember(heard, "модель не уверена");
				return;
			}

			if (IsClipPlaying())
			{
				Consume(clientId, generation);
				Remember(heard, "фраза есть, играет нарезка");
				return;
			}

			Consume(clientId, generation);
			Remember(heard, "пуск позиции " + bestSlot.ToString(CultureInfo.InvariantCulture));
			onSlot(bestSlot);
		}

		void Consume(int clientId, int generation)
		{
			lock (stateGate)
			{
				if (!consumedGeneration.TryGetValue(clientId, out var consumed) || generation > consumed)
					consumedGeneration[clientId] = generation;
				if (speakers.TryGetValue(clientId, out var speaker) && speaker.Generation == generation && !speaker.Closed)
					speaker.Triggered = true;
			}

			lock (jobGate)
			{
				if (pending.TryGetValue(clientId, out var queued) && queued.Generation <= generation)
					pending.Remove(clientId);
			}
		}

		static bool ContainsPhrase(string heard, string phrase)
		{
			return (" " + heard + " ").IndexOf(" " + phrase + " ", StringComparison.Ordinal) >= 0;
		}

		static bool PhraseClose(string heard, string phrase)
		{
			if (ContainsPhrase(heard, phrase))
				return true;
			var heardWords = heard.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var phraseWords = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (phraseWords.Length == 0 || heardWords.Length < phraseWords.Length)
				return false;
			bool alone = phraseWords.Length == 1;
			for (int start = 0; start <= heardWords.Length - phraseWords.Length; start++)
			{
				bool close = true;
				for (int i = 0; i < phraseWords.Length; i++)
				{
					if (!WordsClose(phraseWords[i], heardWords[start + i], alone))
					{
						close = false;
						break;
					}
				}
				if (close)
					return true;
			}

			return false;
		}

		static bool WordsClose(string expected, string heard, bool alone)
		{
			if (expected == heard)
				return true;
			int len = Math.Max(expected.Length, heard.Length);
			if (alone && len < 5)
				return false;
			if (!alone && len < 3)
				return false;
			int dist = EditDistance(expected, heard);
			if (len <= 6)
				return dist <= 1;
			return dist <= 2;
		}

		static int EditDistance(string left, string right)
		{
			var row = new int[right.Length + 1];
			for (int j = 0; j <= right.Length; j++)
				row[j] = j;
			for (int i = 1; i <= left.Length; i++)
			{
				int previous = row[0];
				row[0] = i;
				for (int j = 1; j <= right.Length; j++)
				{
					int current = row[j];
					int cost = left[i - 1] == right[j - 1] ? 0 : 1;
					int insert = row[j] + 1;
					int delete = row[j - 1] + 1;
					int replace = previous + cost;
					int best = insert < delete ? insert : delete;
					row[j] = best < replace ? best : replace;
					previous = current;
				}
			}

			return row[right.Length];
		}

		static bool IsJunk(string norm)
		{
			string[] junk =
			{
				"продолжение следует",
				"субтитры",
				"субтитры создавал",
				"спасибо за просмотр",
				"подписывайтесь на канал",
				"редактор субтитров",
				"музыка",
				"аплодисменты",
				"music",
				"applause",
				"thanks for watching",
				"thank you"
			};
			foreach (var item in junk)
			{
				if (norm == item || norm.StartsWith(item + " ", StringComparison.Ordinal))
					return true;
			}

			return false;
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
			Remember(null, "играет нарезка, голос пропускаю");
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

				else
					job.WaitStamp = Stopwatch.GetTimestamp();
				if (pending.ContainsKey(job.ClientId))
					job.WaitStamp = pending[job.ClientId].WaitStamp;
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
				if (ended && !speaker.Triggered)
					live = MakeJob(clientId, speaker);
				if (ended)
				{
					speaker.ResetAudio();
					speaker.Closed = true;
				}
			}

			if (flush != null)
				Offer(flush);
			if (live != null)
				Offer(live);
		}

		void SetPhase(int value, string fault)
		{
			lock (statusGate)
			{
				phase = value;
				if (!string.IsNullOrEmpty(fault))
					faultText = fault;
			}
			onStatus("");
		}

		void Remember(string heard, string note)
		{
			lock (statusGate)
			{
				if (heard != null)
				{
					heardText = heard.Length > 48 ? heard.Substring(0, 48) : heard;
					heardAt = DateTime.UtcNow;
				}
				if (note != null)
				{
					noteText = note;
					noteAt = DateTime.UtcNow;
				}
			}
			onStatus("");
		}

		string Compose()
		{
			int current;
			string fault, heard, note;
			DateTime heardStamp, noteStamp;
			int gpu, busy;
			lock (statusGate)
			{
				current = phase;
				fault = faultText;
				heard = heardText;
				note = noteText;
				heardStamp = heardAt;
				noteStamp = noteAt;
				gpu = lastGpuMs;
				busy = computing;
			}

			if (current == 0)
				return "Фразы выключены";
			if (current == 1)
				return "Фразы: гружу модель на видеокарту…";
			if (current == 4)
				return "Фразы: " + fault;

			var now = DateTime.UtcNow;
			var line = new StringBuilder("Фразы: слушаю");
			if (busy != 0)
				line.Append(", считаю");
			if (IsClipPlaying())
				line.Append(" · нарезка играет, голос пропускаю");
			var who = TalkingIds();
			if (who == "0")
				line.Append(" · тишина");
			else
			{
				line.Append(" · говорят ");
				line.Append(who);
			}
			line.Append(" · очередь ");
			lock (jobGate)
				line.Append(pending.Count.ToString(CultureInfo.InvariantCulture));
			if (gpu > 0)
			{
				line.Append(" · ");
				line.Append(gpu.ToString(CultureInfo.InvariantCulture));
				line.Append(" мс");
			}
			if (heard.Length > 0 && (now - heardStamp).TotalSeconds < 8)
			{
				line.Append(" · «");
				line.Append(heard);
				line.Append("»");
			}
			if (note.Length > 0 && (now - noteStamp).TotalSeconds < 8)
			{
				line.Append(" · ");
				line.Append(note);
			}
			return line.ToString();
		}

		int Level()
		{
			int current;
			lock (statusGate)
				current = phase;
			if (current == 0)
				return 0;
			if (current == 1)
				return 1;
			if (current == 4)
				return 4;
			int queued;
			int busy;
			lock (jobGate)
				queued = pending.Count;
			lock (statusGate)
				busy = computing;
			if (IsClipPlaying() || queued >= 2 || busy != 0)
				return 3;
			lock (stateGate)
			{
				var now = DateTime.UtcNow;
				foreach (var speaker in speakers.Values)
				{
					if (!speaker.Closed && speaker.InSpeech && (now - speaker.LastPacket).TotalMilliseconds < 500)
						return 3;
				}
			}
			return 2;
		}

		string TalkingIds()
		{
			var now = DateTime.UtcNow;
			var ids = new List<int>();
			lock (stateGate)
			{
				foreach (var pair in speakers)
				{
					var speaker = pair.Value;
					if (speaker.Closed || !speaker.InSpeech)
						continue;
					if ((now - speaker.LastPacket).TotalMilliseconds > 500)
						continue;
					ids.Add(pair.Key);
				}
			}

			if (ids.Count == 0)
				return "0";
			var text = new StringBuilder();
			for (int i = 0; i < ids.Count && i < 4; i++)
			{
				if (i > 0)
					text.Append(", ");
				text.Append(ids[i].ToString(CultureInfo.InvariantCulture));
			}
			if (ids.Count > 4)
				text.Append("…");
			return text.ToString();
		}

		static int AgeMs(long stamp)
		{
			if (stamp == 0)
				return 0;
			return (int)((Stopwatch.GetTimestamp() - stamp) * 1000 / Stopwatch.Frequency);
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
			public long WaitStamp;
			public byte[] Pcm;
		}

		sealed class Speaker
		{
			readonly short[] buffer = new short[Rate * 4];
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

		public readonly struct PhraseHud
		{
			public readonly string Line;
			public readonly int Level;

			public PhraseHud(string line, int level)
			{
				Line = line;
				Level = level;
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
