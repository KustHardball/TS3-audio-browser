using System;
using System.Threading.Tasks;
using TSLib;
using TSLib.Audio;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Messages;
using TSLib.Scheduler;

namespace TsBrowser
{
	public sealed class TeamSpeakSession : IDisposable
	{
		readonly DedicatedTaskScheduler scheduler;
		readonly TsFullClient client;
		readonly EncoderPipe encoder;
		readonly PreciseTimedPipe timePipe;
		readonly StaticMetaPipe metaPipe;
		readonly PcmRing ring;
		bool disposed;

		public string Uid { get; private set; } = "";

		public event Action<string> Status;

		/// <summary>Текст личного сообщения от другого клиента.</summary>
		public event Action<string> PrivateCommand;

		public TeamSpeakSession(PcmRing ring)
		{
			this.ring = ring;
			scheduler = new DedicatedTaskScheduler(Id.Null);
			client = new TsFullClient(scheduler);
			encoder = new EncoderPipe(Codec.OpusMusic) { Bitrate = 96_000 };
			timePipe = new PreciseTimedPipe(encoder, Id.Null)
			{
				ReadBufferSize = encoder.PacketSize,
				Paused = true
			};
			metaPipe = new StaticMetaPipe();
			metaPipe.SetVoice();

			ring.Into(timePipe).Chain(metaPipe).Chain(encoder).Chain(client);

			client.OnDisconnected += (_, args) =>
			{
				timePipe.Paused = true;
				var text = args.Error != null
					? "Соединение закрыто: " + args.Error.ErrorFormat()
					: "Соединение закрыто: " + args.ExitReason;
				Status?.Invoke(text);
			};
			client.OnErrorEvent += (_, error) => Status?.Invoke(error.ErrorFormat());
			client.OnEachTextMessage += (_, message) =>
			{
				if (message.Target != TextMessageTargetMode.Private)
					return;
				if (message.InvokerId == client.ClientId)
					return;
				if (string.IsNullOrWhiteSpace(message.Message))
					return;
				PrivateCommand?.Invoke(message.Message);
			};
		}

		public async Task Connect(AppConfig config)
		{
			var identity = await Task.Run(() => LoadOrCreateIdentity(config));
			Uid = identity.ClientUid.ToString();
			config.Save();

			string address = config.Server.Trim();
			if (address.IndexOf(':') < 0)
				address += ":9987";

			var connection = new ConnectionDataFull(
				address,
				identity,
				versionSign: TsVersionSigned.VER_WIN_3_X_X,
				username: string.IsNullOrWhiteSpace(config.Nickname) ? "Браузер-радио" : config.Nickname.Trim(),
				serverPassword: string.IsNullOrEmpty(config.ServerPassword) ? (Password?)null : Password.FromPlain(config.ServerPassword),
				defaultChannel: string.IsNullOrWhiteSpace(config.Channel) ? null : config.Channel.Trim(),
				defaultChannelPassword: string.IsNullOrEmpty(config.ChannelPassword) ? (Password?)null : Password.FromPlain(config.ChannelPassword));

			var connectResult = await scheduler.InvokeAsync(() => client.Connect(connection));
			if (!connectResult.GetOk(out var error))
				throw new InvalidOperationException(error.ErrorFormat());

			try
			{
				await scheduler.InvokeAsync(() => client.RequestTalkPower("браузер"));
			}
			catch
			{
				// На части серверов запрос права говорить не нужен и команда просто не принимается.
			}

			ring.Clear();
			timePipe.Paused = false;
		}

		public async Task Disconnect()
		{
			timePipe.Paused = true;
			if (!client.Connected && !client.Connecting)
				return;

			await scheduler.InvokeAsync(async () =>
			{
				if (client.Connected)
					await client.Disconnect();
			});
		}

		static IdentityData LoadOrCreateIdentity(AppConfig config)
		{
			int level = config.SecurityLevel;
			if (level < 1) level = 1;
			if (level > 16) level = 16;

			IdentityData identity;
			if (string.IsNullOrWhiteSpace(config.PrivateKey))
			{
				identity = TsCrypt.GenerateNewIdentity(level);
			}
			else
			{
				var loaded = TsCrypt.LoadIdentity(config.PrivateKey, config.KeyOffset);
				if (!loaded.Ok)
					identity = TsCrypt.GenerateNewIdentity(level);
				else
				{
					identity = loaded.Value;
					if (TsCrypt.GetSecurityLevel(identity) < level)
						TsCrypt.ImproveSecurity(identity, level);
				}
			}

			config.PrivateKey = identity.PrivateKeyString;
			config.KeyOffset = identity.ValidKeyOffset;
			config.SecurityLevel = level;
			return identity;
		}

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;

			timePipe.Paused = true;
			try
			{
				scheduler.InvokeAsync(async () =>
				{
					if (client.Connected)
						await client.Disconnect();
				}).Wait(TimeSpan.FromSeconds(4));
			}
			catch
			{
			}

			timePipe.Dispose();
			encoder.Dispose();
			client.Dispose();
			scheduler.Dispose();
		}
	}
}
