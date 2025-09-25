# Roleplay Text to Speech Cross-Mod Compatibility Guide
Hello and welcome to the cross-mod compatibility guide for RPTTS.<br>
RPTTS is a simple mod in scope, and comes with a dedicated API that you can make use of through dynamic bindings (aka dynamic dispatch), this should make it trivial for most mods to support RPTTS.

My goal is to make it as easy and simple as possible to have access to all the features of RPTTS, while leaving the details and control over the implementation within the confines of your mod entirely in your hands.

-- YangWenLi

## Referencing RPTTS
In your mod, at the start of its lifecycle, perform the following somewhere accessible to the rest of your code:
```cs
// Class defined vars
public bool UsingRPTTS; // Boolean used to gate IF RPTTS is present
public dynamic? RPTTSAPI; // Actual reference to RPTTS' API

// Somewhere at the start of your lifecycle, before your calls to RPTTS are expected, eg StartClientSide
UsingRPTTS = api.ModLoader.IsModSystemEnabled("RPTTS.RPTTSAPI");
if (UsingRPTTS) { RPTTSAPI = api.ModLoader.GetModSystem("RPTTS.RPTTSAPI"); }
```
With this, you'll hyave a boolean to verify that RPTTS is present (and can gate logic based on that), as well as a reference to the API itself.<br>
Now you can go straight to using RPTTS by calling our API. Simple, wasn't it?

Keep in mind that if you screw up with a dynamic binding, it can cause crashes. Make sure you get the names right and gate logic appropriately.

## Using RPTTS
The only thing necessary to make a character speak is to call `APIChatMessage` on their client with the text you want them to speak, all other args are optional.

Keep in mind:
1. **Text processing/clean-up is your responsibility,** if you have the following string `/speak hello world` and want it to be spoken as `hello world`, you'll need to figure out the removal of the `/speak` segment of the string yourself. This should be fastest and easiest to do within the confines of your mod's code before it gets passed onto our API.
2. **The call must be made from and for the client speaking.** Under no other context should you attempt to make a player speak. Each client should be in charge of calling `APIChatMessage` for themselves. Remote clients (or the server) should not attempt to make other clients speak.

Here are the arguments you can pass to `APIChatMessage`:
1. `string` The text that you want a client to synthesize
2. `float?` A multiplier for the loudness of the audio (math: `final loudness = (PCM raw loudness * gain multiplier) * client loudness setting`)
3. `float?` Falloff modifier, used to increase the falloff curve, which decreases the effective range at which audio is heard

RPTTS' API puts a lot of trust in developers, so there's little in the way of safety checks, nor are they too strict. Use this power wisely.<br>
Keep in mind not to confuse the permitted range for values with the expected range.
```cs
// Basic Example | This is how all messages are processed by default in RPTTS, no frills, uses the default settings that Yang lovingly set
RPTTSAPI?.APIChatMessage(text) // From the client speaking, send the string of text to be said like this

// Gain Modulation | Multiplier for the loudness gain value (0.2f - 2.25f) (slightly stringer clamp for local 2D playback)
RPTTSAPI?.APIChatMessage(text, 1.5f) // Yelling		| Keep in mind that this value alters the PCM, with the result then still being multiplied
RPTTSAPI?.APIChatMessage(text, 0.6f) // Whispering	| yet again by the user's settings. So loudness is ultimately relative to player taste.

// Falloff Modifier | How far away audio can be heard from before becoming inaudible (1f - 8f) (60 blocks to 7.5 blocks)
RPTTSAPI?.APIChatMessage(text, null, 2.5f) // Falloff modifier, x2.5 times shorter distance
```
Keep in mind that messages that start with `/ . ! *` will be purged to prevent commands or flavour text from being spoken.<br>
If for whatever reason you wish to make such things audible, add a space at the start of your string to bypass this check.

Here are some full usage examples:
```cs
// Simple Example
private void SendRPMessage(string message) // This is from your mod, wherever you make or recieve a string to be sent in chat
{
	// [...] Your irrelevant code

	if (UsingRPTTS) { RPTTSAPI?.APIChatMessage(message); }
}

// Less Simple Example
private void OnSendRPMessageWithLoudness(string message, enum loudness) // How fancy of your mod to use enums for a state machine!
{
	if (UsingRPTTS)
	{
		// this could be a bool, enum, or float, or whatever you want to use to divide up the modes of speaking
		if		(loudness == speakingmodes.yelling)		{ RPTTSAPI?.APIChatMessage(message, 1.45f); }
		else if	(loudness == speakingmodes.whispering)	{ RPTTSAPI?.APIChatMessage(message, 0.65f, 5f); }
		else											{ RPTTSAPI?.APIChatMessage(message); }
	}
}
```

## Enforcing Explicit Calls
If you wish to stop RPTTS from listening for new chat messages and automatically reading them out loud,<br>
for example, if your mod moves the chat into a new tab and you don't want "OOC" general chat messages to be read out loud, you can do the following:
```cs
if (UsingRPTTS) { RPTTSAPI?.RPTTSMain.OverwriteChatSubscription(false); } // False as in set the subscription to false
```
Keep in mind that players can still overwrite this behaviour via the `ttsexplicit` command.

## Complete Mod Example
Below is the entire source code for a bare-bones but complete mod that uses the RPTTS API.
```cs
// https://github.com/HugoCortell/vs_rptts/tree/main/api_example_mod
using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace RPTTSApiTest
{
	public sealed class RPTTSApiTestSystem : ModSystem
	{
		private ICoreClientAPI capi = null!;
		public bool UsingRPTTS;
		public dynamic? RPTTSAPI;

		public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

		public override void StartClientSide(ICoreClientAPI api)
		{
			capi = api;
			UsingRPTTS = api.ModLoader.IsModSystemEnabled("RPTTS.RPTTSAPI");
			if (UsingRPTTS) { RPTTSAPI = api.ModLoader.GetModSystem("RPTTS.RPTTSAPI"); }

			capi.ChatCommands
				.Create("testrpttsapi")
				.HandleWith(args =>
				{
					if (UsingRPTTS)
					{
						RPTTSAPI?.APIChatMessage("hello world");
						return TextCommandResult.Success();
					}
					return TextCommandResult.Error("Didn't work.");
				}
			);

			capi.ChatCommands
				.Create("testrpttsapi2")
				.HandleWith(args =>
				{
					if (UsingRPTTS)
					{
						RPTTSAPI?.APIChatMessage("hello world I am shouting", 2f);
						return TextCommandResult.Success();
					}
					return TextCommandResult.Error("Didn't work.");
				}
			);

			capi.ChatCommands
				.Create("testrpttsapi3")
				.HandleWith(args =>
				{
					if (UsingRPTTS)
					{
						RPTTSAPI?.APIChatMessage("hello world I am whispering", 0.5f, 8f);
						return TextCommandResult.Success();
					}
					return TextCommandResult.Error("Didn't work.");
				}
			);
		}
	}
}
```