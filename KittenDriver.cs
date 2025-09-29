using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using OpenTK.Audio.OpenAL;
using System.Buffers;
using System.Collections.Concurrent;
using System.Xml.Serialization;
using VSSherpaOnnx;

// OpenAL calls require a *current* AL context on the calling thread.
// Otherwise, you’ll get access violations near world load.The thread doing AL calls should contain the current context.

namespace RPTTS
{
	public sealed class KittenTTSEngine : IDisposable
	{
		#region Variables
		private readonly ICoreClientAPI ClientAPI;
		private readonly object sync = new();
		private bool InitializationAttempted, InitializationFailed;
		public int VoicesAvailable { get; private set; } = 6; // Manually defined and never updated during runtime to avoid loading a model before the config UI
		private const int StreamChunkSamples = 2048; // ~128 ms @ 16 kHz | Increasing it to 4096 - 8192 would reduce alloc frequency, but increase latency

		// Paths
		private readonly string AssembliesDirectory;
		private readonly string MLModelDirectory;

		// Reflection Caches
		private Assembly?		SherpaAssembly;
		private Type?			TypeOfflineTTS, TypeOfflineTTSConfig, TypeOfflineTTSModelConfig, TypeOfflineTTSKittenModelConfig;
		private Type?			TypeOfflineTtsCallbackProgress;
		private MethodInfo?		MethodInfoGenerateWithCallbackProgress; // GenerateWithCallbackProgress(xxx)
		private PropertyInfo?	ModelSampleRate; // OfflineTts.SampleRate

		// Speaker Slots | Pool of combined model + source, each slot to be assigned to a player
		private sealed class SpeakerSlot
		{
			public string? SpeakerUID;
			public object? OfflineTTSInstance;
			public int SampleRate = 16000;
			public List<short> ChunkBuilder = new List<short>(KittenTTSEngine.StreamChunkSamples * 2);
			public ConcurrentQueue<short[]> StreamQueue = new ConcurrentQueue<short[]>();
			public volatile bool StreamEnded;
			public CancellationTokenSource? SlotCancellationToken;
			public int Source; // OpenAL source
			public readonly int[] Buffers = new int[6];
			public long TickID;
			public DateTime StartUTC;
			public float ActivePitch = 1f;
			public float ActiveGainMultiplier = 1f; // Used by other mods via API for shouting or whispering
			public float ActiveFalloff = 1f; // API only, for increasing falloff range past 1f
			public long LastPosUpdateMs = 0; // throttle position writes
		}
		private readonly List<SpeakerSlot> SpeakerSlotPool = new List<SpeakerSlot>();
		private readonly object SlotLock = new object();
		private readonly SemaphoreSlim ModelInitGate = new SemaphoreSlim(1, 1);
		private sealed class SlotCallbackShim
		{
			private readonly KittenTTSEngine TTSEngine;
			private readonly SpeakerSlot Slot;
			public SlotCallbackShim(KittenTTSEngine engine, SpeakerSlot slot) { TTSEngine = engine; Slot = slot; }
			public int OnProgress(IntPtr samples, int n, float progress) => TTSEngine.PCMProgressForSlot(Slot, samples, n, progress);
		}

		// TTS Engine
		public KittenTTSEngine(ICoreClientAPI api)
		{
			ClientAPI				= api;
			AssembliesDirectory		= Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
			MLModelDirectory		= Path.Combine(AssembliesDirectory, "models", "kitten-nano-en-v0_2-fp16");
		}

		// Settings
		public bool RPTTSDebug							= false;
		public int PositionRefreshRate	{ get; set; }	= 500;	// The high TTSZeroDistance value helps mask the low refresh rate, very good for performance
		public float BaseTTSVolume		{ get; set; }	= 2f;
		public float TTSZeroDistance					= 2f;	// Distance at which audio is 2D/max volume
		public float TTSMaxDistance						= 35f;	// Max distance at which the audio can be heard
		public int LocalVoiceID			{ get; set; } 	= 0;
		public float PlayerPitch		{ get; set; }	= 1f;
		public bool HearSelf			{ get; set; } 	= true;
		public bool ForbidLongMessages	{ get; set; } 	= false;
		public int HearingRange			{ get; set; } 	= 60;	// Max distance at which audio sources bother to play (avoid playing audio you can't hear)
		public int CPUThreadMode		{ get; set; }	= 1;	// 0 - 2 | Single thread, 50% threads, all threads
		public int MaxSpeakers			{ get; set; }	= 4;	// 2 - 32 | How many models we keep in memory to be assigned concurrently to individual speakers

		// Hello world messages
		private readonly Random MessageRNG = new();
		private readonly string[] InitializationGreetings =
		{
			"Hello world!",
			"I have arrived!",
			"Greetings, all!",
			"Salutations, my friends.",
			"I've come to see John Madden.",
			"I'm just looking to survive...",
			"Praise the thunder lord!",
			"Hello. I'm new here!"
		};
		private readonly string[] ShortenedMessageBackups =
		{
			"I'm not saying all that out loud.",
			"Cool story, bro.",
			"Most can say much with few words. I can't.",
			"What I have to say isn't of much substance.",
			"Something... Something... Long message.",
			"This message is way too long. Let's talk about the thunder lord instead!",
			"Your processor can't handle such a long message. Skipping it!"
		};
		#endregion

		#region Message Processing
		public void InitializationGreet()
		{
			var InitializationGreeting = InitializationGreetings[MessageRNG.Next(InitializationGreetings.Length)]; // RNG.Next is exclusive of the max value

			// Chat messages sent fron code don't trigger OnSendChatMessage, so there's no risk of double speak.
			ClientAPI.Logger.Notification("[rptts] Threads available: " + GetThreadCount() + " (out of " + Environment.ProcessorCount + ")");
			ClientAPI.Logger.Notification("[rptts] Initialization sanity started: '" + InitializationGreeting + "'");
			Speak(InitializationGreeting, LocalVoiceID, PlayerPitch, null);
			ClientAPI.Logger.Notification("[rptts] Initialization sanity ended.");
		}

		public void Speak(string text, int voiceID, float pitch, string? speakerUID, float? gainModifier = 1f, float? falloffModifier = 1f)
		{
			if (string.IsNullOrWhiteSpace(text)) return;
			if (ForbidLongMessages == true && text.Length > 84) { text = ShortenedMessageBackups[MessageRNG.Next(ShortenedMessageBackups.Length)]; }
			if (!EnsureInit()) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] EnsureInit failed in Speak()"); return; }

			speakerUID = string.IsNullOrWhiteSpace(speakerUID) ? null : speakerUID; // Normalize empty ids into null for faster ternary operations later on
			// Note: We don't need to clamp the VoiceID as the model already graciously falls back on 0 if a value is invalid

			SpeakerSlot slot = AcquireSlotFor(speakerUID);
			slot.ActivePitch				= pitch;
			slot.ActiveGainMultiplier		= gainModifier ?? 1f;
			slot.ActiveFalloff				= falloffModifier ?? 1f; // == HearingRange / customfalloff (so from 60 to 7.5 blocks)
			slot.StartUTC					= DateTime.UtcNow;
			slot.StreamEnded				= false;
			slot.ChunkBuilder.Clear();
			while (slot.StreamQueue.TryDequeue(out _)) {}
			slot.SlotCancellationToken?.Cancel();
			slot.SlotCancellationToken = new CancellationTokenSource();

			if (RPTTSDebug)
			{ 
				ShowChatMessageOnMainThread
				(
					"[rptts debug] " + "Recieved "
					+ (text?.Length ?? 0) + "ch message from "
					+ (speakerUID ?? "yourself") + " at "
					+ pitch + " pitch with voice " + voiceID + ". "
					+ "Gain: " + gainModifier + ", Falloff: " + falloffModifier + "."
				);
			}

			// Synthesize on a dedicated task/job
			Task.Factory.StartNew(() =>
			{
				try { SynthesizeIntoSlot(slot, text!, voiceID, pitch, slot.SlotCancellationToken.Token); }
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [ERROR] Slot synth failed: {0}", ex); }
			}, slot.SlotCancellationToken.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

			// Note: The Grapheme-to-Phoneme step in our model is likely our biggest bottleneck,
			// chunking the input can reduce our latency from 6.5s to 1.5s, but the output will sound incoherent
			// no amount of blending, mixing, fourier-ing, or signal theory can fix this. Do not attempt.
		}
		#endregion

		#region Helpers
		public void Dispose()
		{
			lock (SlotLock)
			{
				foreach (var slot in SpeakerSlotPool)
				{
					try { slot.SlotCancellationToken?.Cancel(); }																							catch { }
					try { if (slot.TickID != 0 || slot.Source != 0) ClientAPI.Event.EnqueueMainThreadTask(() => StopSlotStream(slot), "rptts-stop-slot"); }	catch { }
					try { slot.OfflineTTSInstance?.GetType().GetMethod("Dispose")?.Invoke(slot.OfflineTTSInstance, null); }									catch { }
				}
				SpeakerSlotPool.Clear();
			}
		}

		private bool EnsureInit() // Reflection-driven initialization (per-slot instance performed later)
		{
			if (InitializationAttempted) return !InitializationFailed;
			
			lock (sync)
			{
				if (InitializationAttempted) return !InitializationFailed;
				InitializationAttempted = true;
				
				try
				{
					// Validate files
					if (!Directory.Exists(MLModelDirectory))	throw new DirectoryNotFoundException($"Kitten model directory not found: {MLModelDirectory}");

					// Prepare natives & Load managed wrapper
					SherpaAssembly = VSSherpaOnnxSystem.EnsureSherpaReady(ClientAPI);

					TypeOfflineTTSKittenModelConfig		= SherpaAssembly.GetType("SherpaOnnx.OfflineTtsKittenModelConfig",	throwOnError: true);
					TypeOfflineTTSModelConfig			= SherpaAssembly.GetType("SherpaOnnx.OfflineTtsModelConfig",		throwOnError: false);
					TypeOfflineTTSConfig 				= SherpaAssembly.GetType("SherpaOnnx.OfflineTtsConfig",				throwOnError: false);
					TypeOfflineTTS						= SherpaAssembly.GetType("SherpaOnnx.OfflineTts",					throwOnError: false);

					// Callback progress type (for raw PCM streaming)
					TypeOfflineTtsCallbackProgress		= SherpaAssembly.GetType("SherpaOnnx.OfflineTtsCallbackProgress",	throwOnError: false);

					// Build KittenTTS config
					var kittenCFG = Activator.CreateInstance(TypeOfflineTTSKittenModelConfig!)!;
					TypeOfflineTTSKittenModelConfig!.GetField("Model")!		.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "model.fp16.onnx"));
					TypeOfflineTTSKittenModelConfig!.GetField("Voices")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "voices.bin"));
					TypeOfflineTTSKittenModelConfig!.GetField("Tokens")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "tokens.txt"));
					TypeOfflineTTSKittenModelConfig!.GetField("DataDir")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "espeak-ng-data"));
					
					var fLengthScale = TypeOfflineTTSKittenModelConfig!.GetField("LengthScale");
					if (fLengthScale != null) fLengthScale.SetValue(kittenCFG, 1.0f);

					// Build model config
					var modelCfg = Activator.CreateInstance(TypeOfflineTTSModelConfig!)!;
					TypeOfflineTTSModelConfig!.GetField("Kitten")!.SetValue(modelCfg, kittenCFG);
					TypeOfflineTTSModelConfig!.GetField("NumThreads")!.SetValue(modelCfg, GetThreadCount());
					
					var fProvider = TypeOfflineTTSModelConfig!.GetField("Provider");
					if (fProvider != null) fProvider.SetValue(modelCfg, "cpu");
					
					TypeOfflineTTSModelConfig!.GetField("Debug")!.SetValue(modelCfg, 0);

					// Build tts config (used if an instance is created on a worker thread in the background)
					var ttsCfg = Activator.CreateInstance(TypeOfflineTTSConfig!)!;
					TypeOfflineTTSConfig!.GetField("Model")!.SetValue(ttsCfg, modelCfg);

					// Cache GenerateWithCallbackProgress(text, speed, sid, callback)
					MethodInfoGenerateWithCallbackProgress =
						TypeOfflineTTS!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
							.FirstOrDefault(m =>
								m.Name == "GenerateWithCallbackProgress" &&
								m.GetParameters().Length == 4);
					
					if (MethodInfoGenerateWithCallbackProgress == null) throw new MissingMethodException(TypeOfflineTTS!.FullName, "GenerateWithCallbackProgress");
					ModelSampleRate = TypeOfflineTTS!.GetProperty("SampleRate", BindingFlags.Public | BindingFlags.Instance); // Cache SampleRate property

					ClientAPI.Logger.Notification("[rptts] TTS model initialized!");
					return true;
				}
				catch (Exception ex)
				{
					InitializationFailed = true;
					ClientAPI.Logger.Warning("[rptts] [CRITICAL] EnsureInit failed: {0}", ex);
					return false;
				}
			}
		}

		private Vintagestory.API.MathTools.Vec3d? GetSpeakerPosition(string? uid)
		{
			if (string.IsNullOrEmpty(uid))
			{
				var localplayer = ClientAPI.World?.Player?.Entity?.Pos?.XYZ;
				return localplayer;
			}
			var targetplayer = ClientAPI.World?.AllPlayers?.FirstOrDefault(player => player.PlayerUID == uid);
			return targetplayer?.Entity?.Pos?.XYZ;
		}
		
		public int GetThreadCount()
		{
			if		(CPUThreadMode == 0)	{ return 1; }												// Single Thread
			if		(CPUThreadMode == 1)	{ return Math.Max(1, Environment.ProcessorCount / 2); } 	// 50% Threads
			return Math.Max(1, Environment.ProcessorCount);												// Full thread use
		}

		public void ShowChatMessageOnMainThread(string message) // Calling ShowChatMessage causes crashes (System.InvalidOperationException)
		{
			try
			{ 
				ClientAPI.Event.EnqueueMainThreadTask(() => 
				{
					try { ClientAPI.ShowChatMessage(message); }
					catch (Exception ex) { ClientAPI.Logger.Warning ("[rptts] [CRITICAL] ShowChatMessage ({0}) failed to reach the main thread: {1}", message, ex); }
				}, "rptts-chat");
			}
			catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [CRITICAL] ShowChatMessage ({0}) failed to enqueue to the main thread: {1}", message, ex); }
		}

		public string GetSpeakerCount() { return $"{MaxSpeakers} ({SpeakerSlotPool.Count} Active)"; }
		#endregion

		#region Slot Management
		private object CreatePerSlotTTSInstance()
		{
			// Build Kitten/Model/TTS configs fresh so each slot owns its instance
			var kittenCFG = Activator.CreateInstance(TypeOfflineTTSKittenModelConfig!)!;
			TypeOfflineTTSKittenModelConfig!.GetField("Model")!		.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "model.fp16.onnx"));
			TypeOfflineTTSKittenModelConfig!.GetField("Voices")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "voices.bin"));
			TypeOfflineTTSKittenModelConfig!.GetField("Tokens")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "tokens.txt"));
			TypeOfflineTTSKittenModelConfig!.GetField("DataDir")!	.SetValue(kittenCFG, Path.Combine(MLModelDirectory, "espeak-ng-data"));
			var fLengthScale = TypeOfflineTTSKittenModelConfig!.GetField("LengthScale");
			if (fLengthScale != null) fLengthScale.SetValue(kittenCFG, 1.0f);

			var modelCfg = Activator.CreateInstance(TypeOfflineTTSModelConfig!)!;
			TypeOfflineTTSModelConfig!.GetField("Kitten")!.SetValue(modelCfg, kittenCFG);
			TypeOfflineTTSModelConfig!.GetField("NumThreads")!.SetValue(modelCfg, GetThreadCount());
			var fProvider = TypeOfflineTTSModelConfig!.GetField("Provider");
			if (fProvider != null) fProvider.SetValue(modelCfg, "cpu");
			TypeOfflineTTSModelConfig!.GetField("Debug")!.SetValue(modelCfg, 0);

			var ttsCfg = Activator.CreateInstance(TypeOfflineTTSConfig!)!;
			TypeOfflineTTSConfig!.GetField("Model")!.SetValue(ttsCfg, modelCfg);
			return Activator.CreateInstance(TypeOfflineTTS!, new object[] { ttsCfg })!;
		}

		private SpeakerSlot AcquireSlotFor(string? speakerUID)
		{
			lock (SlotLock)
			{
				// Reuse slot already bound to this speaker if present
				var existing = SpeakerSlotPool.FirstOrDefault(slot => slot.SpeakerUID == speakerUID);
				if (existing != null)
				{
					if (RPTTSDebug)
					{
						string slotassignmentlog =
						(
							"[rptts debug] Slot Assignment: Re-use (" 
							+ SpeakerSlotPool.IndexOf(existing) + ", " 
							+ (speakerUID ?? "local") + ")"
						);

						ShowChatMessageOnMainThread(slotassignmentlog);
						ClientAPI.Logger.Notification(slotassignmentlog);
					}
					CancelSlot(existing);
					return existing;
				}

				// Find an idle slot
				var idle = SpeakerSlotPool.FirstOrDefault
				(
					slot => 
					slot.SlotCancellationToken == null
					&& slot.Source == 0
					&& slot.TickID == 0
					&& slot.SpeakerUID == null
				);
				if (idle != null)
				{ 
					idle.SpeakerUID = speakerUID;
					if (RPTTSDebug)
					{
						string slotassignmentlog =
						(
							"[rptts debug] Slot Assignment: Idle (" 
							+ SpeakerSlotPool.IndexOf(idle) + ", " 
							+ (speakerUID ?? "local") + ")"
						);
						ShowChatMessageOnMainThread(slotassignmentlog);
						ClientAPI.Logger.Notification(slotassignmentlog);
					}
					return idle;
				}

				// Create a new slot if under capacity
				if (SpeakerSlotPool.Count < Math.Max(1, MaxSpeakers))
				{
					var slot = new SpeakerSlot { SpeakerUID = speakerUID, ActivePitch = PlayerPitch };
					SpeakerSlotPool.Add(slot); // Model creation is handled by SynthesizeIntoSlot on background thread
					if (RPTTSDebug)
					{
						string slotassignmentlog =
						(
							"[rptts debug] Slot Assignment: Created New (" 
							+ SpeakerSlotPool.IndexOf(slot) + ", " 
							+ (speakerUID ?? "local") + ")"
						);
						ShowChatMessageOnMainThread(slotassignmentlog);
						ClientAPI.Logger.Notification(slotassignmentlog);
					}
					return slot;
				}

				// Evict oldest speaking slot (cut off mid-sentence)
				var victim = SpeakerSlotPool
					.OrderBy(s => s.StartUTC)
					.First();
				if (RPTTSDebug)
				{
					string slotassignmentlog =
					(
						"[rptts debug] Slot Assignment: Eviction (" 
						+ SpeakerSlotPool.IndexOf(victim) + ", " 
						+ (speakerUID ?? "local") + ")"
					);
					ShowChatMessageOnMainThread(slotassignmentlog);
					ClientAPI.Logger.Notification(slotassignmentlog);
				}
				CancelSlot(victim);
				victim.SpeakerUID = speakerUID;
				return victim;
			}
		}

		private void SlotTick(SpeakerSlot slot)
		{
			if (slot.Source == 0) { StopSlotStream(slot); return; }

			// Unqueue processed buffers and refill from slot queue
			AL.GetSource(slot.Source, ALGetSourcei.BuffersProcessed, out int processed);
			while (processed-- > 0)
			{
				int buffer = AL.SourceUnqueueBuffer(slot.Source);
				if (slot.StreamQueue.TryDequeue(out var chunk))
				{
					var handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
					try { AL.BufferData(buffer, ALFormat.Mono16, handle.AddrOfPinnedObject(), chunk.Length * sizeof(short), slot.SampleRate); }
					finally { handle.Free(); }
					AL.SourceQueueBuffer(slot.Source, buffer);
				}
				// if not, leave buffer unqueued. We'll re-prime when data arrives.
			}
			
			AL.GetSource(slot.Source, ALGetSourcei.BuffersQueued, out int queued);
			if (queued == 0) // Prime if empty
			{
				for (int i = 0; i < slot.Buffers.Length; i++)
				{
					if (!slot.StreamQueue.TryDequeue(out var chunk)) break;
					var handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
					try { AL.BufferData(slot.Buffers[i], ALFormat.Mono16, handle.AddrOfPinnedObject(), chunk.Length * sizeof(short), slot.SampleRate); }
					finally { handle.Free(); }
					AL.SourceQueueBuffer(slot.Source, slot.Buffers[i]);
				}
			}

			// Start if not playing and we have buffers
			AL.GetSource(slot.Source, ALGetSourcei.SourceState, out int stateInt);
			if ((ALSourceState)stateInt != ALSourceState.Playing)
			{
				AL.GetSource(slot.Source, ALGetSourcei.BuffersQueued, out queued);
				if (queued > 0) AL.SourcePlay(slot.Source);
			}

			// Update position at no higher rate than PositionRefreshRate, and only if not local player
			if (slot.SpeakerUID != null)
			{
				long currentMS = Environment.TickCount64;
				if (currentMS - slot.LastPosUpdateMs >= PositionRefreshRate)
				{
					// Position Refresh
					slot.LastPosUpdateMs = currentMS;
					var speaker = GetSpeakerPosition(slot.SpeakerUID);
					var speakerplayer = ClientAPI.World?.AllPlayers?.FirstOrDefault(player => player.PlayerUID == slot.SpeakerUID)?.Entity;
					if (speaker == null || speakerplayer == null || speakerplayer.Alive == false) // Lost track or player died
					{
						// Lost track of entity: stop audio and abort ongoing synthesis
						StopSlotStream(slot);
						try { slot.SlotCancellationToken?.Cancel(); } catch { }
						return;
					}
					AL.Source(slot.Source, ALSource3f.Position, (float)speaker.X, (float)speaker.Y, (float)speaker.Z);

					// Verbose Fall-off Debugging (commented out for performance) | Result: Everything works
					// if (RPTTSDebug && slot.SpeakerUID != null)
					// {
					// 	var debugspeaker = GetSpeakerPosition(slot.SpeakerUID);
					// 	var debuglocalplayer = ClientAPI.World?.Player?.Entity?.Pos?.XYZ;
						
					// 	if (debugspeaker != null && debuglocalplayer != null)
					// 	{
					// 		double deltaX = debugspeaker.X - debuglocalplayer.X, deltaY = debugspeaker.Y - debuglocalplayer.Y, deltaZ = debugspeaker.Z - debuglocalplayer.Z;
					// 		double debugdistance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);

					// 		AL.GetSource(slot.Source, ALSource3f.Position, out float speakerX, out float speakerY, out float speakerZ);
					// 		AL.GetListener(ALListener3f.Position, out float listenerX, out float listenerY, out float listenerZ);

					// 		int debugdistancemodel = AL.Get(ALGetInteger.DistanceModel);
					// 		AL.GetSource(slot.Source, ALSourceb.SourceRelative, out bool debugrelative);

					// 		ShowChatMessageOnMainThread
					// 		(
					// 			"===[" + slot.SpeakerUID + "'s message falloff]===\n"					+
					// 			$"Distance: {debugdistance:0.##}\n"										+
					// 			$"Cutoff: {(TTSMaxDistance - debugdistance):0.##}\n"					+
					// 			$"Speaker: ({speakerX:0.##}, {speakerY:0.##}, {speakerZ:0.##})\n"		+
					// 			$"Listener: ({listenerX:0.##}, {listenerY:0.##}, {listenerZ:0.##})\n"	+
					// 			$"Model: {debugdistancemodel} (Relative: {debugrelative}, Should be: False)"
					// 		);
					// 	}
					// }
				}
			}

			// Shutdown when stream ends and drained
			if (slot.StreamEnded && slot.StreamQueue.IsEmpty)
			{
				AL.GetSource(slot.Source, ALGetSourcei.BuffersQueued, out queued);
				if (queued == 0) { StopSlotStream(slot); }
			}
		}

		private void CancelSlot(SpeakerSlot slot)
		{
			try { slot.SlotCancellationToken?.Cancel(); } catch { }
			
			// Stop stream and free AL on main thread, will be recreated on next start
			if (slot.TickID != 0 || slot.Source != 0) { ClientAPI.Event.EnqueueMainThreadTask(() => StopSlotStream(slot), "rptts-stop-slot-stream");}
			slot.StreamEnded = true;
			
			while (slot.StreamQueue.TryDequeue(out _)) {}
			slot.ChunkBuilder.Clear();
		}
		#endregion

		#region Slot Synth & Stream
		private void SynthesizeIntoSlot(SpeakerSlot slot, string text, int voiceId, float pitch, CancellationToken canceltoken)
		{
			if (canceltoken.IsCancellationRequested) return;

			// Start the OpenAL streaming source on the main thread (only while this slot is active)
			ClientAPI.Event.EnqueueMainThreadTask(() =>
			{
				try { StartSlotStream(slot); }
				catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [ERROR] StartSlotStream failed: {0}", ex); }
			}, "rptts-start-slot-stream");

			try
			{
				// Initialize a new model into the slot if it does not exist | This could potentially be its own function in the future
				if (slot.OfflineTTSInstance == null)
				{
					var CapturedCancelationToken = slot.SlotCancellationToken;
					if (canceltoken.IsCancellationRequested) return;
					try { ModelInitGate.Wait(canceltoken); } catch (OperationCanceledException) { return; }
					try
					{
						// Verify that another worker has not gone ahead of us and created the model
						if (slot.OfflineTTSInstance == null && ReferenceEquals(slot.SlotCancellationToken, CapturedCancelationToken))
						{
							var modelinittime = System.Diagnostics.Stopwatch.StartNew();
							var inst = CreatePerSlotTTSInstance(); // Avg. Time: 2500ms
							slot.OfflineTTSInstance = inst;

							modelinittime.Stop();
							if (RPTTSDebug) { ShowChatMessageOnMainThread("[rptts debug] Initialized new model slot in " + modelinittime.ElapsedMilliseconds + "ms"); }
							ClientAPI.Logger.Notification("[rptts] Initialized new model slot in " + modelinittime.ElapsedMilliseconds + "ms");

							try { slot.SampleRate = (int)(ModelSampleRate!.GetValue(inst) ?? 16000); } catch { slot.SampleRate = 16000; }
						}
					}
					finally { ModelInitGate.Release(); }
					if (canceltoken.IsCancellationRequested) return;
				}

				var methodinfo = MethodInfoGenerateWithCallbackProgress!; // Invoke GenerateWithCallbackProgress(text, speed, sid, callback)
				var speed = 1.0f;
				var shim = new SlotCallbackShim(this, slot);
				var methodinfocallback = typeof(SlotCallbackShim).GetMethod(nameof(SlotCallbackShim.OnProgress))!;
				var methodinfodelegate = Delegate.CreateDelegate(TypeOfflineTtsCallbackProgress!, shim, methodinfocallback);
				methodinfo.Invoke(slot.OfflineTTSInstance!, new object[] { text, speed, voiceId, methodinfodelegate });
				GC.KeepAlive(methodinfodelegate);
			}
			
			catch (TargetInvocationException tie) { ClientAPI.Logger.Warning("[rptts] [ERROR] TTS invoke error: {0}", tie.InnerException ?? tie); }
			catch (Exception ex) { ClientAPI.Logger.Warning("[rptts] [ERROR] TTS synth error: {0}", ex); }
			finally
			{
				// Enqueue the last partial buffer to avoid it getting cut off
				var cb = slot.ChunkBuilder;
				if (cb.Count > 0)
				{
					var tail = new short[cb.Count];
					cb.CopyTo(0, tail, 0, cb.Count);
					slot.StreamQueue.Enqueue(tail);
				}
				slot.ChunkBuilder.Clear();
				slot.StreamEnded = true;
			}
		}

		// Callback target for sherpa-onnx: (IntPtr samples, int n, float progress) -> int
		// Receives float32 PCM in [-1, 1]. Converts to PCM16 and appends to _accumPcm.
		private int PCMProgressForSlot(SpeakerSlot slot, IntPtr samples, int samplecount, float progress)
		{
			try
			{
				var token = slot.SlotCancellationToken?.Token ?? default;
				if (token.IsCancellationRequested) return 0;

				var floatbuffer = ArrayPool<float>.Shared.Rent(samplecount);
				Marshal.Copy(samples, floatbuffer, 0, samplecount);
				var chunkbuffer = slot.ChunkBuilder;
				for (int i = 0; i < samplecount; i++)
				{
					float v = (floatbuffer[i] * slot.ActiveGainMultiplier) * BaseTTSVolume;
					if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
					chunkbuffer.Add((short)MathF.Round(v * short.MaxValue));
					if (chunkbuffer.Count >= StreamChunkSamples)
					{
						var chunk = new short[StreamChunkSamples];
						chunkbuffer.CopyTo(0, chunk, 0, StreamChunkSamples);
						slot.StreamQueue.Enqueue(chunk);
						chunkbuffer.RemoveRange(0, StreamChunkSamples);
					}
				}
				ArrayPool<float>.Shared.Return(floatbuffer);
				return 1;
			}
			catch { return 0; }
		}

		private void StartSlotStream(SpeakerSlot slot) // Per-slot OpenAL streaming
		{
			bool islocalplayer = string.IsNullOrEmpty(slot.SpeakerUID); // Local player is 2D

			// Create buffers + source
			for (int i = 0; i < slot.Buffers.Length; i++) slot.Buffers[i] = AL.GenBuffer();
			slot.Source = AL.GenSource();
			AL.Source(slot.Source, ALSourceb.SourceRelative, islocalplayer);
			AL.Source(slot.Source, ALSourcef.Pitch, slot.ActivePitch);
			AL.Source(slot.Source, ALSourcef.ReferenceDistance, TTSZeroDistance);
			AL.Source(slot.Source, ALSourcef.MaxDistance, TTSMaxDistance);
			AL.Source(slot.Source, ALSourcef.MinGain, 0f); // Required for falloff to work
			AL.Source(slot.Source, ALSourcef.MaxGain, 1f); // Required for falloff to work
			AL.Source(slot.Source, ALSourcef.RolloffFactor, islocalplayer ? 0f : slot.ActiveFalloff);
			AL.Enable((ALCapability)AL.GetEnumValue("AL_SOURCE_DISTANCE_MODEL")); // Explicitly enable the source distance model
			AL.Source(slot.Source, (ALSourcei)0xD000, (int)ALDistanceModel.LinearDistanceClamped); // Independent distance model to ensure 1-0 falloff

			if (!islocalplayer) // Prime an initial position
			{
				var pos = GetSpeakerPosition(slot.SpeakerUID);
				if (pos != null)	{ AL.Source(slot.Source, ALSource3f.Position, (float)pos.X, (float)pos.Y, (float)pos.Z); }
				else				{ AL.Source(slot.Source, ALSource3f.Position, 0f, 0f, 0f); }
			}
			else { AL.Source(slot.Source, ALSource3f.Position, 0f, 0f, 0f); }

			// Tick while active only
			slot.TickID = ClientAPI.Event.RegisterGameTickListener(dt => SlotTick(slot), 16);
		}

		private void StopSlotStream(SpeakerSlot slot)
		{
			if (slot.TickID != 0) { ClientAPI.Event.UnregisterGameTickListener(slot.TickID); slot.TickID = 0; }
			try { if (slot.Source != 0) { AL.SourceStop(slot.Source); } } catch { }
			try { if (slot.Source != 0) { AL.Source(slot.Source, ALSourcei.Buffer, 0); } } catch { }
			try { for (int i = 0; i < slot.Buffers.Length; i++) if (slot.Buffers[i] != 0) AL.DeleteBuffer(slot.Buffers[i]); } catch { }
			try { if (slot.Source != 0) AL.DeleteSource(slot.Source); } catch { }
			for (int i = 0; i < slot.Buffers.Length; i++) slot.Buffers[i] = 0;
			slot.Source = 0;
			try { slot.SlotCancellationToken?.Dispose(); } catch { }
			slot.SlotCancellationToken = null;
			slot.SpeakerUID = null;
			slot.StreamEnded = true;
		}
		#endregion
	}
}
