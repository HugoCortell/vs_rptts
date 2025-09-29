using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using ProtoBuf; // The networking API of the game
using System.Linq;
using Vintagestory.API.Config;

namespace RPTTS
{
	public class TTSChatSystem : ModSystem
	{
		private ICoreClientAPI? ClientAPI;
		private KittenTTSEngine? VoiceSynthEngine;
		private bool SanityCheckScheduled;

		private TTSClientConfig UserConfig = null!;
		private GuiDialogTTSSettings? SettingsDialogue;
		private bool DebugMode = false;
		public bool ExplicitCallsOnly = false; // Used to control our subscription to OnSendChatMessage

		// Signal packages used as settings sync requests
		[ProtoContract] public sealed class ShowSetupMsg	{ [ProtoMember(1)] public byte _ = 0; } // server -> client: Open settings once
		[ProtoContract] public sealed class SetupAckMsg		{ [ProtoMember(1)] public byte _ = 0; } // client -> server: Marked as seen for this world
		[ProtoContract] public sealed class SetupPingMsg	{ [ProtoMember(1)] public byte _ = 0; } // client -> server: Ask if setup needed
		private IClientNetworkChannel? ClientNetworkChannel;
		private IServerNetworkChannel? ServerNetworkChannel;

		// TTS Network Relay
		[ProtoContract] public sealed class SpeakRequest // Client -> Server
		{
			[ProtoMember(1)] public string Text { get; set; } = "";
			[ProtoMember(2)] public int VoiceId { get; set; }
			[ProtoMember(3)] public float Pitch { get; set; }
		}

		[ProtoContract] public sealed class SpeakEvent // Server -> Other Clients
		{
			[ProtoMember(1)] public string SenderUid	{ get; set; } = "";
			[ProtoMember(2)] public string Text			{ get; set; } = "";
			[ProtoMember(3)] public int VoiceId			{ get; set; }
			[ProtoMember(4)] public float Pitch			{ get; set; }
		}
		public const int ServerRelayRadius = 200; // server-side envelope, clients still cull by HearingRange
		private IClientNetworkChannel? ClientMessageChannel;
		private IServerNetworkChannel? ServerMessageChannel;

		// API read-only exposed variables
		public bool APIDebugMode			=> DebugMode;
		public KittenTTSEngine? APIEngine	=> VoiceSynthEngine;


		public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client || forSide == EnumAppSide.Server;

		// Registers channel & message types on both network sides
		public override void Start(ICoreAPI api)
		{
			api.Network
				.RegisterChannel("rptts-setup")
				.RegisterMessageType<ShowSetupMsg>()
				.RegisterMessageType<SetupAckMsg>()
				.RegisterMessageType<SetupPingMsg>();

			api.Network
				.RegisterChannel("rptts-speak")
				.RegisterMessageType<SpeakRequest>()
				.RegisterMessageType<SpeakEvent>();
		}

		#region Client
		public override void StartClientSide(ICoreClientAPI api)
		{
			ClientAPI = api;
			UserConfig = api.LoadModConfig<TTSClientConfig>("rptts_settings.json") ?? new TTSClientConfig();
			
			// Registering a command for opening the tts settings window
			api.ChatCommands
				.Create("tts")
				.WithDescription("Open TTS settings GUI window")
				.HandleWith(args =>
				{
					OpenTTSSettings();
					return TextCommandResult.Success();
				}
			);

			// Enable/disable debug mode for verbose logging on the chatbox
			api.ChatCommands
				.Create("ttsdebug")
				.WithDescription("Enable or disable verbose on-chat logging")
				.HandleWith(args =>
				{
					if (VoiceSynthEngine == null) { return TextCommandResult.Error("[rptts] [CRITICAL] VoiceSynthEngine is missing, can't execute command."); }
					VoiceSynthEngine.RPTTSDebug = !VoiceSynthEngine.RPTTSDebug;
					DebugMode = VoiceSynthEngine.RPTTSDebug;
					ClientAPI.ShowChatMessage("[rptts] [DEBUG] Debug Now: " + VoiceSynthEngine.RPTTSDebug);
					
					return TextCommandResult.Success();
				}
			);

			// Dump KittenDriver settings and execute a test line
			api.ChatCommands
				.Create("ttstest")
				.WithDescription("Print the live KittenDriver settings (debug command)")
				.HandleWith(args =>
				{
					try
					{
						VoiceSynthEngine ??= new KittenTTSEngine(ClientAPI);
						VoiceSynthEngine.Speak(
							"Did you ever hear the tragedy of Darth Plagueus the weise? Noh? I thought not, It's No story the jedi would tell you.",
							VoiceSynthEngine.LocalVoiceID,
							VoiceSynthEngine.PlayerPitch,
							null
						);
						
						string testlog =
							$"KittenDriver Settings:\n"														+
							$"Voice ID = {VoiceSynthEngine.LocalVoiceID}\n"									+
							$"Volume = {VoiceSynthEngine.BaseTTSVolume:0.0}\n"								+
							$"Pitch = {VoiceSynthEngine.PlayerPitch:0.0}\n"									+
							$"Positional Refresh Rate = {VoiceSynthEngine.PositionRefreshRate}\n"			+
							$"Hearing Distance = {VoiceSynthEngine.HearingRange:0.##}\n"					+
							$"Audible Distance = {VoiceSynthEngine.TTSMaxDistance:0.##}\n"					+
							$"Speaker Count = {VoiceSynthEngine.GetSpeakerCount()}"							;
						ClientAPI.ShowChatMessage(testlog);
						
						return TextCommandResult.Success();
					}
					catch (Exception ex)
					{
						ClientAPI?.ShowChatMessage("[rptts] [CRITICAL] ttstest failed: " + ex.Message);
						return TextCommandResult.Error("[rptts] Critical error encountered! See log for details.");
					}
				}
			);

			// For toggling the subscription to OnSendChatMessage on/off, mostly for other mods to use
			api.ChatCommands
				.Create("ttsexplicit")
				.WithDescription("Toggles whether tts should trigger whenever a player speaks, or only when explicitly requested by other mods")
				.HandleWith(args =>
				{
					try
					{
						OverwriteChatSubscription(!ExplicitCallsOnly);
						return TextCommandResult.Success();
					}
					catch (Exception ex)
					{
						ClientAPI?.ShowChatMessage("[rptts] [ERROR] ttsexplicit failed: " + ex.Message);
						return TextCommandResult.Error("[rptts] Error when executing command! See log for details.");
					}
				}
			);

			VoiceSynthEngine = new KittenTTSEngine(ClientAPI) // Create new KittenEngine & apply settings
			{
				BaseTTSVolume			= UserConfig.BaseTTSVolume,
				PositionRefreshRate		= UserConfig.PositionRefreshRate,
				LocalVoiceID			= UserConfig.VoiceID,
				ForbidLongMessages		= UserConfig.AvoidLongMessages,
				PlayerPitch				= UserConfig.PlayerPitch,
				HearingRange			= UserConfig.HearingRange,
				CPUThreadMode			= UserConfig.CPUThreadMode,
				HearSelf				= UserConfig.HearSelf,
				MaxSpeakers				= UserConfig.MaxIdleModels
			};

			ClientAPI.Event.OnSendChatMessage		+= OnSendChatMessage;	// Called whenever a player sends a message (local only for now)
			ClientAPI.Event.LevelFinalize			+= OnLevelFinalize;		// Called when the player loads into a world

			// Network: setup client channels & handlers
			ClientNetworkChannel = api.Network
				.GetChannel("rptts-setup")
				.SetMessageHandler<ShowSetupMsg>(OnShowSetup);
			
			ClientMessageChannel = api.Network
				.GetChannel("rptts-speak")
				.SetMessageHandler<SpeakEvent>(OnSpeakEvent);

			ClientAPI.Logger.Notification("[rptts] TTS Chat System initiated!");
		}

		private void OnLevelFinalize()
		{
			if (SanityCheckScheduled) { return; } SanityCheckScheduled = true;
			if (UserConfig.MaxIdleModels > 8) { ClientAPI!.ShowChatMessage(Lang.Get("rptts:Error-StartupSpeakerCountWarn", UserConfig.MaxIdleModels * 25)); }

			ClientAPI!.Logger.Notification("[rptts] LevelFinalize happened, scheduling initialization sanity check.");

			// Delay a bit so audio + player entity are fully ready
			ClientAPI.Event.RegisterCallback(_ =>
			{
				try
				{
					VoiceSynthEngine ??= new KittenTTSEngine(ClientAPI);
					ClientAPI.Logger.Notification("[rptts] Running initialization sanity check...");
					VoiceSynthEngine.InitializationGreet();
				}
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] Initialization sanity check failed: {0}", ex); }

				try { ClientNetworkChannel?.SendPacket(new SetupPingMsg()); }
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] SetupPing failed to send: {0}", ex); }
			}, 2500); // If we try to trigger our TTS too early, it'll cause a crash. We need to wait for the player and audio system to initialize in the world.
		}

		// Outgoing chat message sent by player
		private void OnSendChatMessage(int groupId, ref string message, ref EnumHandling handled)
		{
			if (string.IsNullOrWhiteSpace(message) || ClientAPI == null) return;
			if (message.StartsWith('/') || message.StartsWith('.')) return; // Ignore commands/console

			string toSpeak = message; // Copy ref param into a variable before using inside a lambda
			if (DebugMode) { ClientAPI.Logger.Notification("[rptts] OnSendChatMessage: '{0}'", toSpeak); } // Keeps spam off the log if debug is disabled

			ClientAPI.Event.EnqueueMainThreadTask(() =>
			{
				try
				{
					VoiceSynthEngine ??= new KittenTTSEngine(ClientAPI);
					
					if (VoiceSynthEngine.HearSelf) // Local playback
					{
						VoiceSynthEngine.Speak(
							toSpeak,
							VoiceSynthEngine.LocalVoiceID,
							VoiceSynthEngine.PlayerPitch,
							null
						);
					}
					
					try // Send to network via server request
					{
						ClientMessageChannel?.SendPacket(new SpeakRequest
						{
							Text		= toSpeak,
							VoiceId		= VoiceSynthEngine.LocalVoiceID,
							Pitch		= VoiceSynthEngine.PlayerPitch
						});
					}
					catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] SpeakRequest send failed: {0}", ex);}
				}
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] Speak (send message) failed: {0}", ex); }
			}, "rptts-speak-send");
		}

		// Incoming relay from server (nearby speaker)
		private void OnSpeakEvent(SpeakEvent eventmessage)
		{
			try
			{
				var playerself = ClientAPI!.World?.Player;
				if (playerself == null || playerself.PlayerUID == eventmessage.SenderUid) return; // Drop messages from ourselves
				if (VoiceSynthEngine == null)
				{
					ClientAPI.Logger.Warning("[rptts] [ERROR] OnSpeakEvent failed to locate the KittenDriver. Bailing out.");
					return;
				}

				// Find sender entity
				var sender = ClientAPI.World?.AllPlayers?.FirstOrDefault(p => p.PlayerUID == eventmessage.SenderUid);
				
				var senderPos		= sender?.Entity?.Pos?.XYZ;
				var localposition	= playerself.Entity?.Pos?.XYZ;
				if (senderPos == null || localposition == null) return;
				
				// Drop any requests that aren't inside our hearing range
				double dx = senderPos.X - localposition.X;
				double dy = senderPos.Y - localposition.Y;
				double dz = senderPos.Z - localposition.Z;
				double dist2 = dx * dx + dy * dy + dz * dz;
				
				int maxrange = VoiceSynthEngine!.HearingRange;
				if (dist2 > (double)maxrange * maxrange) return;
				
				// Play out request locally
				VoiceSynthEngine!.Speak(eventmessage.Text, eventmessage.VoiceId, eventmessage.Pitch, eventmessage.SenderUid);
			}
			catch (Exception ex) { ClientAPI?.Logger.Warning("[rptts] OnSpeakEvent failed: {0}", ex); }
		}

		private void OnShowSetup(ShowSetupMsg _)
		{
			ClientAPI?.Event.RegisterCallback(__ =>
			{
				OpenTTSSettings();
				ClientNetworkChannel?.SendPacket(new SetupAckMsg());
				ClientAPI?.Logger.Notification("[rptts] Setup dialog shown & ACK-nowledgement was sent back");
			}, 50);
			
			// We're using a RegisterCallback not for the actual time delay (that'd be very dirty!), but rather because this timer
			// only starts after the player properly spawns into the world. Which is something you can't reliably check for directly even with events.
			// Not without a lot of messy and inefficient code. So paradoxically, this callback is the cleanest and most performant approach.
		}

		private void OpenTTSSettings()
		{
			if (ClientAPI == null) return;
			VoiceSynthEngine ??= new KittenTTSEngine(ClientAPI); // ensure non-null for GUI

			SettingsDialogue ??= new GuiDialogTTSSettings(ClientAPI, UserConfig, VoiceSynthEngine, SaveConfigAndApply);
			if (!SettingsDialogue.IsOpened()) { SettingsDialogue.TryOpen(); }
		}

		private void SaveConfigAndApply(TTSClientConfig clientConfig)
		{
			UserConfig = clientConfig;

			if (VoiceSynthEngine != null)
			{
				VoiceSynthEngine.BaseTTSVolume			= clientConfig.BaseTTSVolume;
				VoiceSynthEngine.PositionRefreshRate	= clientConfig.PositionRefreshRate;
				VoiceSynthEngine.LocalVoiceID			= clientConfig.VoiceID;
				VoiceSynthEngine.ForbidLongMessages		= clientConfig.AvoidLongMessages;
				VoiceSynthEngine.PlayerPitch			= clientConfig.PlayerPitch;
				VoiceSynthEngine.HearingRange			= clientConfig.HearingRange;
				VoiceSynthEngine.CPUThreadMode			= clientConfig.CPUThreadMode;
				VoiceSynthEngine.HearSelf				= clientConfig.HearSelf;
				VoiceSynthEngine.MaxSpeakers			= clientConfig.MaxIdleModels;
			}

			ClientAPI!.StoreModConfig(UserConfig, "rptts_settings.json");
		}
		#endregion

		#region Server
		public override void StartServerSide(ICoreServerAPI ServerAPI)
		{
			ServerNetworkChannel = ServerAPI.Network
				.GetChannel("rptts-setup")
				.SetMessageHandler<SetupAckMsg>(OnSetupAck)
				.SetMessageHandler<SetupPingMsg>((from, _msg) =>
				{
					var requestingplayer = (IServerPlayer)from;
					bool seen = requestingplayer.WorldData.GetModData<bool>("rptts-setup-done", false);
					if (!seen) ServerNetworkChannel!.SendPacket(new ShowSetupMsg(), requestingplayer);
				}
			);
				
			ServerMessageChannel = ServerAPI.Network
				.GetChannel("rptts-speak")
				.SetMessageHandler<SpeakRequest>((from, req) =>
				{
					var sender = from as IServerPlayer;
					if (sender?.Entity?.Pos == null) return;
					
					var senderposition = sender.Entity.Pos.XYZ;
					foreach (var rawrecipient in ServerAPI.World.AllOnlinePlayers)
					{
						var recipient = rawrecipient as IServerPlayer;
						if (recipient?.Entity?.Pos == null) continue;
						
						var recipientposition = recipient.Entity.Pos.XYZ;
						double dx = senderposition.X - recipientposition.X, dy = senderposition.Y - recipientposition.Y, dz = senderposition.Z - recipientposition.Z;
						if (recipient.PlayerUID == sender.PlayerUID) continue; // Don't send back to sender
						if ((dx * dx + dy * dy + dz * dz) <= (double)ServerRelayRadius * ServerRelayRadius)
						{
							ServerMessageChannel!.SendPacket(new SpeakEvent
							{
								SenderUid	= sender.PlayerUID,
								Text		= req.Text ?? "",
								VoiceId		= req.VoiceId,
								Pitch		= req.Pitch
							}, recipient);
						}
					}
				}
			);

			ServerAPI.Logger.Notification("[rptts] TTS Chat System initiated on Server.");
			ServerAPI.Event.PlayerNowPlaying += OnPlayerNowPlaying;
		}

		private void OnPlayerNowPlaying(IServerPlayer player)
		{
			bool seen = player.WorldData.GetModData<bool>("rptts-setup-done", false);
			if (!seen) ServerNetworkChannel?.SendPacket(new ShowSetupMsg(), player); // Fire the setup dialog once for this player in this save
		}

		private void OnSetupAck(IPlayer fromPlayer, SetupAckMsg _)
		{
			if (fromPlayer is IServerPlayer serverplayer)
			{
				if (!serverplayer.WorldData.GetModData<bool>("rptts-setup-done", false))
				{
					serverplayer.WorldData.SetModData("rptts-setup-done", true);
				}
			}
		}
		#endregion

		public void OverwriteChatSubscription(bool value)
		{
			if (ClientAPI == null) return;
			ExplicitCallsOnly = value;

			ClientAPI.Event.OnSendChatMessage -= OnSendChatMessage;
			if (ExplicitCallsOnly) { ClientAPI.Event.OnSendChatMessage += OnSendChatMessage; }

			string logmessage = "[rptts] Explicit calls is now set to: " + ExplicitCallsOnly;
			ClientAPI.ShowChatMessage(logmessage);
			ClientAPI.Logger.Notification(logmessage);
		}

		public override void Dispose()
		{
			try { if (ClientAPI != null)	ClientAPI.Event.OnSendChatMessage	-= OnSendChatMessage; }		catch { }
			try { if (ClientAPI != null)	ClientAPI.Event.LevelFinalize		-= OnLevelFinalize; }		catch { }
			try { VoiceSynthEngine?.Dispose(); } catch { }
		}
	}
}
