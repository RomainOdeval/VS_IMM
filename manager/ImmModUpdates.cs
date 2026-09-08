#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace IntegratedModManager.Config;

public enum ImmModUpdateCheckState
{
	NotChecked,
	Checking,
	Ready,
	Failed
}

public enum ImmModUpdateEntryState
{
	Available,
	PendingRestart
}

internal sealed record ImmInstalledModSnapshot(string ModId, string Name, string Version, EnumModSourceType SourceType);

internal sealed class ImmModUpdateEntry
{
	public string ModId = "";
	public string Name = "";
	public string InstalledVersion = "";
	public string TargetVersion = "";
	public EnumModSourceType SourceType;
	public ImmModUpdateEntryState State;
	public bool Retracted;
	public bool HostedInstallAllowed = true;
}

public sealed class ImmModUpdateService : IDisposable
{
	private const string ModDbApiUrl = "https://mods.vintagestory.at/api/";
	private const int ChangelogLimit = 32768;
	private const int CheckBatchSize = 50;
	private const int HostedModeForbiddenErrorCode = 4031;
	private const int RetractedErrorCode = 4101;
	private const int ForcedRetractedErrorCode = 4102;

	private static readonly Regex BrRegex = new(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex ListItemStartRegex = new(@"<\s*li(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex ListItemEndRegex = new(@"<\s*/\s*li\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex ParagraphEndRegex = new(@"<\s*/\s*p\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
	private static readonly Regex ExcessBlankLinesRegex = new(@"\n[ \t]*\n(?:[ \t]*\n)+", RegexOptions.Compiled);

	private readonly ICoreServerAPI Api;
	private readonly HttpClient Http = new() { BaseAddress = new Uri(ModDbApiUrl), Timeout = TimeSpan.FromSeconds(15) };
	private readonly CancellationTokenSource Cancellation = new();
	private readonly Dictionary<string, ImmModUpdateEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Task<string>> ChangelogTasks = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> InstallRequestsInFlight = new(StringComparer.OrdinalIgnoreCase);
	private List<ImmInstalledModSnapshot>? LocalClientSnapshots;

	private ImmUpdateManagementMode Mode;
	private ImmModUpdateCheckState CheckState = ImmModUpdateCheckState.NotChecked;
	private bool FullCacheReady;
	private bool ActiveCheckIsFull;
	private string CheckError = "";
	private int CheckGeneration;
	private bool Disposed;
	private DateTime LastFullCheckAttemptUtc = DateTime.MinValue;

	public ImmModUpdateService(ICoreServerAPI api)
	{
		Api = api;
	}

	public void Start()
	{
		Mode = IntegratedModManagerConfig.LoadOrCreate(Api).UpdateManagement;
		Api.Event.RegisterEventBusListener(OnConfigChanged, filterByEventName: ImmConfigBroadcast.GetEventName(IntegratedModManagerSystem.ModId));

		BeginConfiguredCheck();
	}

	public void ReceiveLocalInventory(IServerPlayer player, ImmModUpdateLocalInventoryPacket packet)
	{
		if (Disposed || Api.Server.IsDedicated || !CanManage(player)) { return; }

		LocalClientSnapshots = (packet.Mods ?? Array.Empty<ImmLocalInstalledModPacket>())
			.Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.ModId) && !string.IsNullOrWhiteSpace(mod.Version))
			.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.Select(mod => new ImmInstalledModSnapshot(mod.ModId, string.IsNullOrWhiteSpace(mod.Name) ? mod.ModId : mod.Name, mod.Version, mod.SourceType))
			.ToList();

		if (Mode == ImmUpdateManagementMode.Manual)
		{
			if (CheckState == ImmModUpdateCheckState.NotChecked) { BeginCheck(full: false); }
		}
		else { EnsureFullCheck(); }
	}

	private void BeginConfiguredCheck()
	{
		if (Mode == ImmUpdateManagementMode.Manual) { BeginCheck(full: false); }
		else { EnsureFullCheck(); }
	}

	public ImmModUpdateSummaryResponse BuildSummary(IServerPlayer player)
	{
		if (!CanManage(player)) { return new ImmModUpdateSummaryResponse { Error = "You do not have permission to manage server mods." }; }

		return CreateSummary();
	}

	public ImmModUpdateCatalogResponse BuildCatalog(IServerPlayer player, bool requestFullCheck)
	{
		if (!CanManage(player)) { return new ImmModUpdateCatalogResponse { Error = "You do not have permission to manage server mods." }; }
		if (requestFullCheck) { EnsureFullCheck(); }

		return CreateCatalog();
	}

	public void RequestInstall(IServerPlayer player, ImmModUpdateInstallRequest request, Action<ImmModUpdateInstallResponse> reply)
	{
		if (!CanManage(player))
		{
			reply(new ImmModUpdateInstallResponse { Error = "You do not have permission to manage server mods.", ModId = request.ModId, TargetVersion = request.TargetVersion });
			return;
		}

		if (!TryGetExactEntry(request.ModId, request.TargetVersion, out ImmModUpdateEntry? entry))
		{
			reply(new ImmModUpdateInstallResponse { Error = "That update is not present in the current ModDB update cache.", ModId = request.ModId, TargetVersion = request.TargetVersion });
			return;
		}

		RequestInstall(entry, response => reply(response));
	}

	public void RequestChangelog(IServerPlayer player, ImmModUpdateChangelogRequest request, Action<ImmModUpdateChangelogResponse> reply)
	{
		if (!CanManage(player))
		{
			reply(new ImmModUpdateChangelogResponse { Error = "You do not have permission to manage server mods.", ModId = request.ModId, TargetVersion = request.TargetVersion });
			return;
		}

		if (!TryGetExactEntry(request.ModId, request.TargetVersion, out _))
		{
			reply(new ImmModUpdateChangelogResponse { Error = "That update is not present in the current ModDB update cache.", ModId = request.ModId, TargetVersion = request.TargetVersion });
			return;
		}

		string cacheKey = request.ModId + "@" + request.TargetVersion;

		if (!ChangelogTasks.TryGetValue(cacheKey, out Task<string>? task))
		{
			task = FetchChangelogAsync(request.ModId, request.TargetVersion, Cancellation.Token);
			ChangelogTasks[cacheKey] = task;
		}

		task.ContinueWith(completed =>
		{
			if (Disposed) { return; }

			Api.Event.EnqueueMainThreadTask(() =>
			{
				if (Disposed) { return; }

				if (completed.IsCompletedSuccessfully)
				{
					reply(new ImmModUpdateChangelogResponse { Success = true, ModId = request.ModId, TargetVersion = request.TargetVersion, Changelog = completed.Result });
					return;
				}

				if (completed.IsFaulted) { ChangelogTasks.Remove(cacheKey); }

				string error = completed.Exception?.GetBaseException().Message ?? "The changelog request was cancelled.";
				reply(new ImmModUpdateChangelogResponse { Error = error, ModId = request.ModId, TargetVersion = request.TargetVersion });
			}, "integratedmodmanager-update-changelog");
		});
	}

	private void OnConfigChanged(string eventName, ref EnumHandling handling, Vintagestory.API.Datastructures.IAttribute data)
	{
		if (Disposed) { return; }

		ImmUpdateManagementMode newMode;
		try { newMode = Api.LoadModConfig<IntegratedModManagerConfig>(IntegratedModManagerConfig.FileName)?.UpdateManagement ?? ImmUpdateManagementMode.Manual; }
		catch { return; }
		if (newMode == Mode) { return; }

		Mode = newMode;

		if (Mode == ImmUpdateManagementMode.AutomaticCheck) { EnsureFullCheck(); }
		else if (Mode == ImmUpdateManagementMode.AutomaticUpdate)
		{
			if (FullCacheReady && CheckState == ImmModUpdateCheckState.Ready) { RequestAllUpdates(); }
			else { EnsureFullCheck(); }
		}
	}

	private void EnsureFullCheck()
	{
		if (Disposed || FullCacheReady) { return; }
		if (!Api.Server.IsDedicated && LocalClientSnapshots == null) { return; }
		if (CheckState == ImmModUpdateCheckState.Checking && ActiveCheckIsFull) { return; }
		if (CheckState == ImmModUpdateCheckState.Failed && DateTime.UtcNow - LastFullCheckAttemptUtc < TimeSpan.FromSeconds(10)) { return; }

		BeginCheck(full: true);
	}

	private void BeginCheck(bool full)
	{
		if (Disposed || (full && !Api.Server.IsDedicated && LocalClientSnapshots == null)) { return; }

		List<ImmInstalledModSnapshot> snapshots = SnapshotMods(full);
		int generation = ++CheckGeneration;
		if (full) { LastFullCheckAttemptUtc = DateTime.UtcNow; }

		CheckState = ImmModUpdateCheckState.Checking;
		ActiveCheckIsFull = full;
		CheckError = "";

		if (snapshots.Count == 0)
		{
			ApplyCheckResult(generation, full, Array.Empty<ImmModUpdateEntry>());
			return;
		}

		FetchUpdatesAsync(snapshots, Cancellation.Token).ContinueWith(task =>
		{
			if (Disposed) { return; }

			Api.Event.EnqueueMainThreadTask(() =>
			{
				if (Disposed || generation != CheckGeneration) { return; }

				if (task.IsCompletedSuccessfully)
				{
					ApplyCheckResult(generation, full, task.Result);
					return;
				}

				CheckState = ImmModUpdateCheckState.Failed;
				ActiveCheckIsFull = false;
				CheckError = task.Exception?.GetBaseException().Message ?? "The ModDB request was cancelled.";
				Api.Logger.Warning("[integratedmodmanager] ModDB update check failed: {0}", CheckError);
			}, "integratedmodmanager-update-check");
		});
	}

	private List<ImmInstalledModSnapshot> SnapshotMods(bool full)
	{
		Dictionary<string, ImmInstalledModSnapshot> snapshots = Api.ModLoader.Mods
			.Where(mod => mod.Info != null && !string.IsNullOrWhiteSpace(mod.Info.ModID) && !string.IsNullOrWhiteSpace(mod.Info.Version))
			.GroupBy(mod => mod.Info.ModID, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToDictionary
			(
				mod => mod.Info.ModID,
				mod => new ImmInstalledModSnapshot(mod.Info.ModID, string.IsNullOrWhiteSpace(mod.Info.Name) ? mod.Info.ModID : mod.Info.Name, mod.Info.Version, mod.SourceType),
				StringComparer.OrdinalIgnoreCase
			);

		if (!Api.Server.IsDedicated && LocalClientSnapshots != null)
		{
			foreach (ImmInstalledModSnapshot local in LocalClientSnapshots) { if (!snapshots.ContainsKey(local.ModId)) { snapshots[local.ModId] = local; } }
		}

		IEnumerable<ImmInstalledModSnapshot> mods = snapshots.Values;

		if (full) { mods = mods.Where(mod => !IsBuiltInMod(mod.ModId)); }
		else { mods = mods.Where(mod => string.Equals(mod.ModId, IntegratedModManagerSystem.ModId, StringComparison.OrdinalIgnoreCase)); }

		return mods.ToList();
	}

	private async Task<ImmModUpdateEntry[]> FetchUpdatesAsync(List<ImmInstalledModSnapshot> snapshots, CancellationToken cancellationToken)
	{
		List<ImmModUpdateEntry> updates = new();

		foreach (ImmInstalledModSnapshot[] batch in snapshots.Chunk(CheckBatchSize))
		{
			string ids = string.Join(",", batch.Select(mod => Uri.EscapeDataString(mod.ModId + "@" + mod.Version)));
			string path = "v2/mods/install-information?gv=" + Uri.EscapeDataString(GameVersion.ShortGameVersion) + "&ids=" + ids;

			using HttpResponseMessage response = await Http.GetAsync(path, cancellationToken).ConfigureAwait(false);
			string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) { throw new HttpRequestException($"ModDB returned HTTP {(int)response.StatusCode}."); }

			ModInstallInformationResponse? result = JsonConvert.DeserializeObject<ModInstallInformationResponse>(json);
			if (result == null) { throw new InvalidOperationException("ModDB returned an empty update response."); }
			if (!string.IsNullOrWhiteSpace(result.Error)) { throw new InvalidOperationException(result.Error); }

			Dictionary<string, ModInstallInformation> data = result.Data ?? new Dictionary<string, ModInstallInformation>(StringComparer.OrdinalIgnoreCase);

			foreach (ImmInstalledModSnapshot mod in batch)
			{
				if 
				(
					!TryGetInstallInformation(data, mod.ModId, out ModInstallInformation? information)
					|| string.IsNullOrWhiteSpace(information.RecommendedUpgrade)
					|| string.Equals(information.RecommendedUpgrade, mod.Version, StringComparison.OrdinalIgnoreCase)
				) { continue; }

				updates.Add(new ImmModUpdateEntry
				{
					ModId = mod.ModId,
					Name = mod.Name,
					InstalledVersion = mod.Version,
					TargetVersion = information.RecommendedUpgrade,
					SourceType = mod.SourceType,
					State = ImmModUpdateEntryState.Available,
					Retracted = IsRetracted(information)
				});
			}
		}

		if (Api.Server.Config.HostedMode && Api.Server.Config.HostedModeAllowMods && updates.Count > 0)
		{
			await ApplyHostedInstallabilityAsync(updates, cancellationToken).ConfigureAwait(false);
		}

		return updates.ToArray();
	}

	private async Task ApplyHostedInstallabilityAsync(List<ImmModUpdateEntry> updates, CancellationToken cancellationToken)
	{
		foreach (ImmModUpdateEntry[] batch in updates.Chunk(CheckBatchSize))
		{
			try
			{
				string ids = string.Join(",", batch.Select(entry => Uri.EscapeDataString(entry.ModId + "@" + entry.TargetVersion)));
				string path = "v2/mods/install-information?gv=" + Uri.EscapeDataString(GameVersion.ShortGameVersion) + "&hosted-mode=1&ids=" + ids;

				using HttpResponseMessage response = await Http.GetAsync(path, cancellationToken).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode) { continue; }

				string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				ModInstallInformationResponse? result = JsonConvert.DeserializeObject<ModInstallInformationResponse>(json);
				if (result?.Data == null) { continue; }

				foreach (ImmModUpdateEntry entry in batch)
				{
					if (TryGetInstallInformation(result.Data, entry.ModId, out ModInstallInformation? information) && information.ErrorCode == HostedModeForbiddenErrorCode)
					{
						entry.HostedInstallAllowed = false;
					}
				}
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception exception)
			{
				Api.Logger.Warning("[integratedmodmanager] Could not verify hosted-mode installability for a ModDB update batch: {0}", exception.Message);
			}
		}
	}

	private void ApplyCheckResult(int generation, bool full, IReadOnlyList<ImmModUpdateEntry> updates)
	{
		if (Disposed || generation != CheckGeneration) { return; }

		Dictionary<string, ImmModUpdateEntryState> pending = Entries.Values
			.Where(entry => entry.State == ImmModUpdateEntryState.PendingRestart)
			.ToDictionary(entry => EntryKey(entry.ModId, entry.TargetVersion), entry => entry.State, StringComparer.OrdinalIgnoreCase);

		Entries.Clear();

		foreach (ImmModUpdateEntry entry in updates)
		{
			if (pending.TryGetValue(EntryKey(entry.ModId, entry.TargetVersion), out ImmModUpdateEntryState state)) { entry.State = state; }
			Entries[entry.ModId] = entry;
		}

		CheckState = ImmModUpdateCheckState.Ready;
		ActiveCheckIsFull = false;
		FullCacheReady = full;
		CheckError = "";

		if (full && Mode == ImmUpdateManagementMode.AutomaticUpdate) { RequestAllUpdates(); }
	}

	private void RequestAllUpdates()
	{
		foreach (ImmModUpdateEntry entry in Entries.Values.Where(entry => entry.State == ImmModUpdateEntryState.Available && IsInstallable(entry)).ToArray())
		{
			RequestInstall(entry, _ => { });
		}
	}

	private void RequestInstall(ImmModUpdateEntry entry, Action<ImmModUpdateInstallResponse> reply)
	{
		string key = EntryKey(entry.ModId, entry.TargetVersion);

		if (entry.State == ImmModUpdateEntryState.PendingRestart)
		{
			reply(CreateInstallResponse(entry, success: true, ""));
			return;
		}

		if (!IsInstallable(entry))
		{
			reply(CreateInstallResponse(entry, success: false, GetUnavailableReason(entry)));
			return;
		}

		if (!InstallRequestsInFlight.Add(key))
		{
			reply(CreateInstallResponse(entry, success: false, "An update request for this mod is already in progress."));
			return;
		}

		Caller caller = new() { Type = EnumCallerType.Console, CallerPrivileges = new[] { Privilege.controlserver } };
		TextCommandCallingArgs args = new() { Caller = caller, LanguageCode = "en" };
		string command = $"/moddb install {entry.ModId}@{entry.TargetVersion} {GameVersion.ShortGameVersion}";

		try
		{
			Api.ChatCommands.ExecuteUnparsed(command, args, result =>
			{
				InstallRequestsInFlight.Remove(key);

				if (result.Status == EnumCommandStatus.Deferred || result.Status == EnumCommandStatus.Success)
				{
					entry.State = ImmModUpdateEntryState.PendingRestart;
					reply(CreateInstallResponse(entry, success: true, ""));
					return;
				}

				string error = string.IsNullOrWhiteSpace(result.StatusMessage) ? "Vintage Story rejected the ModDB update request." : result.StatusMessage;
				reply(CreateInstallResponse(entry, success: false, error));
			});
		}
		catch (Exception exception)
		{
			InstallRequestsInFlight.Remove(key);
			reply(CreateInstallResponse(entry, success: false, exception.Message));
		}
	}

	private ImmModUpdateSummaryResponse CreateSummary()
	{
		return new ImmModUpdateSummaryResponse
		{
			Success = true,
			Error = CheckError,
			State = CheckState,
			AvailableCount = Entries.Values.Count(entry => entry.State == ImmModUpdateEntryState.Available),
			PendingRestartCount = Entries.Values.Count(entry => entry.State == ImmModUpdateEntryState.PendingRestart),
			FullCacheReady = FullCacheReady,
			RetractedCount = Entries.Values.Count(entry => entry.Retracted)
		};
	}

	private ImmModUpdateCatalogResponse CreateCatalog()
	{
		return new ImmModUpdateCatalogResponse
		{
			Success = true,
			Error = CheckError,
			State = CheckState,
			FullCacheReady = FullCacheReady,
			Updates = Entries.Values
				.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
				.Select(entry => new ImmModUpdatePacket
				{
					ModId = entry.ModId,
					Name = entry.Name,
					InstalledVersion = entry.InstalledVersion,
					TargetVersion = entry.TargetVersion,
					SourceType = entry.SourceType,
					State = entry.State,
					Installable = IsInstallable(entry),
					UnavailableReason = GetUnavailableReason(entry),
					Retracted = entry.Retracted
				})
				.ToArray()
		};
	}

	private bool IsInstallable(ImmModUpdateEntry entry)
	{
		return entry.SourceType == EnumModSourceType.ZIP
			&& (!Api.Server.Config.HostedMode || (Api.Server.Config.HostedModeAllowMods && entry.HostedInstallAllowed));
	}

	private string GetUnavailableReason(ImmModUpdateEntry entry)
	{
		if (Api.Server.Config.HostedMode && (!Api.Server.Config.HostedModeAllowMods || !entry.HostedInstallAllowed)) { return "integratedmodmanager:updates-unavailable-hosted"; }
		if (entry.SourceType != EnumModSourceType.ZIP) { return "integratedmodmanager:updates-unavailable-development"; }

		return "";
	}

	private async Task<string> FetchChangelogAsync(string modId, string targetVersion, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await Http.GetAsync("mod/" + Uri.EscapeDataString(modId), cancellationToken).ConfigureAwait(false);
		string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) { throw new HttpRequestException($"ModDB returned HTTP {(int)response.StatusCode}."); }

		JToken root = JToken.Parse(json);
		JObject[] objects = EnumerateObjects(root).ToArray();
		JObject? versionMatch = objects.FirstOrDefault(obj => string.Equals(GetString(obj, "modidstr", "modid", "identifier"), modId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(GetString(obj, "modversion", "version"), targetVersion, StringComparison.OrdinalIgnoreCase));

		versionMatch ??= objects.FirstOrDefault(obj => string.Equals(GetString(obj, "modversion", "version"), targetVersion, StringComparison.OrdinalIgnoreCase)
			&& obj.Properties().Any(property => string.Equals(property.Name, "changelog", StringComparison.OrdinalIgnoreCase)));

		string html = GetString(versionMatch, "changelog");
		return SanitizeChangelog(html);
	}

	private static string SanitizeChangelog(string html)
	{
		if (string.IsNullOrWhiteSpace(html)) { return ""; }

		string text = BrRegex.Replace(html, "\n");
		text = ListItemStartRegex.Replace(text, "• ");
		text = ListItemEndRegex.Replace(text, "\n");
		text = ParagraphEndRegex.Replace(text, "\n\n");
		text = HtmlTagRegex.Replace(text, "");
		text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Replace('\r', '\n');
		text = ExcessBlankLinesRegex.Replace(text, "\n\n").Trim();

		return text.Length <= ChangelogLimit ? text : text[..ChangelogLimit].TrimEnd() + "\n…";
	}

	private static IEnumerable<JObject> EnumerateObjects(JToken token)
	{
		if (token is JObject obj) { yield return obj; }
		if (token is not JContainer container) { yield break; }

		foreach (JToken child in container.Children())
		{
			foreach (JObject descendant in EnumerateObjects(child)) { yield return descendant; }
		}
	}

	private static string GetString(JObject? obj, params string[] names)
	{
		if (obj == null) { return ""; }

		foreach (string name in names)
		{
			JProperty? property = obj.Property(name, StringComparison.OrdinalIgnoreCase);
			if (property?.Value.Type == JTokenType.String) { return property.Value.Value<string>() ?? ""; }
		}

		return "";
	}

	private bool TryGetExactEntry(string modId, string targetVersion, out ImmModUpdateEntry? entry)
	{
		if (Entries.TryGetValue(modId ?? "", out entry) && string.Equals(entry.TargetVersion, targetVersion, StringComparison.OrdinalIgnoreCase)) { return true; }

		entry = null;
		return false;
	}

	private static bool TryGetInstallInformation(Dictionary<string, ModInstallInformation> data, string modId, out ModInstallInformation? information)
	{
		if (data.TryGetValue(modId, out information)) { return true; }

		KeyValuePair<string, ModInstallInformation> match = data.FirstOrDefault(pair => string.Equals(pair.Key, modId, StringComparison.OrdinalIgnoreCase));
		information = match.Value;
		return information != null;
	}

	private static bool IsRetracted(ModInstallInformation information)
	{
		return information.ErrorCode == RetractedErrorCode
			|| information.ErrorCode == ForcedRetractedErrorCode
			|| !string.IsNullOrWhiteSpace(information.RetractionReason);
	}

	private static bool IsBuiltInMod(string modId)
	{
		return string.Equals(modId, "game", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(modId, "creative", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(modId, "survival", StringComparison.OrdinalIgnoreCase);
	}

	private static bool CanManage(IServerPlayer player) { return player.HasPrivilege(Privilege.controlserver); }

	private static string EntryKey(string modId, string targetVersion) { return modId + "@" + targetVersion; }

	private static ImmModUpdateInstallResponse CreateInstallResponse(ImmModUpdateEntry entry, bool success, string error)
	{
		return new ImmModUpdateInstallResponse
		{
			Success = success,
			Error = error,
			ModId = entry.ModId,
			TargetVersion = entry.TargetVersion,
			State = entry.State
		};
	}

	public void Dispose()
	{
		if (Disposed) { return; }
		Disposed = true;

		Api.Event.UnregisterEventBusListener(OnConfigChanged);
		Cancellation.Cancel();
		Cancellation.Dispose();
		Http.Dispose();

		Entries.Clear();
		ChangelogTasks.Clear();
		InstallRequestsInFlight.Clear();
		LocalClientSnapshots = null;
	}
}

public sealed class ImmModUpdateClient : IDisposable
{
	private readonly ICoreClientAPI Api;
	private readonly IClientNetworkChannel Channel;

	public event Action<ImmModUpdateSummaryResponse>? SummaryReceived;
	public event Action<ImmModUpdateCatalogResponse>? CatalogReceived;
	public event Action<ImmModUpdateInstallResponse>? InstallReceived;
	public event Action<ImmModUpdateChangelogResponse>? ChangelogReceived;

	public ImmModUpdateClient(ICoreClientAPI api)
	{
		Api = api;
		Channel = api.Network.GetChannel(ImmModUpdateNetwork.NetworkChannelCode)
			.SetMessageHandler<ImmModUpdateSummaryResponse>(packet => Dispatch(() => SummaryReceived?.Invoke(packet), "summary"))
			.SetMessageHandler<ImmModUpdateCatalogResponse>(packet => Dispatch(() => CatalogReceived?.Invoke(packet), "catalog"))
			.SetMessageHandler<ImmModUpdateInstallResponse>(packet => Dispatch(() => InstallReceived?.Invoke(packet), "install"))
			.SetMessageHandler<ImmModUpdateChangelogResponse>(packet => Dispatch(() => ChangelogReceived?.Invoke(packet), "changelog"));

		Api.Event.LevelFinalize += OnLevelFinalize;
	}

	private void OnLevelFinalize()
	{
		if (!Api.IsSinglePlayer) { return; }

		ImmLocalInstalledModPacket[] mods = Api.ModLoader.Mods
			.Where(mod => mod.Info != null && !string.IsNullOrWhiteSpace(mod.Info.ModID) && !string.IsNullOrWhiteSpace(mod.Info.Version))
			.GroupBy(mod => mod.Info.ModID, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.Select(mod => new ImmLocalInstalledModPacket
			{
				ModId = mod.Info.ModID,
				Name = string.IsNullOrWhiteSpace(mod.Info.Name) ? mod.Info.ModID : mod.Info.Name,
				Version = mod.Info.Version,
				SourceType = mod.SourceType
			})
			.ToArray();

		Channel.SendPacket(new ImmModUpdateLocalInventoryPacket { Mods = mods });
	}

	public void RequestSummary() { Channel.SendPacket(new ImmModUpdateSummaryRequest()); }
	public void RequestCatalog(bool requestFullCheck) { Channel.SendPacket(new ImmModUpdateCatalogRequest { RequestFullCheck = requestFullCheck }); }
	public void RequestInstall(string modId, string targetVersion) { Channel.SendPacket(new ImmModUpdateInstallRequest { ModId = modId, TargetVersion = targetVersion }); }
	public void RequestChangelog(string modId, string targetVersion) { Channel.SendPacket(new ImmModUpdateChangelogRequest { ModId = modId, TargetVersion = targetVersion }); }

	private void Dispatch(Action action, string suffix) { Api.Event.EnqueueMainThreadTask(action, "integratedmodmanager-update-" + suffix); }

	public void Dispose()
	{
		Api.Event.LevelFinalize -= OnLevelFinalize;

		SummaryReceived = null;
		CatalogReceived = null;
		InstallReceived = null;
		ChangelogReceived = null;
	}
}

public static class ImmModUpdateNetwork
{
	public const string NetworkChannelCode = "integratedmodmanager-updates";

	public static void RegisterNetwork(ICoreAPI api)
	{
		api.Network.RegisterChannel(NetworkChannelCode)
			.RegisterMessageType<ImmModUpdateLocalInventoryPacket>()
			.RegisterMessageType<ImmLocalInstalledModPacket>()
			.RegisterMessageType<ImmModUpdateSummaryRequest>()
			.RegisterMessageType<ImmModUpdateSummaryResponse>()
			.RegisterMessageType<ImmModUpdateCatalogRequest>()
			.RegisterMessageType<ImmModUpdateCatalogResponse>()
			.RegisterMessageType<ImmModUpdatePacket>()
			.RegisterMessageType<ImmModUpdateInstallRequest>()
			.RegisterMessageType<ImmModUpdateInstallResponse>()
			.RegisterMessageType<ImmModUpdateChangelogRequest>()
			.RegisterMessageType<ImmModUpdateChangelogResponse>();
	}

	public static void StartServer(ICoreServerAPI api, ImmModUpdateService service)
	{
		IServerNetworkChannel channel = api.Network.GetChannel(NetworkChannelCode);

		channel.SetMessageHandler<ImmModUpdateLocalInventoryPacket>((player, packet) => service.ReceiveLocalInventory(player, packet));
		channel.SetMessageHandler<ImmModUpdateSummaryRequest>((player, packet) => channel.SendPacket(service.BuildSummary(player), player));
		channel.SetMessageHandler<ImmModUpdateCatalogRequest>((player, packet) => channel.SendPacket(service.BuildCatalog(player, packet.RequestFullCheck), player));
		channel.SetMessageHandler<ImmModUpdateInstallRequest>((player, packet) => service.RequestInstall(player, packet, response => channel.SendPacket(response, player)));
		channel.SetMessageHandler<ImmModUpdateChangelogRequest>((player, packet) => service.RequestChangelog(player, packet, response => channel.SendPacket(response, player)));
	}
}

[ProtoContract]
public sealed class ImmModUpdateLocalInventoryPacket
{
	[ProtoMember(1)] public ImmLocalInstalledModPacket[] Mods = Array.Empty<ImmLocalInstalledModPacket>();
}

[ProtoContract]
public sealed class ImmLocalInstalledModPacket
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public string Name = "";
	[ProtoMember(3)] public string Version = "";
	[ProtoMember(4)] public EnumModSourceType SourceType;
}

[ProtoContract]
public sealed class ImmModUpdateSummaryRequest
{
	[ProtoMember(1)] public byte Request = 1;
}

[ProtoContract]
public sealed class ImmModUpdateSummaryResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public ImmModUpdateCheckState State;
	[ProtoMember(4)] public int AvailableCount;
	[ProtoMember(5)] public int PendingRestartCount;
	[ProtoMember(6)] public bool FullCacheReady;
	[ProtoMember(7)] public int RetractedCount;
}

[ProtoContract]
public sealed class ImmModUpdateCatalogRequest
{
	[ProtoMember(1)] public bool RequestFullCheck;
}

[ProtoContract]
public sealed class ImmModUpdateCatalogResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public ImmModUpdateCheckState State;
	[ProtoMember(4)] public ImmModUpdatePacket[] Updates = Array.Empty<ImmModUpdatePacket>();
	[ProtoMember(5)] public bool FullCacheReady;
}

[ProtoContract]
public sealed class ImmModUpdatePacket
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public string Name = "";
	[ProtoMember(3)] public string InstalledVersion = "";
	[ProtoMember(4)] public string TargetVersion = "";
	[ProtoMember(5)] public EnumModSourceType SourceType;
	[ProtoMember(6)] public ImmModUpdateEntryState State;
	[ProtoMember(7)] public bool Installable;
	[ProtoMember(8)] public string UnavailableReason = "";
	[ProtoMember(9)] public bool Retracted;
}

[ProtoContract]
public sealed class ImmModUpdateInstallRequest
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public string TargetVersion = "";
}

[ProtoContract]
public sealed class ImmModUpdateInstallResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public string ModId = "";
	[ProtoMember(4)] public string TargetVersion = "";
	[ProtoMember(5)] public ImmModUpdateEntryState State;
}

[ProtoContract]
public sealed class ImmModUpdateChangelogRequest
{
	[ProtoMember(1)] public string ModId = "";
	[ProtoMember(2)] public string TargetVersion = "";
}

[ProtoContract]
public sealed class ImmModUpdateChangelogResponse
{
	[ProtoMember(1)] public bool Success;
	[ProtoMember(2)] public string Error = "";
	[ProtoMember(3)] public string ModId = "";
	[ProtoMember(4)] public string TargetVersion = "";
	[ProtoMember(5)] public string Changelog = "";
}

internal sealed class ModInstallInformationResponse
{
	public string? Error;
	public Dictionary<string, ModInstallInformation>? Data;
}

internal sealed class ModInstallInformation
{
	public int ErrorCode;
	public string? RetractionReason;
	public string? RecommendedUpgrade;
}
