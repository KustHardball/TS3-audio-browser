using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Vorbis;
using NAudio.Wave;

namespace TsBrowser
{
	public static class SoundDecoder
	{
		const int MaxSeconds = 180;

		public static byte[] Decode(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				throw new InvalidOperationException("Файл не найден: " + path);

			WaveStream reader;
			try
			{
				reader = Open(path);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException("Не удалось открыть " + Path.GetFileName(path) + ": " + ex.Message, ex);
			}

			try
			{
				IWaveProvider source = reader;
				MediaFoundationResampler resampler = null;
				var fmt = reader.WaveFormat;
				if (fmt.Encoding != WaveFormatEncoding.Pcm || fmt.SampleRate != 48000 || fmt.Channels != 2 || fmt.BitsPerSample != 16)
				{
					resampler = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 2))
					{
						ResamplerQuality = 60
					};
					source = resampler;
				}

				try
				{
					using (var ms = new MemoryStream())
					{
						var buf = new byte[16384];
						int max = 48000 * 4 * MaxSeconds;
						int n;
						while ((n = source.Read(buf, 0, buf.Length)) > 0)
						{
							if (ms.Length + n > max)
								throw new InvalidOperationException("Нарезка длиннее трёх минут: " + Path.GetFileName(path));
							ms.Write(buf, 0, n);
						}

						var pcm = ms.ToArray();
						int len = pcm.Length - (pcm.Length % 4);
						if (len < 4)
							throw new InvalidOperationException("В файле нет звука: " + Path.GetFileName(path));
						if (len != pcm.Length)
							Array.Resize(ref pcm, len);
						return pcm;
					}
				}
				finally
				{
					resampler?.Dispose();
				}
			}
			finally
			{
				reader.Dispose();
			}
		}

		static WaveStream Open(string path)
		{
			var ext = Path.GetExtension(path).ToLowerInvariant();
			if (ext == ".ogg")
				return new VorbisWaveReader(path);
			if (ext == ".wav")
				return new WaveFileReader(path);
			return new MediaFoundationReader(path);
		}
	}

	/// <summary>
	/// Складывает звук страницы и нарезки. Одна копия идёт в колонки, вторая — в TeamSpeak.
	/// </summary>
	public sealed class AudioMixer : IDisposable
	{
		const int FrameBytes = 960 * 4;

		readonly PcmRing browser;
		readonly PcmRing output;
		readonly LocalMonitor monitor;
		readonly object gate = new object();
		readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		readonly List<Voice> voices = new List<Voice>();
		readonly byte[] browserBuf = new byte[FrameBytes];
		readonly byte[] mixed = new byte[FrameBytes];
		readonly Thread thread;
		volatile bool running = true;
		volatile int browserVolume;
		volatile int soundVolume;

		sealed class Voice
		{
			public string Path;
			public byte[] Pcm;
			public int Pos;
		}

		public AudioMixer(PcmRing browser, PcmRing output, LocalMonitor monitor, int browserVolume, int soundVolume)
		{
			this.browser = browser;
			this.output = output;
			this.monitor = monitor;
			SetVolumes(browserVolume, soundVolume);
			thread = new Thread(Loop) { IsBackground = true, Name = "audio-mix" };
			thread.Start();
		}

		public void SetVolumes(int browserVol, int soundVol)
		{
			if (browserVol < 0) browserVol = 0;
			if (browserVol > 100) browserVol = 100;
			if (soundVol < 0) soundVol = 0;
			if (soundVol > 100) soundVol = 100;
			browserVolume = browserVol;
			soundVolume = soundVol;
		}

		public void Cache(string path, byte[] pcm)
		{
			lock (gate)
				cache[path] = pcm;
		}

		public void Play(string path)
		{
			byte[] pcm;
			lock (gate)
				cache.TryGetValue(path, out pcm);

			if (pcm == null)
			{
				pcm = SoundDecoder.Decode(path);
				lock (gate)
					cache[path] = pcm;
			}

			lock (gate)
			{
				for (int i = voices.Count - 1; i >= 0; i--)
				{
					if (string.Equals(voices[i].Path, path, StringComparison.OrdinalIgnoreCase))
						voices.RemoveAt(i);
				}

				voices.Add(new Voice { Path = path, Pcm = pcm, Pos = 0 });
			}
		}

		void Loop()
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			long next = 0;
			while (running)
			{
				try
				{
					MixFrame();
				}
				catch
				{
					// Один сбой кадра не должен останавливать саундбар.
				}

				next += 20;
				var delay = next - clock.ElapsedMilliseconds;
				if (delay > 0)
					Thread.Sleep((int)Math.Min(delay, 40));
				else if (delay < -200)
					next = clock.ElapsedMilliseconds;
			}
		}

		void MixFrame()
		{
			int got = browser.ReadUpTo(browserBuf, 0, FrameBytes);
			byte[][] clips;
			int[] positions;
			int frame;
			lock (gate)
			{
				if (got == 0 && voices.Count == 0)
					return;

				// Без нарезок отдаём ровно то, что пришло со страницы, без добивки тишиной.
				// Пока нарезка играет, кадр всегда 20 мс, иначе её скорость поедет.
				frame = voices.Count == 0 ? got : FrameBytes;
				clips = new byte[voices.Count][];
				positions = new int[voices.Count];
				for (int i = 0; i < voices.Count; i++)
				{
					clips[i] = voices[i].Pcm;
					positions[i] = voices[i].Pos;
					voices[i].Pos += frame;
				}

				for (int i = voices.Count - 1; i >= 0; i--)
				{
					if (voices[i].Pos >= voices[i].Pcm.Length)
						voices.RemoveAt(i);
				}
			}

			int bVol = browserVolume;
			int sVol = soundVolume;
			for (int i = 0; i < frame; i += 2)
			{
				int sample = 0;
				if (i + 1 < got)
				{
					int raw = (short)(browserBuf[i] | (browserBuf[i + 1] << 8));
					sample += raw * bVol / 100;
				}

				for (int v = 0; v < clips.Length; v++)
				{
					int pos = positions[v] + i;
					if (pos + 1 >= clips[v].Length)
						continue;
					int raw = (short)(clips[v][pos] | (clips[v][pos + 1] << 8));
					sample += raw * sVol / 100;
				}

				if (sample > 32767) sample = 32767;
				if (sample < -32768) sample = -32768;
				mixed[i] = (byte)sample;
				mixed[i + 1] = (byte)(sample >> 8);
			}

			monitor.Write(mixed, frame);
			output.Write(mixed, frame);
		}

		public void Dispose()
		{
			running = false;
			try { thread.Join(800); } catch { }
		}
	}
}
