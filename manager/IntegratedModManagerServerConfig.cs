#nullable enable

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace IntegratedModManager.Config;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmNudgeBehaviour
{
	FirstTime,
	WhenErrorsFound,
	WarningsOrErrors
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmImportantInformationHighlight
{
	Pulsating,
	Flat,
	Disabled
}


[JsonConverter(typeof(StringEnumConverter))]
public enum ImmUpdateManagementMode
{
	Manual,
	AutomaticCheck,
	AutomaticUpdate
}

public sealed class IntegratedModManagerConfig
{
	public const string FileName = "integratedmodmanager/integratedmodmanager.json";

	private static ICoreClientAPI? Api;
	private static IntegratedModManagerConfig Current = new();

	public bool ShowNonConfigurableMods = false;
	public int ModSelectorRows = 3;
	public ImmNudgeBehaviour NudgeBehaviour = ImmNudgeBehaviour.WhenErrorsFound;
	public ImmImportantInformationHighlight ImportantInformationHighlight = ImmImportantInformationHighlight.Pulsating;
	public ImmUpdateManagementMode UpdateManagement = ImmUpdateManagementMode.Manual;

	public static bool ShouldShowNonConfigurableMods => Current.ShowNonConfigurableMods;

	public static int SelectorRows => Math.Clamp(Current.ModSelectorRows, 1, 6);

	public static ImmNudgeBehaviour ConfiguredNudgeBehaviour => Current.NudgeBehaviour;

	public static ImmImportantInformationHighlight ConfiguredInformationHighlight => Current.ImportantInformationHighlight;

	public static IntegratedModManagerConfig LoadOrCreate(ICoreAPI api)
	{
		try
		{
			IntegratedModManagerConfig config = api.LoadModConfig<IntegratedModManagerConfig>(FileName) ?? new IntegratedModManagerConfig();

			config.ModSelectorRows = Math.Clamp(config.ModSelectorRows, 1, 6);
			api.StoreModConfig(config, FileName);

			return config;
		}
		catch (Exception exception)
		{
			api.Logger.Error("[integratedmodmanager] Failed to initialize IMM config: {0}", exception.Message);
			return new IntegratedModManagerConfig();
		}
	}

	public static void Start(ICoreClientAPI api)
	{
		Stop();

		Api = api;
		EnsureExists(api);
		Reload();

		api.Event.RegisterEventBusListener(OnConfigChanged, filterByEventName: ImmConfigBroadcast.GetEventName("integratedmodmanager"));
	}

	public static void Stop()
	{
		if (Api == null) { return; }

		Api.Event.UnregisterEventBusListener(OnConfigChanged);

		Api = null;
		Current = new IntegratedModManagerConfig();
	}

	private static void OnConfigChanged(string eventName, ref EnumHandling handling, IAttribute data) { Reload(); }

	private static void Reload()
	{
		if (Api == null) { return; }

		try { Current = Api.LoadModConfig<IntegratedModManagerConfig>(FileName) ?? new IntegratedModManagerConfig(); }
		catch (Exception exception) { Api.Logger.Error("[integratedmodmanager] Failed to reload client config: {0}", exception.Message); }
	}

	private static void EnsureExists(ICoreClientAPI api) { LoadOrCreate(api); }
}
