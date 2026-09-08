#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

internal sealed class ImmConfigKitOwnershipDetector : IImmExternalManagerDetector
{
	private const string ManagerId = "configkit";
	private const string ManagerName = "ConfigKit";
	private const string SystemName = "ConfigKit.ConfigKitModSystem";

	public void Detect(ICoreAPI api, ImmExternalManagerOwnership ownership)
	{
		if (!api.ModLoader.IsModEnabled(ManagerId)) { return; }

		ModSystem? system;

		try { system = api.ModLoader.GetModSystem(SystemName); }
		catch (Exception exception)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigKit is enabled but its ownership API could not be accessed: {0}", exception.Message);
			return;
		}

		if (system == null)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigKit is enabled but its mod system was not found.");
			return;
		}

		HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

		try
		{
			dynamic configKit = system;

			foreach (Mod mod in api.ModLoader.Mods)
			{
				string? modId = mod.Info?.ModID;
				if (string.IsNullOrWhiteSpace(modId) || !(bool)configKit.WillManage(modId)) { continue; }

				claimed.Add(modId);
			}
		}
		catch (Exception exception)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigKit ownership detection failed. ConfigKit 1.3.5 or newer is required for automatic ownership coordination: {0}", exception.Message);
			return;
		}

		string[] claimedMods = claimed.OrderBy(modId => modId, StringComparer.OrdinalIgnoreCase).ToArray();

		foreach (string modId in claimedMods) { ownership.AddClaim(ManagerId, ManagerName, modId); }

		if (claimedMods.Length == 0) { api.Logger.Notification("[integratedmodmanager] ConfigKit detected. No loaded mod domains are currently managed by ConfigKit."); }
		else { api.Logger.Notification("[integratedmodmanager] ConfigKit detected. IMM will cede ownership for the following domains: {0}.", string.Join(", ", claimedMods)); }
	}
}
