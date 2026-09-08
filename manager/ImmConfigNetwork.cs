#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace IntegratedModManager.Config;

public sealed class ImmConfigClient : IDisposable
{
	private readonly ICoreClientAPI ClientApi;
	private readonly IClientNetworkChannel Channel;

	private readonly ImmContentPatchCoordinatorSystem? PatchCoordinator;

	private readonly HashSet<string> ServerPageModIds = new(StringComparer.OrdinalIgnoreCase);

	private bool CanManageServer;

	public event Action<ImmConfigCatalogResponse>? CatalogReceived;
	public event Action<ImmConfigPageResponse>? PageReceived;
	public event Action<ImmConfigApplyResponse>? ApplyReceived;
	public event Action<ImmDependencyResolveResponse>? DependencyResolveReceived;

	public ImmConfigClient(ICoreClientAPI clientApi)
	{
		ClientApi = clientApi;

		PatchCoordinator = clientApi.ModLoader.GetModSystem<ImmContentPatchCoordinatorSystem>();
		Channel = clientApi.Network.GetChannel(ImmConfigNetwork.NetworkChannelCode).SetMessageHandler<ImmConfigCatalogResponse>(OnCatalogReceived).SetMessageHandler<ImmConfigPageResponse>(OnPageReceived).SetMessageHandler<ImmConfigApplyResponse>(OnApplyReceived).SetMessageHandler<ImmDependencyResolveResponse>(OnDependencyResolveReceived);
	}

	public void RequestCatalog() { Channel.SendPacket(new ImmConfigCatalogRequest()); }

	public void RequestPage(string modId)
	{
		if (!ServerPageModIds.Contains(modId) && TryBuildLocalClientPage(modId, out ImmConfigPageResponse localPage)) { ClientApi.Event.EnqueueMainThreadTask(() => FinalizePage(localPage), "integratedmodmanager-local-config-page"); return; }

		Channel.SendPacket(new ImmConfigPageRequest { ModId = modId });
	}

	public void ApplyServer(string modId, ImmConfigChangePacket[] changes) { Channel.SendPacket(new ImmConfigApplyRequest { ModId = modId, Changes = changes }); }

	public void ResolveDependency(int runtimeId) { Channel.SendPacket(new ImmDependencyResolveRequest { RuntimeId = runtimeId }); }

	public bool TryApplyClient(ImmConfigPageResponse page, ImmConfigChangePacket[] changes, out ImmReloadRequirement reloadRequirement, out string error)
	{
		reloadRequirement = ImmReloadRequirement.None;

		error = "";

		if (page.ConfigurationExternallyManaged || PatchCoordinator?.ExternalManagers.IsControlled(page.ModId) == true)
		{
			error = "Configuration for this mod is controlled by another mod manager.";
			return false;
		}

		if (changes.Length == 0) { return true; }

		Dictionary<int, ControlLocation> controls = BuildControlIndex(page);
		Dictionary<string, JObject> configs = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
		HashSet<int> seenIndexes = new();
		List<PreparedClientChange> preparedConfigChanges = new(changes.Length);
		List<PreparedPatchClientChange> preparedPatchChanges = new();
		JObject? patchDocument = null;

		foreach (ImmConfigChangePacket change in changes)
		{
			if (!seenIndexes.Add(change.Index) || !controls.TryGetValue(change.Index, out ControlLocation location)) { error = $"Configuration index {change.Index} is invalid."; return false; }
			if (location.Control.ConfigSide != ImmConfigSide.Client) { error = $"{location.Control.Label}: setting is not client-owned."; return false; }

			JToken submitted;

			try { submitted = JToken.Parse(change.ValueJson); }
			catch { error = $"{location.Control.Label}: submitted value was not valid JSON."; return false; }

			if (location.Block.ConfigSource == ImmConfigSource.PatchSettings)
			{
				if (PatchCoordinator?.PatchSettings == null || PatchCoordinator.Registry == null) { error = "IMM PatchSettings runtime is not available on the client."; return false; }
				if (!PatchCoordinator.Registry.TryGetPatchSetting(page.ModId, location.Control.Code, out _, out ImmConfigEntry entry, out ImmConfigSide side) || side != ImmConfigSide.Client) { error = $"{location.Control.Label}: PatchSetting metadata is not available."; return false; }
				if (!ImmConfigValueValidator.TryNormalizePatchSetting(entry, submitted, out JToken normalized, out string validationError)) { error = $"{location.Control.Label}: {validationError}"; return false; }

				patchDocument ??= PatchCoordinator.PatchSettings.ReadDocument(page.ModId);
				JToken current = PatchCoordinator.PatchSettings.GetEffectiveValue(page.ModId, entry, ImmConfigSide.Client, patchDocument);

				if (!JToken.DeepEquals(current, normalized)) { preparedPatchChanges.Add(new PreparedPatchClientChange(entry, normalized)); }

				continue;
			}

			if (!TryGetLocalConfig(location.Block.ConfigFile, configs, out JObject config, out error)) { return false; }

			JToken? target;

			try { target = config.SelectToken(location.Control.Map); }
			catch (Exception exception) { error = $"{location.Control.Label}: invalid Map ({exception.Message})."; return false; }

			if (target == null) { error = $"{location.Control.Label}: mapped config value was not found."; return false; }
			if (!ImmConfigValueValidator.TryNormalizeValue(location.Control, target, submitted, out JToken normalizedConfig, out string configValidationError)) { error = $"{location.Control.Label}: {configValidationError}"; return false; }

			if (!JToken.DeepEquals(target, normalizedConfig))
			{
				preparedConfigChanges.Add(new PreparedClientChange(target, normalizedConfig));
				changedFiles.Add(location.Block.ConfigFile);
			}
		}

		try
		{
			foreach (PreparedClientChange change in preparedConfigChanges) { change.Target.Replace(change.Value); }
			ImmAtomicFileBatch batch = new();

			foreach (string configFile in changedFiles)
			{
				if (configs.TryGetValue(configFile, out JObject? config)) { batch.Write(Path.Combine(ClientApi.DataBasePath, "ModConfig", configFile.Replace('/', Path.DirectorySeparatorChar)), config.ToString(Formatting.Indented)); }
			}

			bool committed;

			if (preparedPatchChanges.Count > 0)
			{
				if (PatchCoordinator?.PatchSettings == null) { error = "IMM PatchSettings runtime is not available on the client."; return false; }

				committed = PatchCoordinator.PatchSettings.TryCommitWithOverrides(page.ModId, ImmConfigSide.Client, preparedPatchChanges.Select(change => (change.Entry, change.Value)).ToArray(), batch, out error);
			}
			else { committed = batch.TryCommit(out error); }

			if (!committed) { return false; }
			if (preparedConfigChanges.Count > 0) { ClientApi.Event.PushEvent(ImmConfigBroadcast.GetEventName(page.ModId)); }
			if (PatchCoordinator?.ContentPatches.HasPendingReload(page.ModId, ImmConfigSide.Client) == true) { reloadRequirement = ImmReloadRequirement.ReenterWorld; }

			return true;
		}
		catch (Exception exception) { error = $"Failed to prepare client configuration: {exception.Message}"; return false; }
	}

	private void OnCatalogReceived(ImmConfigCatalogResponse packet) { ClientApi.Event.EnqueueMainThreadTask(() => { if (packet.Success) { FinalizeCatalog(packet); } else { ServerPageModIds.Clear(); CanManageServer = false; } CatalogReceived?.Invoke(packet); }, "integratedmodmanager-config-catalog"); }

	private void OnPageReceived(ImmConfigPageResponse packet) { ClientApi.Event.EnqueueMainThreadTask(() => FinalizePage(packet), "integratedmodmanager-config-page"); }

	private void OnApplyReceived(ImmConfigApplyResponse packet) { ClientApi.Event.EnqueueMainThreadTask(() => ApplyReceived?.Invoke(packet), "integratedmodmanager-config-apply"); }

	private void OnDependencyResolveReceived(ImmDependencyResolveResponse packet) { ClientApi.Event.EnqueueMainThreadTask(() => DependencyResolveReceived?.Invoke(packet), "integratedmodmanager-dependency-resolve"); }

	private void FinalizeCatalog(ImmConfigCatalogResponse packet)
	{
		CanManageServer = packet.CanManageServer;
		ServerPageModIds.Clear();

		ImmServerModPacket[] serverMods = packet.Mods ?? Array.Empty<ImmServerModPacket>();

		foreach (ImmServerModPacket mod in serverMods)
		{
			if (mod.HasConfiguration || mod.WarningCount > 0 || mod.ErrorCount > 0) { ServerPageModIds.Add(mod.ModId); }
		}

		Dictionary<string, ImmServerModPacket> merged = serverMods.Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.ModId)).GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

		ImmConfigRegistry? registry = PatchCoordinator?.Registry;

		if (registry != null)
		{
			Dictionary<string, Mod> localMods = ClientApi.ModLoader.Mods.Where(mod => !string.IsNullOrWhiteSpace(mod.Info?.ModID)).GroupBy(mod => mod.Info!.ModID, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

			foreach (ImmRegisteredDescriptor registered in registry.RegisteredDescriptors)
			{
				if (!ImmConfigPacketFactory.HasConfigurationForSide(registered.Descriptor, ImmConfigSide.Client)) { continue; }

				if (merged.TryGetValue(registered.ModId, out ImmServerModPacket? existing)) { existing.HasConfiguration = true; continue; }

				localMods.TryGetValue(registered.ModId, out Mod? localMod);

				merged[registered.ModId] = new ImmServerModPacket { ModId = registered.ModId, Name = localMod?.Info?.Name ?? registered.ModId, HasConfiguration = true };
			}
		}

		IEnumerable<ImmServerModPacket> visibleMods = merged.Values;
		if (!IntegratedModManagerConfig.ShouldShowNonConfigurableMods) { visibleMods = visibleMods.Where(mod => mod.HasConfiguration || mod.WarningCount > 0 || mod.ErrorCount > 0); }

		packet.Mods = visibleMods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private bool TryBuildLocalClientPage(string modId, out ImmConfigPageResponse page)
	{
		page = new ImmConfigPageResponse { ModId = modId };

		ImmConfigRegistry? registry = PatchCoordinator?.Registry;

		if (registry == null || !registry.TryGet(modId, out ImmConfigDescriptor descriptor) || !ImmConfigPacketFactory.HasConfigurationForSide(descriptor, ImmConfigSide.Client)) { return false; }

		ImmExternalManagerOwnership? externalManagers = PatchCoordinator?.ExternalManagers;

		if (externalManagers?.IsControlled(modId) == true)
		{
			page = new ImmConfigPageResponse { Success = true, CanManageServer = CanManageServer, ModId = modId, Configuration = Array.Empty<ImmConfigBlockPacket>(), Dependencies = Array.Empty<ImmDependencyPacket>(), ConfigurationExternallyManaged = true, ExternalManagerActive = externalManagers.AnyManagerActive, ExternalManagerName = externalManagers.GetPrimaryManagerName(ClientApi, modId) };
			return true;
		}

		int globalIndex = 0;

		List<ImmConfigBlockPacket> blocks = new();

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
		{
			ImmConfigBlock block = descriptor.Configuration[blockIndex];
			List<ImmConfigControlPacket> controls = new();

			foreach (ImmConfigEntry entry in block.Settings)
			{
				ImmConfigSide effectiveSide = entry.ConfigSide ?? block.ConfigSide;

				int controlIndex = globalIndex++;

				if (effectiveSide != ImmConfigSide.Client) { continue; }

				controls.Add(ImmConfigPacketFactory.CreateControlPacket(controlIndex, blockIndex, block, entry, effectiveSide));
			}

			if (controls.Count == 0) { continue; }

			blocks.Add(ImmConfigPacketFactory.CreateBlockPacket(blockIndex, block, controls.ToArray()));
		}

		if (blocks.Count == 0) { return false; }
		page = new ImmConfigPageResponse { Success = true, CanManageServer = CanManageServer, ModId = modId, Configuration = blocks.ToArray(), Dependencies = Array.Empty<ImmDependencyPacket>(), ExternalManagerActive = PatchCoordinator?.ExternalManagers.AnyManagerActive == true };

		return true;
	}

	private void MergeLocalClientConfiguration(ImmConfigPageResponse page)
	{
		if (page.ConfigurationExternallyManaged) { page.Configuration = Array.Empty<ImmConfigBlockPacket>(); return; }

		ImmConfigRegistry? registry = PatchCoordinator?.Registry;

		if (registry == null || !registry.TryGet(page.ModId, out ImmConfigDescriptor descriptor)) { return; }

		List<ImmConfigBlockPacket> blocks = (page.Configuration ?? Array.Empty<ImmConfigBlockPacket>()).Where(block => block != null).ToList();

		int nextControlIndex = blocks.SelectMany(block => block.Controls ?? Array.Empty<ImmConfigControlPacket>()).Select(control => control.Index).DefaultIfEmpty(-1).Max() + 1;
		int nextBlockIndex = blocks.Select(block => block.Index).DefaultIfEmpty(-1).Max() + 1;

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
		{
			ImmConfigBlock localBlock = descriptor.Configuration[blockIndex];

			List<ImmConfigEntry> missingEntries = localBlock.Settings.Where(entry => (entry.ConfigSide ?? localBlock.ConfigSide) == ImmConfigSide.Client && !ContainsClientControl(blocks, localBlock, entry)).ToList();

			if (missingEntries.Count == 0) { continue; }

			ImmConfigBlockPacket? targetBlock = blocks.FirstOrDefault(block => block.Index == blockIndex && block.ConfigSource == localBlock.ConfigSource && string.Equals(block.ConfigFile, localBlock.ConfigFile, StringComparison.OrdinalIgnoreCase));

			if (targetBlock == null)
			{
				targetBlock = ImmConfigPacketFactory.CreateBlockPacket(nextBlockIndex++, localBlock, Array.Empty<ImmConfigControlPacket>());

				blocks.Add(targetBlock);
			}

			List<ImmConfigControlPacket> controls = (targetBlock.Controls ?? Array.Empty<ImmConfigControlPacket>()).ToList();

			foreach (ImmConfigEntry entry in missingEntries) { controls.Add(ImmConfigPacketFactory.CreateControlPacket(nextControlIndex++, targetBlock.Index, localBlock, entry, ImmConfigSide.Client)); }

			targetBlock.Controls = controls.ToArray();
		}

		page.Configuration = blocks.ToArray();
	}

	private static bool ContainsClientControl(IEnumerable<ImmConfigBlockPacket> blocks, ImmConfigBlock localBlock, ImmConfigEntry localEntry)
	{
		foreach (ImmConfigBlockPacket block in blocks)
		{
			if (block.ConfigSource != localBlock.ConfigSource) { continue; }

			foreach (ImmConfigControlPacket control in block.Controls ?? Array.Empty<ImmConfigControlPacket>())
			{
				if (control.ConfigSide != ImmConfigSide.Client) { continue; }

				if (localBlock.ConfigSource == ImmConfigSource.PatchSettings)
				{
					if (string.Equals(control.Code, localEntry.Code, StringComparison.Ordinal)) { return true; }

					continue;
				}

				if (string.Equals(block.ConfigFile, localBlock.ConfigFile, StringComparison.OrdinalIgnoreCase) && string.Equals(control.Map, localEntry.Map, StringComparison.Ordinal)) { return true; }
			}
		}

		return false;
	}

	private void FinalizePage(ImmConfigPageResponse packet)
	{
		if (packet.Success)
		{
			ImmExternalManagerOwnership? externalManagers = PatchCoordinator?.ExternalManagers;

			bool locallyControlled = externalManagers?.IsControlled(packet.ModId) == true;

			if (locallyControlled && string.IsNullOrWhiteSpace(packet.ExternalManagerName)) { packet.ExternalManagerName = externalManagers!.GetPrimaryManagerName(ClientApi, packet.ModId); }

			packet.ConfigurationExternallyManaged |= locallyControlled;
			packet.ExternalManagerActive |= externalManagers?.AnyManagerActive == true;

			if (packet.ConfigurationExternallyManaged) { packet.Configuration = Array.Empty<ImmConfigBlockPacket>(); }
			else
			{
				MergeLocalClientConfiguration(packet);
				PopulateClientValues(packet);
			}
		}

		ImmLocalization.LocalizePage(packet);

		PageReceived?.Invoke(packet);
	}

	private void PopulateClientValues(ImmConfigPageResponse page)
	{
		JObject? patchDocument = null;

		foreach (ImmConfigBlockPacket block in page.Configuration ?? Array.Empty<ImmConfigBlockPacket>())
		{
			ImmConfigControlPacket[] controls = block.Controls ?? Array.Empty<ImmConfigControlPacket>();
			bool needsClientConfig = block.ConfigSide == ImmConfigSide.Client || controls.Any(control => control.ConfigSide == ImmConfigSide.Client);

			if (!needsClientConfig) { continue; }

			if (block.ConfigSource == ImmConfigSource.PatchSettings)
			{
				if (PatchCoordinator?.PatchSettings == null || PatchCoordinator.Registry == null) { MarkClientControlsUnavailable(controls, "IMM PatchSettings runtime is not available."); continue; }

				patchDocument ??= PatchCoordinator.PatchSettings.ReadDocument(page.ModId);

				foreach (ImmConfigControlPacket control in controls)
				{
					if (control.ConfigSide != ImmConfigSide.Client) { continue; }

					if (!PatchCoordinator.Registry.TryGetPatchSetting(page.ModId, control.Code, out _, out ImmConfigEntry entry, out ImmConfigSide side) || side != ImmConfigSide.Client)
					{
						control.Available = false;
						control.UnavailableReason = "PatchSetting metadata is unavailable.";
						continue;
					}

					JToken saved = PatchCoordinator.PatchSettings.GetEffectiveValue(page.ModId, entry, side, patchDocument);

					if (!ImmConfigValueValidator.TryValidateCurrentValue(control, saved, out string validationError))
					{
						control.Available = false;
						control.UnavailableReason = validationError;
						continue;
					}

					control.Available = true;
					control.UnavailableReason = "";
					control.ValueJson = saved.ToString(Formatting.None);

					control.PendingReload = PatchCoordinator.ContentPatches?.IsPendingReload(page.ModId, entry, side, saved) == true;
				}

				continue;
			}

			JObject? config;

			try { config = ClientApi.LoadModConfig<JObject>(block.ConfigFile); }
			catch (Exception exception) { MarkClientControlsUnavailable(controls, $"Failed to read client config: {exception.Message}"); continue; }

			if (config == null) { MarkClientControlsUnavailable(controls, $"Config file '{block.ConfigFile}' was not found."); continue; }

			if (block.ParseDescriptions && block.ConfigSide == ImmConfigSide.Client) { block.Description = ReadRootDescription(config); }

			foreach (ImmConfigControlPacket control in controls)
			{
				if (control.ConfigSide != ImmConfigSide.Client) { continue; }

				PopulateControl(block, control, config);
			}
		}
	}

	private static void PopulateControl(ImmConfigBlockPacket block, ImmConfigControlPacket control, JObject config)
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
		control.UnavailableReason = "";
		control.ValueJson = target.ToString(Formatting.None);

		if (block.ParseDescriptions) { control.Description = ReadSettingDescription(target); }
	}

	private static string ReadRootDescription(JObject config) { return config.TryGetValue("Description", StringComparison.OrdinalIgnoreCase, out JToken? description) && description.Type == JTokenType.String ? description.Value<string>() ?? "" : ""; }

	private static string ReadSettingDescription(JToken target)
	{
		if (target.Parent is not JProperty property || property.Parent is not JObject parent) { return ""; }

		string descriptionName = property.Name + "Description";

		return parent.TryGetValue(descriptionName, StringComparison.OrdinalIgnoreCase, out JToken? description) && description.Type == JTokenType.String ? description.Value<string>() ?? "" : "";
	}

	private static void MarkClientControlsUnavailable(ImmConfigControlPacket[] controls, string reason)
	{
		foreach (ImmConfigControlPacket control in controls)
		{
			if (control.ConfigSide != ImmConfigSide.Client) { continue; }

			control.Available = false;
			control.UnavailableReason = reason;
		}
	}

	private bool TryGetLocalConfig(string configFile, Dictionary<string, JObject> configs, out JObject config, out string error)
	{
		if (configs.TryGetValue(configFile, out config!)) { error = ""; return true; }

		try
		{
			config = ClientApi.LoadModConfig<JObject>(configFile)!;

			if (config == null) { error = $"Config file '{configFile}' was not found."; return false; }

			configs[configFile] = config;

			error = "";
			return true;
		}
		catch (Exception exception)
		{
			config = new JObject();
			error = $"Failed to read '{configFile}': {exception.Message}";
			return false;
		}
	}

	private static Dictionary<int, ControlLocation>
		BuildControlIndex(ImmConfigPageResponse page)
	{
		Dictionary<int, ControlLocation> result = new();

		foreach (ImmConfigBlockPacket block in page.Configuration ?? Array.Empty<ImmConfigBlockPacket>())
		{
			foreach (ImmConfigControlPacket control in block.Controls ?? Array.Empty<ImmConfigControlPacket>()) { result[control.Index] = new ControlLocation(block, control); }
		}

		return result;
	}

	public void Dispose()
	{
		ServerPageModIds.Clear();
		CanManageServer = false;

		CatalogReceived = null;
		PageReceived = null;
		ApplyReceived = null;
		DependencyResolveReceived = null;
	}

	private sealed record ControlLocation(ImmConfigBlockPacket Block, ImmConfigControlPacket Control);
	private sealed record PreparedClientChange(JToken Target, JToken Value);
	private sealed record PreparedPatchClientChange(ImmConfigEntry Entry, JToken Value);
}

public static class ImmConfigNetwork
{
	public const string NetworkChannelCode = "integratedmodmanager-config";

	public static void RegisterNetwork(ICoreAPI api) { api.Network.RegisterChannel(NetworkChannelCode).RegisterMessageType<ImmConfigCatalogRequest>().RegisterMessageType<ImmConfigCatalogResponse>().RegisterMessageType<ImmServerModPacket>().RegisterMessageType<ImmConfigPageRequest>().RegisterMessageType<ImmConfigPageResponse>().RegisterMessageType<ImmConfigBlockPacket>().RegisterMessageType<ImmConfigControlPacket>().RegisterMessageType<ImmConfigOptionPacket>().RegisterMessageType<ImmConfigApplyRequest>().RegisterMessageType<ImmConfigChangePacket>().RegisterMessageType<ImmConfigApplyResponse>().RegisterMessageType<ImmDependencyPacket>().RegisterMessageType<ImmDependencyResolveRequest>().RegisterMessageType<ImmDependencyResolveResponse>(); }

	public static void StartServer(ICoreServerAPI api, ImmConfigService service)
	{
		IServerNetworkChannel channel = api.Network.GetChannel(NetworkChannelCode);

		channel.SetMessageHandler<ImmConfigCatalogRequest>((fromPlayer, packet) => { channel.SendPacket(service.BuildCatalog(fromPlayer), fromPlayer); });
		channel.SetMessageHandler<ImmConfigPageRequest>((fromPlayer, packet) =>
		{
			ImmConfigPageResponse response = service.BuildPage(fromPlayer, packet);

			ImmLocalization.LocalizePage(response, fromPlayer.LanguageCode);
			channel.SendPacket(response, fromPlayer);
		});
		channel.SetMessageHandler<ImmConfigApplyRequest>((fromPlayer, packet) => { channel.SendPacket(service.Apply(fromPlayer, packet), fromPlayer); });
		channel.SetMessageHandler<ImmDependencyResolveRequest>((fromPlayer, packet) => { channel.SendPacket(service.Resolve(fromPlayer, packet), fromPlayer); });
	}
}

[ProtoContract]
public sealed class ImmConfigCatalogRequest
{
	[ProtoMember(1)] public byte Request = 1;
}

[ProtoContract]
public sealed class ImmConfigCatalogResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public ImmServerModPacket[] Mods = Array.Empty<ImmServerModPacket>();
	[ProtoMember(4)] public bool CanManageServer;
}

[ProtoContract]
public sealed class ImmServerModPacket
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public string Name = "";
	[ProtoMember(3)] public bool HasConfiguration;
	[ProtoMember(4)] public int WarningCount;
	[ProtoMember(5)] public int ErrorCount;
}

[ProtoContract]
public sealed class ImmConfigPageRequest
{
	[ProtoMember(1)] public string ModId = "";
}

[ProtoContract]
public sealed class ImmConfigPageResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public string ModId = "";
	[ProtoMember(4)] public ImmConfigBlockPacket[] Configuration = Array.Empty<ImmConfigBlockPacket>();
	[ProtoMember(5)] public ImmDependencyPacket[] Dependencies = Array.Empty<ImmDependencyPacket>();
	[ProtoMember(6)] public bool CanManageServer;
	[ProtoMember(7)] public bool ConfigurationExternallyManaged;
	[ProtoMember(8)] public bool ExternalManagerActive;
	[ProtoMember(9)] public string ExternalManagerName = "";
}

[ProtoContract]
public sealed class ImmConfigBlockPacket
{
	[ProtoMember(1)] public int Index;
	[ProtoMember(2)] public string ConfigFile = "";
	[ProtoMember(3)] public string ConfigLabel = "";
	[ProtoMember(4)] public ImmConfigSide ConfigSide;
	[ProtoMember(5)] public bool ParseDescriptions;
	[ProtoMember(6)] public string Description = "";
	[ProtoMember(7)] public ImmConfigControlPacket[] Controls = Array.Empty<ImmConfigControlPacket>();
	[ProtoMember(8)] public ImmConfigSource ConfigSource;
}

[ProtoContract]
public sealed class ImmConfigControlPacket
{
	[ProtoMember(1)] public int Index;
	[ProtoMember(2)] public int BlockIndex;
	[ProtoMember(3)] public string Type = "";
	[ProtoMember(4)] public string Label = "";
	[ProtoMember(5)] public string Map = "";
	[ProtoMember(6)] public ImmConfigSide ConfigSide;
	[ProtoMember(7)] public string Description = "";
	[ProtoMember(8)] public string ValueJson = "";
	[ProtoMember(9)] public bool Available;
	[ProtoMember(10)] public string UnavailableReason = "";
	[ProtoMember(11)] public bool HasMin;
	[ProtoMember(12)] public double Min;
	[ProtoMember(13)] public bool HasMax;
	[ProtoMember(14)] public double Max;
	[ProtoMember(15)] public bool HasStep;
	[ProtoMember(16)] public double Step;
	[ProtoMember(17)] public ImmConfigOptionPacket[] Options = Array.Empty<ImmConfigOptionPacket>();
	[ProtoMember(18)] public string ElementType = "";
	[ProtoMember(19)] public string Code = "";
	[ProtoMember(20)] public string DefaultValueJson = "";
	[ProtoMember(21)] public ImmConfigSource ConfigSource;
	[ProtoMember(22)] public bool PendingReload;
}

[ProtoContract]
public sealed class ImmConfigOptionPacket
{
	[ProtoMember(1)] public string Label = "";

	[ProtoMember(2)] public string ValueJson = "";
}

[ProtoContract]
public sealed class ImmConfigApplyRequest
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public ImmConfigChangePacket[] Changes = Array.Empty<ImmConfigChangePacket>();
}

[ProtoContract]
public sealed class ImmConfigChangePacket
{
	[ProtoMember(1)] public int Index;
	[ProtoMember(2)] public string ValueJson = "";
}

[ProtoContract]
public sealed class ImmConfigApplyResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public string ModId = "";
	[ProtoMember(4)] public ImmReloadRequirement ReloadRequirement;
}

[ProtoContract]
public sealed class ImmDependencyPacket
{
	[ProtoMember(1)] public int RuntimeId;
	[ProtoMember(2)] public string Label = "";
	[ProtoMember(3)] public string Description = "";
	[ProtoMember(4)] public ImmDependencySeverity Severity;
	[ProtoMember(5)] public bool HasResolution;
	[ProtoMember(6)] public ImmDependencyResolutionType ResolutionType;
	[ProtoMember(7)] public string ResolutionDescription = "";
	[ProtoMember(8)] public string ResolutionDescriptionKey = "";
	[ProtoMember(9)] public string[] ResolutionDescriptionArgs = Array.Empty<string>();
}

[ProtoContract]
public sealed class ImmDependencyResolveRequest
{
	[ProtoMember(1)] public int RuntimeId;
}

public enum ImmDependencyResolutionWarning
{
	None,
	ExternallyManagedModConfig,
	ExternallyManagedPatchSetting
}

[ProtoContract]
public sealed class ImmDependencyResolveResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public int RuntimeId;
	[ProtoMember(4)] public bool RestartRequired;
	[ProtoMember(5)] public string ChatCommand = "";
	[ProtoMember(6)] public ImmDependencyResolutionWarning Warning;
}

