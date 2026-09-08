#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace IntegratedModManager.Config;

public sealed class ImmConfigService
{
	private readonly ICoreServerAPI Api;
	private readonly ImmConfigRegistry Registry;
	private readonly ImmDependencyService Dependencies;
	private readonly ImmPatchSettingsStore PatchSettings;
	private readonly ImmContentPatchService ContentPatches;
	private readonly ImmExternalManagerOwnership ExternalManagers;

	public ImmConfigService(ICoreServerAPI api, ImmConfigRegistry registry, ImmDependencyService dependencies, ImmPatchSettingsStore patchSettings, ImmContentPatchService contentPatches, ImmExternalManagerOwnership externalManagers)
	{
		Api = api;
		Registry = registry;
		Dependencies = dependencies;
		PatchSettings = patchSettings;
		ContentPatches = contentPatches;
		ExternalManagers = externalManagers;
	}

	public ImmConfigCatalogResponse BuildCatalog(IServerPlayer player)
	{
		bool canManageServer = player.HasPrivilege(Privilege.controlserver);

		ImmServerModPacket[] mods = Api.ModLoader.Mods
			.Where(mod => !string.IsNullOrWhiteSpace(mod.Info?.ModID))
			.Select(mod =>
			{
				string modId = mod.Info!.ModID;
				bool hasDescriptor = Registry.TryGet(modId, out ImmConfigDescriptor descriptor);
				bool hasConfiguration = hasDescriptor && (canManageServer ? descriptor.Configuration.Count > 0 : ImmConfigPacketFactory.HasConfigurationForSide(descriptor, ImmConfigSide.Client));
				ImmActiveDependency[] active = canManageServer ? Dependencies.GetForMod(modId) : Array.Empty<ImmActiveDependency>();

				return new ImmServerModPacket
				{
					ModId = modId,
					Name = mod.Info?.Name ?? modId,
					HasConfiguration = hasConfiguration,
					WarningCount = active.Count(dependency => dependency.Severity == ImmDependencySeverity.Warning),
					ErrorCount = active.Count(dependency => dependency.Severity == ImmDependencySeverity.Error)
				};
			})
			.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new ImmConfigCatalogResponse { Success = true, CanManageServer = canManageServer, Mods = mods };
	}

	public ImmConfigPageResponse BuildPage(IServerPlayer player, ImmConfigPageRequest request)
	{
		bool canManageServer = player.HasPrivilege(Privilege.controlserver);

		if (!Registry.TryGet(request.ModId, out ImmConfigDescriptor descriptor)) { return PageError(request.ModId, "integratedmodmanager:error-no-descriptor"); }

		bool externallyManaged = ExternalManagers.IsControlled(request.ModId);

		if (externallyManaged)
		{
			return new ImmConfigPageResponse
			{
				Success = true,
				CanManageServer = canManageServer,
				ModId = request.ModId,
				Configuration = Array.Empty<ImmConfigBlockPacket>(),
				Dependencies = canManageServer ? BuildDependencyPackets(request.ModId) : Array.Empty<ImmDependencyPacket>(),
				ConfigurationExternallyManaged = true,
				ExternalManagerActive = ExternalManagers.AnyManagerActive,
				ExternalManagerName = ExternalManagers.GetPrimaryManagerName(Api, request.ModId)
			};
		}

		if (!canManageServer && !ImmConfigPacketFactory.HasConfigurationForSide(descriptor, ImmConfigSide.Client)) { return PageError(request.ModId, "integratedmodmanager:error-no-client-configuration"); }

		Dictionary<string, JObject?> serverConfigs = new(StringComparer.OrdinalIgnoreCase);
		JObject patchSettingsDocument = canManageServer ? PatchSettings.ReadDocument(request.ModId) : new JObject();

		int globalIndex = 0;
		List<ImmConfigBlockPacket> blocks = new();

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
		{
			ImmConfigBlock block = descriptor.Configuration[blockIndex];

			JObject? config = null;
			string configError = "";

			if (canManageServer && block.ConfigSource == ImmConfigSource.ModConfig)
			{
				bool needsServerConfig = block.ConfigSide == ImmConfigSide.Server || block.Settings.Any(entry => (entry.ConfigSide ?? block.ConfigSide) == ImmConfigSide.Server);

				if (needsServerConfig) { TryGetServerConfig(block.ConfigFile, serverConfigs, out config, out configError); }
			}

			string blockDescription = block.Description ?? "";

			if (canManageServer && block.ConfigSource == ImmConfigSource.ModConfig && block.ParseDescriptions && block.ConfigSide == ImmConfigSide.Server && config != null) { blockDescription = ReadRootDescription(config); }

			List<ImmConfigControlPacket> controls = new();

			for (int entryIndex = 0; entryIndex < block.Settings.Count; entryIndex++)
			{
				ImmConfigEntry entry = block.Settings[entryIndex];
				ImmConfigSide effectiveSide = entry.ConfigSide ?? block.ConfigSide;

				int controlIndex = globalIndex++;

				if (!canManageServer && effectiveSide != ImmConfigSide.Client) { continue; }

				ImmConfigControlPacket control = ImmConfigPacketFactory.CreateControlPacket(controlIndex, blockIndex, block, entry, effectiveSide);

				if (canManageServer && effectiveSide == ImmConfigSide.Server)
				{
					if (block.ConfigSource == ImmConfigSource.PatchSettings)
					{
						JToken savedValue = PatchSettings.GetEffectiveValue(request.ModId, entry, effectiveSide, patchSettingsDocument);

						PopulatePatchSettingControl(request.ModId, entry, control, effectiveSide, savedValue);
					}
					else if (config == null)
					{
						control.Available = false;
						control.UnavailableReason = configError;
					}
					else { PopulateControl(block, control, config); }
				}

				controls.Add(control);
			}

			if (canManageServer || controls.Count > 0)
			{
				ImmConfigBlockPacket blockPacket = ImmConfigPacketFactory.CreateBlockPacket(blockIndex, block, controls.ToArray());

				blockPacket.Description = blockDescription;
				blocks.Add(blockPacket);
			}
		}

		return new ImmConfigPageResponse { Success = true, CanManageServer = canManageServer, ModId = request.ModId, Configuration = blocks.ToArray(), Dependencies = canManageServer ? BuildDependencyPackets(request.ModId) : Array.Empty<ImmDependencyPacket>(), ExternalManagerActive = ExternalManagers.AnyManagerActive };
	}

	public ImmConfigApplyResponse Apply(IServerPlayer player, ImmConfigApplyRequest request)
	{
		if (!player.HasPrivilege(Privilege.controlserver)) { return ApplyError(request.ModId, "You do not have permission to manage server configuration."); }
		if (!Registry.TryGet(request.ModId, out ImmConfigDescriptor descriptor)) { return ApplyError(request.ModId, "This mod does not provide an IMM configuration descriptor."); }
		if (ExternalManagers.IsControlled(request.ModId)) { return ApplyError(request.ModId, "Configuration for this mod is controlled by another mod manager."); }

		Dictionary<int, ServerControlLocation> controls = BuildServerControlIndex(descriptor);
		Dictionary<string, JObject?> configs = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
		HashSet<int> seenIndexes = new();
		List<PreparedServerChange> preparedConfigChanges = new();
		List<PreparedPatchSettingChange> preparedPatchChanges = new();
		JObject patchSettingsDocument = PatchSettings.ReadDocument(request.ModId);

		foreach (ImmConfigChangePacket change in request.Changes ?? Array.Empty<ImmConfigChangePacket>())
		{
			if (!seenIndexes.Add(change.Index) || !controls.TryGetValue(change.Index, out ServerControlLocation location)) { return ApplyError(request.ModId, $"Configuration index {change.Index} is invalid."); }
			if (location.Control.ConfigSide != ImmConfigSide.Server) { return ApplyError(request.ModId, $"{location.Control.Label}: setting is not server-owned."); }

			JToken submitted;

			try { submitted = JToken.Parse(change.ValueJson); }
			catch { return ApplyError(request.ModId, $"{location.Control.Label}: submitted value was not valid JSON."); }

			if (location.Block.ConfigSource == ImmConfigSource.PatchSettings)
			{
				if (location.Entry.Default == null) { return ApplyError(request.ModId, $"{location.Control.Label}: PatchSetting has no Default."); }
				if (!ImmConfigValueValidator.TryNormalizeValue(location.Control, location.Entry.Default, submitted, out JToken normalized, out string validationError)) { return ApplyError(request.ModId, $"{location.Control.Label}: {validationError}"); }

				JToken current = PatchSettings.GetEffectiveValue(request.ModId, location.Entry, ImmConfigSide.Server, patchSettingsDocument);
				if (!JToken.DeepEquals(current, normalized)) { preparedPatchChanges.Add(new PreparedPatchSettingChange(location.Entry, ImmConfigSide.Server, normalized)); }

				continue;
			}

			if (!TryGetServerConfig(location.Block.ConfigFile, configs, out JObject? config, out string configError) || config == null) { return ApplyError(request.ModId, configError); }

			JToken? target;

			try { target = config.SelectToken(location.Control.Map); }
			catch (Exception exception) { return ApplyError(request.ModId, $"{location.Control.Label}: invalid Map ({exception.Message})."); }

			if (target == null) { return ApplyError(request.ModId, $"{location.Control.Label}: mapped config value was not found."); }

			if (!ImmConfigValueValidator.TryNormalizeValue(location.Control, target, submitted, out JToken normalizedConfig, out string configValidationError)) { return ApplyError(request.ModId, $"{location.Control.Label}: {configValidationError}"); }
			if (JToken.DeepEquals(target, normalizedConfig)) { continue; }

			preparedConfigChanges.Add(new PreparedServerChange(target, normalizedConfig));
			changedFiles.Add(location.Block.ConfigFile);
		}

		try
		{
			foreach (PreparedServerChange change in preparedConfigChanges) { change.Target.Replace(change.Value); }
			ImmAtomicFileBatch batch = new();

			foreach (string configFile in changedFiles)
			{
				if (configs.TryGetValue(configFile, out JObject? config) && config != null) { batch.Write(Path.Combine(Api.DataBasePath, "ModConfig", configFile.Replace('/', Path.DirectorySeparatorChar)), config.ToString(Formatting.Indented)); }
			}

			bool committed;
			string saveError;

			if (preparedPatchChanges.Count > 0) { committed = PatchSettings.TryCommitWithOverrides(request.ModId, ImmConfigSide.Server, preparedPatchChanges.Select(change => (change.Entry, change.Value)).ToArray(), batch, out saveError); }
			else { committed = batch.TryCommit(out saveError); }

			if (!committed) { return ApplyError(request.ModId, saveError); }
		}
		catch (Exception exception) { return ApplyError(request.ModId, $"Failed to prepare configuration: {exception.Message}"); }

		if (preparedConfigChanges.Count > 0) { Api.Event.PushEvent(ImmConfigBroadcast.GetEventName(request.ModId)); }
		if (preparedConfigChanges.Count > 0 || preparedPatchChanges.Count > 0) { Dependencies.EvaluateAll(); }

		return new ImmConfigApplyResponse { Success = true, ModId = request.ModId, ReloadRequirement = ContentPatches.HasPendingReload(request.ModId, ImmConfigSide.Server) ? ImmReloadRequirement.ServerRestart : ImmReloadRequirement.None };
	}

	public ImmDependencyResolveResponse Resolve(IServerPlayer player, ImmDependencyResolveRequest request)
	{
		if (!player.HasPrivilege(Privilege.controlserver)) { return new ImmDependencyResolveResponse { RuntimeId = request.RuntimeId, Error = "You do not have permission to resolve dependency issues." }; }

		bool success = Dependencies.TryResolve(request.RuntimeId, out string chatCommand, out ImmDependencyResolutionWarning warning, out string error);
		return new ImmDependencyResolveResponse { Success = success, Error = error, RuntimeId = request.RuntimeId, RestartRequired = success, ChatCommand = success ? chatCommand : "", Warning = success ? warning : ImmDependencyResolutionWarning.None };
	}

	private ImmDependencyPacket[] BuildDependencyPackets(string sourceModId)
	{
		List<ImmDependencyPacket> packets = new();

		foreach (ImmActiveDependency active in Dependencies.GetForMod(sourceModId))
		{
			if (!Registry.TryGetDependency(active.RuntimeId, out ImmRegisteredDependency registered)) { continue; }

			ImmDependencyEntry entry = registered.Entry;
			ImmDependencyResolution? resolution = entry.Resolution;

			(string resolutionKey, string[] resolutionArgs) = resolution == null ? ("", Array.Empty<string>()) : BuildResolutionLocalization(resolution);
			packets.Add(new ImmDependencyPacket { RuntimeId = active.RuntimeId, Label = entry.Label, Description = entry.Description ?? "", Severity = active.Severity, HasResolution = resolution != null, ResolutionType = resolution?.Type ?? ImmDependencyResolutionType.Unknown, ResolutionDescription = "", ResolutionDescriptionKey = resolutionKey, ResolutionDescriptionArgs = resolutionArgs });
		}

		return packets.ToArray();
	}

	private static (string Key, string[] Args)
		BuildResolutionLocalization(ImmDependencyResolution resolution)
	{
		switch (resolution.Type)
		{
			case ImmDependencyResolutionType.SetSetting:
				ImmDependencySettingTarget target = resolution.Target!;

				string value = resolution.Value!.ToString(Formatting.None);

				if (!string.IsNullOrWhiteSpace(target.PatchSetting)) { string targetName = string.IsNullOrWhiteSpace(target.ModId) ? target.PatchSetting : $"{target.ModId}:{target.PatchSetting}"; return ("resolution-set-patchsetting", new[] { targetName, value }); }

			return ("resolution-set-modconfig", new[] { target.Map, target.ConfigFile, value });

			case ImmDependencyResolutionType.InstallMod: return ("resolution-install-mod", new[] { resolution.ModId });

			case ImmDependencyResolutionType.RunCommand:
				string command = resolution.Value!.Value<string>() ?? "";

				string key = command.StartsWith(".", StringComparison.Ordinal) ? "resolution-run-client-command" : command.StartsWith("/", StringComparison.Ordinal) ? "resolution-run-server-command" : "resolution-run-chat-message";

			return (key, new[] { command });

			default: return ("", Array.Empty<string>());
		}
	}

	private void PopulatePatchSettingControl(string modId, ImmConfigEntry entry, ImmConfigControlPacket control, ImmConfigSide side, JToken savedValue)
	{
		if (!ImmConfigValueValidator.TryValidateCurrentValue(control, savedValue, out string validationError))
		{
			control.Available = false;
			control.UnavailableReason = validationError;
			return;
		}

		control.Available = true;
		control.UnavailableReason = "";
		control.ValueJson = savedValue.ToString(Formatting.None);

		control.PendingReload = ContentPatches.IsPendingReload(modId, entry, side, savedValue);
	}

	private static void PopulateControl(ImmConfigBlock block, ImmConfigControlPacket control, JObject config)
	{
		JToken? target;

		try { target = config.SelectToken(control.Map); }
		catch (Exception exception)
		{
			control.Available = false;
			control.UnavailableReason = $"Invalid Map: {exception.Message}";
			return;
		}

		if (target == null)
		{
			control.Available = false;
			control.UnavailableReason = "Mapped config value was not found.";
			return;
		}

		if (!ImmConfigValueValidator.TryValidateCurrentValue(control, target, out string validationError))
		{
			control.Available = false;
			control.UnavailableReason = validationError;
			return;
		}

		control.Available = true;
		control.ValueJson = target.ToString(Formatting.None);

		if (block.ParseDescriptions) { control.Description = ReadSettingDescription(target); }
	}

	private bool TryGetServerConfig(string configFile, Dictionary<string, JObject?> configs, out JObject? config, out string error)
	{
		if (configs.TryGetValue(configFile, out config)) { error = config == null ? $"Config file '{configFile}' was not found." : ""; return config != null; }

		try
		{
			config = Api.LoadModConfig<JObject>(configFile);

			configs[configFile] = config;

			if (config == null) { error = $"Config file '{configFile}' was not found."; return false; }

			error = "";
			return true;
		}
		catch (Exception exception)
		{
			config = null;
			configs[configFile] = null;
			error = $"Failed to read '{configFile}': {exception.Message}";
			return false;
		}
	}

	private static Dictionary<int, ServerControlLocation>
		BuildServerControlIndex(ImmConfigDescriptor descriptor)
	{
		Dictionary<int, ServerControlLocation> result = new();

		int globalIndex = 0;

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
		{
			ImmConfigBlock block = descriptor.Configuration[blockIndex];

			foreach (ImmConfigEntry entry in block.Settings)
			{
				ImmConfigSide effectiveSide = entry.ConfigSide ?? block.ConfigSide;

				ImmConfigControlPacket control = ImmConfigPacketFactory.CreateControlPacket(globalIndex, blockIndex, block, entry, effectiveSide);

				result[globalIndex] = new ServerControlLocation(block, entry, control);

				globalIndex++;
			}
		}

		return result;
	}

	private static string ReadRootDescription(JObject config) { return config.TryGetValue("Description", StringComparison.OrdinalIgnoreCase, out JToken? description) && description.Type == JTokenType.String ? description.Value<string>() ?? "" : ""; }

	private static string ReadSettingDescription(JToken target)
	{
		if (target.Parent is not JProperty property || property.Parent is not JObject parent) { return ""; }

		string descriptionName = property.Name + "Description";

		return parent.TryGetValue(descriptionName, StringComparison.OrdinalIgnoreCase, out JToken? description) && description.Type == JTokenType.String ? description.Value<string>() ?? "" : "";
	}

	private static ImmConfigPageResponse PageError(string modId, string error) { return new ImmConfigPageResponse { ModId = modId, Error = error }; }
	private static ImmConfigApplyResponse ApplyError(string modId, string error) { return new ImmConfigApplyResponse { ModId = modId, Error = error }; }
	private sealed record ServerControlLocation(ImmConfigBlock Block, ImmConfigEntry Entry, ImmConfigControlPacket Control);
	private sealed record PreparedServerChange(JToken Target, JToken Value);
	private sealed record PreparedPatchSettingChange(ImmConfigEntry Entry, ImmConfigSide Side, JToken Value);
}
