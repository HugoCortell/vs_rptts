using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using ProtoBuf;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace RPTTS
{
	public sealed class RPTTSAPI : ModSystem // 99% Boilerplate from ttschat.cs
	{
		#region Variables
		private TTSChatSystem? RPTTSMain;
		private ICoreClientAPI? ClientAPI;
		public override bool ShouldLoad(EnumAppSide forSide) => true;
		private IClientNetworkChannel? ClientMessageChannel;
		private IServerNetworkChannel? ServerMessageChannel;

		[ProtoContract] public sealed class APISpeakRequest
		{
			[ProtoMember(1)] public string Text			{ get; set; } = "";
			[ProtoMember(2)] public int VoiceId 		{ get; set; }
			[ProtoMember(3)] public float Pitch 		{ get; set; }
			[ProtoMember(4)] public float GainMod		{ get; set; } = 1f;
			[ProtoMember(5)] public float FallMod		{ get; set; } = 1f;
		}

		[ProtoContract] public sealed class APISpeakEvent
		{
			[ProtoMember(1)] public string SenderUid	{ get; set; } = "";
			[ProtoMember(2)] public string Text			{ get; set; } = "";
			[ProtoMember(3)] public int VoiceId			{ get; set; }
			[ProtoMember(4)] public float Pitch			{ get; set; }
			[ProtoMember(5)] public float GainMod		{ get; set; } = 1f;
			[ProtoMember(6)] public float FallMod		{ get; set; } = 1f;
		}
		#endregion

		#region Network Registration
		public override void Start(ICoreAPI api)
		{
			api.Network
				.RegisterChannel("rptts-api")
				.RegisterMessageType<APISpeakRequest>()
				.RegisterMessageType<APISpeakEvent>();
		}

		public override void StartClientSide(ICoreClientAPI api)
		{ 
			ClientAPI = api;
			RPTTSMain = api.ModLoader.GetModSystem<TTSChatSystem>(); // Called prior to any code, so it should be safe from race conditions
			if (RPTTSMain == null) throw new InvalidOperationException("StartClientSide has failed to load TTSChatSystem.");

			ClientMessageChannel = api.Network
				.GetChannel("rptts-api")
				.SetMessageHandler<APISpeakEvent>(OnAPISpeakEvent);
		}

		public override void StartServerSide(ICoreServerAPI ServerAPI)
		{
			RPTTSMain = ServerAPI.ModLoader.GetModSystem<TTSChatSystem>();
			if (RPTTSMain == null) throw new InvalidOperationException("StartServerSide has failed to load TTSChatSystem.");

			ServerMessageChannel = ServerAPI.Network
				.GetChannel("rptts-api")
				.SetMessageHandler<APISpeakRequest>((from, req) =>
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
						if ((dx * dx + dy * dy + dz * dz) <= (double)TTSChatSystem.ServerRelayRadius * TTSChatSystem.ServerRelayRadius) // Uses const
						{
							ServerMessageChannel!.SendPacket(new APISpeakEvent
							{
								SenderUid	= sender.PlayerUID,
								Text		= req.Text ?? "",
								VoiceId		= req.VoiceId,
								Pitch		= req.Pitch,
								GainMod		= req.GainMod,
								FallMod		= req.FallMod
							}, recipient);
						}
					}
				}
			);
		}
		#endregion

		#region API Functions
		public void APIChatMessage(string rawtext, float? gainmodifier = 1f, float? falloffmodifier = 1f) // Entryway for API calls
		{
			if (string.IsNullOrWhiteSpace(rawtext) || ClientAPI == null) return;
			if (rawtext.StartsWith('/') || rawtext.StartsWith('.') || rawtext.StartsWith('*') || rawtext.StartsWith('!')) return; // Ignore commands/console/flavour
			if (RPTTSMain?.APIDebugMode == true) { ClientAPI.Logger.Notification("[rptts] APIChatMessage: '{0}'", rawtext); } // Keeps spam off the log if debug is disabled
			if (RPTTSMain?.APIEngine == null) { ClientAPI.Logger.Warning("[rptts] [ERROR] There's no APIEngine, bailing out!"); return; }

			ClientAPI.Event.EnqueueMainThreadTask(() =>
			{
				try
				{		
					if (RPTTSMain.APIEngine.HearSelf) // Local playback
					{
						RPTTSMain.APIEngine.Speak(
							rawtext,
							RPTTSMain.APIEngine.LocalVoiceID,
							RPTTSMain.APIEngine.PlayerPitch,
							null,
							GameMath.Clamp((gainmodifier ?? 1f), 0.5f, 1.5f), // Strictly clamped to avoid overwhelming 2D audio
							1f // Forced to default since it is 2D audio
						);
					}
					
					try // Send to network via server request
					{
						ClientMessageChannel?.SendPacket(new APISpeakRequest
						{
							Text		= rawtext,
							VoiceId		= RPTTSMain.APIEngine.LocalVoiceID,
							Pitch		= RPTTSMain.APIEngine.PlayerPitch, 
							GainMod		= GameMath.Clamp((gainmodifier ?? 1f), 0.2f, 2.25f), // Clamp GainModifier to handle weird values
							FallMod		= GameMath.Clamp((falloffmodifier ?? 1f), 1f, 8f)
						});
					}
					catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] APIChatMessage send failed: {0}", ex);}
				}
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] Speak (via APIChatMessage) (send message) failed: {0}", ex); }
			}, "rptts-api-send");
		}

		private void OnAPISpeakEvent(APISpeakEvent eventmessage) // Private, should not be API accessible
		{
			try
			{
				var playerself = ClientAPI!.World?.Player;
				if (playerself == null || playerself.PlayerUID == eventmessage.SenderUid) return; // Drop messages from ourselves
				if (RPTTSMain?.APIEngine == null) { ClientAPI.Logger.Warning("[rptts] [ERROR] APISpeakEvent failed to locate the KittenDriver. Bailing out."); return; }

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
				
				int maxrange = RPTTSMain.APIEngine!.HearingRange;
				if (dist2 > (double)maxrange * maxrange) return;
				
				// Play out request locally (server -> clients)
				RPTTSMain.APIEngine!.Speak(eventmessage.Text, eventmessage.VoiceId, eventmessage.Pitch, eventmessage.SenderUid, eventmessage.GainMod, eventmessage.FallMod);
			}
			catch (Exception ex) { ClientAPI?.Logger.Warning("[rptts] APISpeakEvent failed: {0}", ex); }
		}
		#endregion
	}
}
