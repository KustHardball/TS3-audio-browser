using System;
using CefSharp;
using CefSharp.Handler;
using CefSharp.Structs;
using TSLib.Audio;

namespace TsBrowser
{
	/// <summary>
	/// Кольцевой буфер стерео PCM 48 кГц, 16 бит. Пишет поток страницы, читает отправка в TeamSpeak.
	/// </summary>
	public sealed class PcmRing : IAudioPassiveProducer
	{
		readonly object gate = new object();
		readonly byte[] buffer = new byte[48000 * 2 * 2 * 3];
		int readPos;
		int writePos;
		int count;
		long capturedPackets;

		public long CapturedPackets => System.Threading.Interlocked.Read(ref capturedPackets);

		public int BufferedMilliseconds
		{
			get
			{
				lock (gate)
					return count * 1000 / (48000 * 2 * 2);
			}
		}

		public void Clear()
		{
			lock (gate)
			{
				readPos = 0;
				writePos = 0;
				count = 0;
			}
		}

		public void Write(byte[] data, int length)
		{
			if (length <= 0)
				return;

			System.Threading.Interlocked.Increment(ref capturedPackets);
			lock (gate)
			{
				for (int i = 0; i < length; i++)
				{
					if (count == buffer.Length)
					{
						readPos = (readPos + 1) % buffer.Length;
						count--;
					}

					buffer[writePos] = data[i];
					writePos = (writePos + 1) % buffer.Length;
					count++;
				}
			}
		}

		public int Read(byte[] target, int offset, int length, out Meta meta)
		{
			meta = null;
			lock (gate)
			{
				if (count < length || length <= 0)
					return 0;

				for (int i = 0; i < length; i++)
				{
					target[offset + i] = buffer[readPos];
					readPos = (readPos + 1) % buffer.Length;
					count--;
				}

				return length;
			}
		}

		public void Dispose()
		{
		}
	}

	/// <summary>
	/// Забирает PCM страницы из CefSharp. Каналы приходят раздельно, float.
	/// </summary>
	public sealed class BrowserAudioCapture : AudioHandler
	{
		readonly PcmRing ring;
		readonly LocalMonitor monitor;
		int sampleRate = 48000;
		int channels = 2;

		public BrowserAudioCapture(PcmRing ring, LocalMonitor monitor)
		{
			this.ring = ring;
			this.monitor = monitor;
		}

		protected override bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref AudioParameters parameters)
		{
			return true;
		}

		protected override void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, AudioParameters parameters, int channels)
		{
			sampleRate = parameters.SampleRate > 0 ? parameters.SampleRate : 48000;
			this.channels = channels > 0 ? channels : 2;
		}

		protected override void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames, long pts)
		{
			if (data == IntPtr.Zero || noOfFrames <= 0)
				return;

			try
			{
				int rate = sampleRate;
				int channelCount = channels;
				var pcm = ConvertToStereo48(data, noOfFrames, channelCount, rate);
				if (pcm == null || pcm.Length == 0)
					return;

				monitor.Write(pcm);
				ring.Write(pcm, pcm.Length);
			}
			catch
			{
				// Поток звука не должен ронять окно.
			}
		}

		static unsafe byte[] ConvertToStereo48(IntPtr data, int frames, int channelCount, int sampleRate)
		{
			float** planar = (float**)data.ToPointer();
			if (planar == null)
				return null;

			if (channelCount < 1)
				channelCount = 1;
			if (sampleRate < 8000)
				sampleRate = 48000;

			int outFrames = (int)Math.Round(frames * (48000.0 / sampleRate));
			if (outFrames < 1)
				outFrames = 1;

			var bytes = new byte[outFrames * 4];
			double step = (double)frames / outFrames;
			for (int i = 0; i < outFrames; i++)
			{
				int src = (int)(i * step);
				if (src >= frames)
					src = frames - 1;

				float left = planar[0][src];
				float right = channelCount > 1 ? planar[1][src] : left;
				WriteSample(bytes, i * 4, left);
				WriteSample(bytes, i * 4 + 2, right);
			}

			return bytes;
		}

		static void WriteSample(byte[] target, int offset, float sample)
		{
			if (sample > 1f) sample = 1f;
			if (sample < -1f) sample = -1f;
			short value = (short)(sample * 32767f);
			target[offset] = (byte)value;
			target[offset + 1] = (byte)(value >> 8);
		}
	}

	public sealed class LocalMonitor : IDisposable
	{
		readonly object gate = new object();
		NAudio.Wave.BufferedWaveProvider provider;
		NAudio.Wave.WaveOutEvent output;
		bool enabled = true;

		public bool Enabled
		{
			get => enabled;
			set => enabled = value;
		}

		public void Write(byte[] pcm)
		{
			if (!enabled || pcm == null || pcm.Length == 0)
				return;

			lock (gate)
			{
				EnsureStarted();
				provider.AddSamples(pcm, 0, pcm.Length);
			}
		}

		void EnsureStarted()
		{
			if (output != null)
				return;

			provider = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(48000, 16, 2))
			{
				DiscardOnBufferOverflow = true,
				BufferDuration = TimeSpan.FromSeconds(2)
			};
			output = new NAudio.Wave.WaveOutEvent { DesiredLatency = 80 };
			output.Init(provider);
			output.Play();
		}

		public void Dispose()
		{
			lock (gate)
			{
				if (output == null)
					return;
				output.Stop();
				output.Dispose();
				output = null;
				provider = null;
			}
		}
	}
}
