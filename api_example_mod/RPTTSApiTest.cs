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
