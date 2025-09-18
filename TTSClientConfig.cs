using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;

public class TTSClientConfig
{
	public float	BaseTTSVolume = 2f; // Clamp should be 0.5f to 3.5f
	public int		PositionRefreshRate = 500;
	public bool		AvoidLongMessages = false;
	public bool		HearSelf = true;
	public int		VoiceID = 0;
	public float	PlayerPitch = 1f; // 0.5 - 1.8
	public int		HearingRange = 60; // 40 - 150
	public int		CPUThreadMode = 1; // 0 - 2
	public int		MaxIdleModels = 4; // 2 - 32
}

namespace RPTTS
{
	public class GuiDialogTTSSettings : GuiDialog
	{
		private readonly TTSClientConfig UserConfigReference;
		private readonly KittenTTSEngine KittenDriver;
		private readonly Action<TTSClientConfig> OnSaveSettings;

		private TTSClientConfig SettingsDraft;
		private int VoicePreviewNonce;

		// For Cancel
		private readonly float	OriginalVolume;
		private readonly int	OriginalRefreshRate;
		private readonly int	OriginalVoice;
		private readonly bool	OriginalAvoid;
		private readonly bool	OriginalHearSelf;
		private readonly float	OriginalPitch;
		private readonly int	OriginalHearingRange;
		private readonly int	OriginalCPUMode;
		private readonly int	OriginalMaxModels;

		public GuiDialogTTSSettings(ICoreClientAPI ClientAPI, TTSClientConfig UserConfig, KittenTTSEngine Engine, Action<TTSClientConfig> OnSave): base(ClientAPI)
		{
			UserConfigReference		= UserConfig	?? throw new ArgumentNullException(nameof(UserConfig));
			KittenDriver			= Engine		?? throw new ArgumentNullException(nameof(Engine));
			this.OnSaveSettings		= OnSave;

			SettingsDraft = new TTSClientConfig {
				BaseTTSVolume		= GameMath.Clamp(UserConfig.BaseTTSVolume, 0.5f, 3.5f),
				PositionRefreshRate	= GameMath.Clamp(UserConfig.PositionRefreshRate, 50, 2000),
				AvoidLongMessages	= UserConfig.AvoidLongMessages,
				PlayerPitch			= GameMath.Clamp(UserConfig.PlayerPitch, 0.6f, 1.6f),
				VoiceID				= GameMath.Clamp(UserConfig.VoiceID, 0, 999),
				HearingRange		= GameMath.Clamp(UserConfig.HearingRange, 40, 150),
				CPUThreadMode		= GameMath.Clamp(UserConfig.CPUThreadMode, 0, 2),
				HearSelf			= UserConfig.HearSelf,
				MaxIdleModels		= GameMath.Clamp(UserConfig.MaxIdleModels, 2, 32)
			};

			OriginalVolume			= KittenDriver.BaseTTSVolume;
			OriginalRefreshRate		= KittenDriver.PositionRefreshRate;
			OriginalVoice			= KittenDriver.LocalVoiceID;
			OriginalAvoid			= KittenDriver.ForbidLongMessages;
			OriginalPitch			= KittenDriver.PlayerPitch;
			OriginalHearingRange	= KittenDriver.HearingRange;
			OriginalCPUMode			= KittenDriver.CPUThreadMode;
			OriginalHearSelf		= KittenDriver.HearSelf;
			OriginalMaxModels		= KittenDriver.MaxSpeakers;

			ComposeDialog();
		}

		public override string ToggleKeyCombinationCode => "rptts-settings";

		public override bool TryOpen()
		{
			if (IsOpened()) return true;

			SettingsDraft = new TTSClientConfig { // Re-seed the draft values when the window opens
				BaseTTSVolume						= GameMath.Clamp(UserConfigReference.BaseTTSVolume, 0.5f, 3.5f),
				PositionRefreshRate					= GameMath.Clamp(UserConfigReference.PositionRefreshRate, 50, 2000),
				AvoidLongMessages					= UserConfigReference.AvoidLongMessages,
				PlayerPitch							= GameMath.Clamp(UserConfigReference.PlayerPitch, 0.6f, 1.6f),
				VoiceID								= GameMath.Clamp(UserConfigReference.VoiceID, 0, 999),
				HearingRange						= GameMath.Clamp(UserConfigReference.HearingRange, 40, 150),
				CPUThreadMode						= GameMath.Clamp(UserConfigReference.CPUThreadMode, 0, 2),
				HearSelf							= UserConfigReference.HearSelf,
				MaxIdleModels						= GameMath.Clamp(UserConfigReference.MaxIdleModels, 2, 32)
			};

			ComposeDialog();
			return base.TryOpen();
		}

		#region UI Rendering
		private void ComposeDialog()
		{
			var DialogueBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

			// Overall padding & child root
			var BackgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
			BackgroundBounds.BothSizing = ElementSizing.FitToChildren;

			// Layout metrics
			int x = 20;
			int w = 620; // content width
			int y = (int)(GuiStyle.TitleBarHeight + 10);

			// Card 1: Description
			var DescriptionBox		= ElementBounds.Fixed(x, y, w, 90);
			var DescriptionTextBox	= ElementBounds.Fixed(x + 6, y + 6, w - 12, 85);
			y += 90 + 10;

			// Card 2: Settings
			var MainSettingsBox		= ElementBounds.Fixed(x, y, w, 240);
			int rowY = y + 10;
			var VolumeLabel			= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var VolumeSlider		= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			var PitchLabel 			= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var PitchSlider			= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			var RefreshRateLabel	= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var RefreshRateSlider	= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			var HearRangeLabel		= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var HearRangeSlider		= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			var CPUModeLabel		= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var CPUModeSlider		= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			var MaxModelsLabel		= ElementBounds.Fixed(x + 10, rowY, 200, 22);
			var MaxModelsSlider		= ElementBounds.Fixed(x + 220, rowY, w - 230, 22);
			rowY += 28;
			int HearSelfToggleWidth = 140;
			int HearSelfToggleX		= x + w - HearSelfToggleWidth - 10;
			var HearSelfLabel		= ElementBounds.Fixed(x + 10, rowY, (HearSelfToggleX - (x + 10) - 10), 22);
			var HearSelfToggle		= ElementBounds.Fixed(HearSelfToggleX, rowY, HearSelfToggleWidth, 24);
			rowY += 28;
			int AvoidLongsWidth		= 140;
			int AvoidLongsX			= x + w - AvoidLongsWidth - 10;
			var AvoidLongsLabel		= ElementBounds.Fixed(x + 10, rowY, (AvoidLongsX - (x + 10) - 10), 22);
			var AvoidLongsToggle	= ElementBounds.Fixed(AvoidLongsX, rowY, AvoidLongsWidth, 24);
			y += 240 + 10;

			// Card 3: Voice selector
			var VoiceSelectionBox	= ElementBounds.Fixed(x, y, w, 80);
			var VoiceSelectionLabel = ElementBounds.Fixed(x + 10, y + 10, w - 20, 24);
			var VoiceSelectPrev		= ElementBounds.Fixed(x + 10, y + 40, 32, 26);
			var VoiceName			= ElementBounds.Fixed(x + 50, y + 40, 180, 26);
			var VoiceSelectionNext	= ElementBounds.Fixed(x + 240, y + 40, 32, 26);
			y += 80 + 10;

			// Card 4: Buttons
			var ConfigActionsBox	= ElementBounds.Fixed(x, y, w, 58);
			var ButtonSaveConfig	= ElementBounds.Fixed(x + 10, y + 14, 120, 26);
			var ButtonResetConfig	= ElementBounds.Fixed(x + 140, y + 14, 120, 26);
			var ButtonCancelConfig	= ElementBounds.Fixed(x + 270, y + 14, 120, 26);

			BackgroundBounds.WithChildren(
				DescriptionBox, DescriptionTextBox,
				
				MainSettingsBox, VolumeLabel, VolumeSlider, PitchLabel, PitchSlider, RefreshRateLabel, 
				RefreshRateSlider, HearRangeLabel, HearRangeSlider, CPUModeLabel, CPUModeSlider,
				MaxModelsLabel, MaxModelsSlider, AvoidLongsLabel, AvoidLongsToggle, HearSelfLabel, HearSelfToggle,
				
				VoiceSelectionBox, VoiceSelectionLabel, VoiceSelectPrev, VoiceName, VoiceSelectionNext,
				
				ConfigActionsBox, ButtonSaveConfig, ButtonResetConfig, ButtonCancelConfig
			);

			var FontBody		= CairoFont.WhiteSmallishText();
			var FontLabels		= CairoFont.WhiteSmallishText();
			var FontNotes		= CairoFont.WhiteDetailText();
			var FontHeaders		= CairoFont.WhiteMediumText();

			SingleComposer = capi.Gui // capi is correct here, it belongs to the namespace's parent
				.CreateCompo("rptts-settings", DialogueBounds)
				.AddDialogBG(BackgroundBounds, true)
				.AddDialogTitleBar(Lang.Get("rptts:GUI-SettingsTitle"), OnTitleBarClose)
				.BeginChildElements(BackgroundBounds)

					// Card 1
					.AddInset(DescriptionBox, 3)
					.AddRichtext(Lang.Get("rptts:GUI-SettingsDesc"), FontNotes, DescriptionTextBox, "descText")

					// Card 2
					.AddInset(MainSettingsBox, 3)

					.AddStaticText(Lang.Get("rptts:GUI-Volume"), FontLabels, VolumeLabel, "volLabel")
					.AddSlider(OnVolumeChanged, VolumeSlider, "vol")

					.AddStaticText(Lang.Get("rptts:GUI-WPRefreshRate"), FontLabels, RefreshRateLabel, "refLabel")
					.AddSlider(OnRefreshChanged, RefreshRateSlider, "ref")

					.AddStaticText(Lang.Get("rptts:GUI-Pitch"), FontLabels, PitchLabel, "pitchLabel")
					.AddSlider(OnPitchChanged, PitchSlider, "pitch")

					.AddStaticText(Lang.Get("rptts:GUI-HearingRange"), FontLabels, HearRangeLabel, "hearLabel")
					.AddSlider(OnHearingChanged, HearRangeSlider, "hear")
					
					.AddStaticText(Lang.Get("rptts:GUI-CPULevel"), FontLabels, CPUModeLabel, "cpuLabel")
					.AddSlider(OnCPUChanged, CPUModeSlider, "cpu")

					.AddStaticText(Lang.Get("rptts:GUI-SpeakerCount"), FontLabels, MaxModelsLabel, "modelsLabel")
					.AddSlider(OnMaxModelsChanged, MaxModelsSlider, "models")

					.AddStaticText(Lang.Get("rptts:GUI-HearSelf"), FontLabels, HearSelfLabel, "hearSelfLabel")
					.AddToggleButton(Lang.Get(SettingsDraft.HearSelf ? "worldconfig-caveIns-Enabled" : "worldconfig-caveIns-Disabled"),
						FontBody, OnHearSelfToggle, HearSelfToggle, "hearselfToggle")

					.AddStaticText(Lang.Get("rptts:GUI-LongMessages"), FontLabels, AvoidLongsLabel, "avoidLabel")
					.AddToggleButton(Lang.Get(SettingsDraft.AvoidLongMessages ? "worldconfig-caveIns-Enabled" : "worldconfig-caveIns-Disabled"),
						FontBody,OnAvoidToggle,AvoidLongsToggle,"avoidToggle")

					// Card 3
					.AddInset(VoiceSelectionBox, 3)
					.AddStaticText(Lang.Get("rptts:GUI-CharacterVoice"), FontLabels, VoiceSelectionLabel, "voiceHeading")
					.AddSmallButton("<", OnVoicePrev, VoiceSelectPrev, EnumButtonStyle.Normal, "voicePrev")
					.AddDynamicText(Lang.Get("rptts:GUI-VoiceN", SettingsDraft.VoiceID), FontBody, VoiceName, "voiceText")
					.AddSmallButton(">", OnVoiceNext, VoiceSelectionNext, EnumButtonStyle.Normal, "voiceNext")

					// Card 4
					.AddInset(ConfigActionsBox, 3)
					.AddButton(Lang.Get("general-save"), OnSave, ButtonSaveConfig)
					.AddButton(Lang.Get("rptts:GUI-ResetButton"), OnReset, ButtonResetConfig)
					.AddButton(Lang.Get("general-cancel"), OnCancel, ButtonCancelConfig)

				.EndChildElements()
				.Compose();

			// Initialize controls
			int volInt = (int)Math.Round(GameMath.Clamp(SettingsDraft.BaseTTSVolume, 0.5f, 3.5f) * 10f);
			SingleComposer.GetSlider("vol")?.SetValues(volInt, 5, 35, 1, "");

			int pitchInt = (int)Math.Round(GameMath.Clamp(SettingsDraft.PlayerPitch, 0.6f, 1.6f) * 100f);
			SingleComposer.GetSlider("pitch")?.SetValues(pitchInt, 60, 160, 1, "");

			int refVal = GameMath.Clamp(SettingsDraft.PositionRefreshRate, 50, 2000);
			SingleComposer.GetSlider("ref")?.SetValues(refVal, 50, 2000, 50, "");

			int hearVal = GameMath.Clamp(SettingsDraft.HearingRange, 40, 150);
			SingleComposer.GetSlider("hear")?.SetValues(hearVal, 40, 150, 1, "");

			int cpuVal = GameMath.Clamp(SettingsDraft.CPUThreadMode, 0, 2);
			SingleComposer.GetSlider("cpu")?.SetValues(cpuVal, 0, 2, 1, "");

			int modelsVal = GameMath.Clamp(SettingsDraft.MaxIdleModels, 2, 32);
			SingleComposer.GetSlider("models")?.SetValues(modelsVal, 2, 32, 1, "");

			SingleComposer.GetToggleButton("hearselfToggle")?.SetValue(SettingsDraft.HearSelf);
			UpdateHearSelfToggleText(SettingsDraft.HearSelf);

			SingleComposer.GetToggleButton("avoidToggle")?.SetValue(SettingsDraft.AvoidLongMessages);
			UpdateAvoidToggleText(SettingsDraft.AvoidLongMessages);
			ClampVoiceSelection();
		}
		#endregion

		#region Buttons & Controls
		private bool OnSave()
		{
			if (UserConfigReference.CPUThreadMode != SettingsDraft.CPUThreadMode)
			{
				// Warn users that the thread count changes will only happen after restart
				capi.TriggerIngameError(this, "rptts",	Lang.Get("rptts:Error-ThreadCountWarn")); // Primary warning
				capi.ShowChatMessage(					Lang.Get("rptts:Error-ThreadCountWarn")); // Chat log
			}
			if (UserConfigReference.MaxIdleModels != SettingsDraft.MaxIdleModels)
			{
				// Warn about voice count changes
				capi.TriggerIngameError(this, "rptts",	Lang.Get("rptts:Error-SpeakerCountWarn")); // Primary warning
				capi.ShowChatMessage(					Lang.Get("rptts:Error-SpeakerCountWarn")); // Chat log
			}

			// Persist to config
			UserConfigReference.BaseTTSVolume			= SettingsDraft.BaseTTSVolume = GameMath.Clamp(SettingsDraft.BaseTTSVolume, 0.5f, 3.5f);
			UserConfigReference.PositionRefreshRate		= SettingsDraft.PositionRefreshRate = GameMath.Clamp(SettingsDraft.PositionRefreshRate, 50, 2000);
			UserConfigReference.AvoidLongMessages		= SettingsDraft.AvoidLongMessages;
			UserConfigReference.HearSelf				= SettingsDraft.HearSelf;
			UserConfigReference.PlayerPitch				= SettingsDraft.PlayerPitch;
			UserConfigReference.VoiceID					= SettingsDraft.VoiceID;
			UserConfigReference.HearingRange			= SettingsDraft.HearingRange = GameMath.Clamp(SettingsDraft.HearingRange, 40, 150);
			UserConfigReference.CPUThreadMode			= SettingsDraft.CPUThreadMode = GameMath.Clamp(SettingsDraft.CPUThreadMode, 0, 2);
			UserConfigReference.MaxIdleModels			= SettingsDraft.MaxIdleModels = GameMath.Clamp(SettingsDraft.MaxIdleModels, 2, 32);

			// Apply to engine
			KittenDriver.BaseTTSVolume					= UserConfigReference.BaseTTSVolume;
			KittenDriver.PositionRefreshRate			= UserConfigReference.PositionRefreshRate;
			KittenDriver.ForbidLongMessages				= UserConfigReference.AvoidLongMessages;
			KittenDriver.PlayerPitch					= UserConfigReference.PlayerPitch;
			KittenDriver.LocalVoiceID					= UserConfigReference.VoiceID;
			KittenDriver.HearingRange					= UserConfigReference.HearingRange;
			KittenDriver.CPUThreadMode					= UserConfigReference.CPUThreadMode;
			KittenDriver.HearSelf						= UserConfigReference.HearSelf;
			KittenDriver.MaxSpeakers					= UserConfigReference.MaxIdleModels;

			OnSaveSettings?.Invoke(UserConfigReference);
			TryClose();
			return true;
		}

		private bool OnReset()
		{
			SettingsDraft.BaseTTSVolume			= 2.0f;
			SettingsDraft.PositionRefreshRate	= 500;
			SettingsDraft.AvoidLongMessages		= false;
			SettingsDraft.HearSelf				= true;
			SettingsDraft.VoiceID				= 0;
			SettingsDraft.PlayerPitch			= 1f;
			SettingsDraft.HearingRange			= 60;
			SettingsDraft.CPUThreadMode			= 1;
			SettingsDraft.MaxIdleModels			= 4;

			OverwriteAllValues(
				SettingsDraft.BaseTTSVolume, SettingsDraft.PlayerPitch, SettingsDraft.PositionRefreshRate,
				SettingsDraft.AvoidLongMessages, SettingsDraft.VoiceID, SettingsDraft.HearingRange,
				SettingsDraft.CPUThreadMode, SettingsDraft.HearSelf, SettingsDraft.MaxIdleModels
			);
			ApplyDraftToEngine();
			DebouncedVoicePreview();
			ClampVoiceSelection();
			return true;
		}

		private bool OnCancel()
		{
			KittenDriver.BaseTTSVolume					= OriginalVolume;
			KittenDriver.PositionRefreshRate 			= OriginalRefreshRate;
			KittenDriver.LocalVoiceID					= OriginalVoice;
			KittenDriver.ForbidLongMessages				= OriginalAvoid;
			KittenDriver.PlayerPitch					= OriginalPitch;
			KittenDriver.HearingRange					= OriginalHearingRange;
			KittenDriver.CPUThreadMode					= OriginalCPUMode;
			KittenDriver.HearSelf						= OriginalHearSelf;
			KittenDriver.MaxSpeakers					= OriginalMaxModels;

			SettingsDraft.BaseTTSVolume					= OriginalVolume;
			SettingsDraft.PositionRefreshRate			= OriginalRefreshRate;
			SettingsDraft.VoiceID						= OriginalVoice;
			SettingsDraft.AvoidLongMessages				= OriginalAvoid;
			SettingsDraft.PlayerPitch					= OriginalPitch;
			SettingsDraft.HearingRange					= OriginalHearingRange;
			SettingsDraft.CPUThreadMode					= OriginalCPUMode;
			SettingsDraft.HearSelf						= OriginalHearSelf;
			SettingsDraft.MaxIdleModels					= OriginalMaxModels;

			OverwriteAllValues
			(	OriginalVolume, OriginalPitch, OriginalRefreshRate, 
				OriginalAvoid, OriginalVoice, OriginalHearingRange, 
				OriginalCPUMode, OriginalHearSelf, OriginalMaxModels
			);
			TryClose(); return true;
		}

		private void OnTitleBarClose() => OnCancel();

		private bool OnVolumeChanged(int value)
		{
			SettingsDraft.BaseTTSVolume = GameMath.Clamp(value / 10f, 0.5f, 3.5f);
			KittenDriver.BaseTTSVolume = SettingsDraft.BaseTTSVolume;
			DebouncedVoicePreview();
			return true;
		}

		private bool OnPitchChanged(int value)
		{
			SettingsDraft.PlayerPitch = GameMath.Clamp(value / 100f, 0.5f, 1.8f);
			KittenDriver.PlayerPitch = SettingsDraft.PlayerPitch;
			DebouncedVoicePreview();
			return true;
		}

		private bool OnRefreshChanged(int value)
		{
			SettingsDraft.PositionRefreshRate = GameMath.Clamp(value, 50, 2000);
			KittenDriver.PositionRefreshRate = SettingsDraft.PositionRefreshRate;
			return true;
		}

		private bool OnHearingChanged(int value)
		{
			SettingsDraft.HearingRange = GameMath.Clamp(value, 40, 150);
			KittenDriver.HearingRange = SettingsDraft.HearingRange;
			return true;
		}

		private bool OnCPUChanged(int value)
		{
			SettingsDraft.CPUThreadMode = GameMath.Clamp(value, 0, 2);
			KittenDriver.CPUThreadMode = SettingsDraft.CPUThreadMode;
			return true;
		}

		private bool OnMaxModelsChanged(int value)
		{
			SettingsDraft.MaxIdleModels = GameMath.Clamp(value, 2, 32);
			KittenDriver.MaxSpeakers = SettingsDraft.MaxIdleModels;
			return true;
		}

		private void OnAvoidToggle(bool value)
		{
			SettingsDraft.AvoidLongMessages = value;
			KittenDriver.ForbidLongMessages = value;
			UpdateAvoidToggleText(value);
		}

		private void OnHearSelfToggle(bool value)
		{
			SettingsDraft.HearSelf = value;
			KittenDriver.HearSelf = value;
			UpdateHearSelfToggleText(value);
		}

		private void UpdateAvoidToggleText(bool value)
		{
			var toggle = SingleComposer?.GetToggleButton("avoidToggle"); if (toggle == null) { return; }
			
			toggle.Text = Lang.Get(value ? "rptts:GUI-EnabledState" : "rptts:GUI-DisabledState");
			SingleComposer?.ReCompose();
		}

		private void UpdateHearSelfToggleText(bool value)
		{
			var toggle = SingleComposer?.GetToggleButton("hearselfToggle"); if (toggle == null) { return; }
			toggle.Text = Lang.Get(value ? "rptts:GUI-EnabledState" : "rptts:GUI-DisabledState");
			SingleComposer?.ReCompose();
		}

		private bool OnVoicePrev()
		{
			SettingsDraft.VoiceID = Math.Max(0, SettingsDraft.VoiceID - 1);
			UpdateVoiceWidgetsAndPreview();
			ClampVoiceSelection();
			return true;
		}

		private bool OnVoiceNext()
		{
			++SettingsDraft.VoiceID;
			UpdateVoiceWidgetsAndPreview();
			ClampVoiceSelection();
			return true;
		}
		#endregion

		#region Helpers
		private void OverwriteAllValues
		(
			float Volume, float Pitch, int RefreshRate, bool AvoidLongs, 
			int VoiceID, int HearingRange, int CPUMode, bool HearSelf, int VoiceCount
		)
		{
			SingleComposer.GetSlider("vol")						?.SetValues((int)Math.Round(Volume * 10f), 5, 35, 1, "");
			SingleComposer.GetSlider("pitch")					?.SetValues((int)Math.Round(Pitch * 100f), 60, 160, 1, "");
			SingleComposer.GetSlider("ref")						?.SetValues(RefreshRate, 50, 2000, 50, "");
			SingleComposer.GetSlider("hear")					?.SetValues(HearingRange, 40, 150, 1, "");
			SingleComposer.GetSlider("cpu")						?.SetValues(CPUMode, 0, 2, 1, "");
			SingleComposer.GetToggleButton("avoidToggle")		?.SetValue(AvoidLongs);
			SingleComposer.GetToggleButton("hearselfToggle")	?.SetValue(HearSelf);
			SingleComposer.GetDynamicText("voiceText")			?.SetNewText(Lang.Get("rptts:GUI-VoiceN", VoiceID));
			SingleComposer.GetSlider("models")					?.SetValues(VoiceCount, 2, 32, 1, "");

			UpdateAvoidToggleText(AvoidLongs);
			UpdateHearSelfToggleText(HearSelf);
		}

		private void ClampVoiceSelection()
		{
			var prev = SingleComposer?.GetButton("voicePrev");
			var next = SingleComposer?.GetButton("voiceNext");
			if (prev == null || next == null) { return; } // Shutup, vscode

			prev.Enabled = true;
			next.Enabled = true;	
			if		(SettingsDraft.VoiceID <= 0)								{ prev.Enabled = false; }
			else if	(SettingsDraft.VoiceID >= KittenDriver.VoicesAvailable)		{ next.Enabled = false; }
		}

		private void UpdateVoiceWidgetsAndPreview()
		{
			SingleComposer.GetDynamicText("voiceText")?.SetNewText(Lang.Get("rptts:GUI-VoiceN", SettingsDraft.VoiceID));
			KittenDriver.LocalVoiceID = SettingsDraft.VoiceID;
			DebouncedVoicePreview();
		}

		private void ApplyDraftToEngine()
		{
			KittenDriver.BaseTTSVolume				= SettingsDraft.BaseTTSVolume;
			KittenDriver.PositionRefreshRate		= SettingsDraft.PositionRefreshRate;
			KittenDriver.LocalVoiceID				= SettingsDraft.VoiceID;
			KittenDriver.ForbidLongMessages			= SettingsDraft.AvoidLongMessages;
			KittenDriver.PlayerPitch				= SettingsDraft.PlayerPitch;
			KittenDriver.HearingRange				= SettingsDraft.HearingRange;
			KittenDriver.CPUThreadMode				= SettingsDraft.CPUThreadMode;
			KittenDriver.HearSelf					= SettingsDraft.HearSelf;
			KittenDriver.MaxSpeakers				= SettingsDraft.MaxIdleModels;
		}

		private void DebouncedVoicePreview()
		{
			int activenonce = ++VoicePreviewNonce;
			capi.Event.RegisterCallback(_ =>
			{
				if (activenonce != VoicePreviewNonce) return;
				try { KittenDriver.Speak("This is my voice!", SettingsDraft.VoiceID, SettingsDraft.PlayerPitch, null); }
				catch (Exception ex) { capi.Logger.Warning("[rptts] [ERROR] Voice preview failed: {0}", ex); }
			}, 450);
		}
		#endregion
	}
}
