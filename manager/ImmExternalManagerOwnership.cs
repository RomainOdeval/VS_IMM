#nullable enable

using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

public sealed record ImmExternalManagerClaim(string ManagerId, string ManagerName, string ModId);

internal interface IImmExternalManagerDetector
{
	void Detect(ICoreAPI api, ImmExternalManagerOwnership ownership);
}

public sealed class ImmExternalManagerOwnership
{
	private readonly Dictionary<string, List<ImmExternalManagerClaim>> Claims = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> ActiveManagers = new(StringComparer.OrdinalIgnoreCase);

	public bool AnyManagerActive => ActiveManagers.Count > 0;

	public void Discover(ICoreAPI api)
	{
		Claims.Clear();
		ActiveManagers.Clear();

		IImmExternalManagerDetector[] detectors = { new ImmConfigLibOwnershipDetector(), new ImmConfigKitOwnershipDetector() };

		foreach (IImmExternalManagerDetector detector in detectors)
		{
			try { detector.Detect(api, this); }
			catch (Exception exception) { api.Logger.Warning("[integratedmodmanager] External mod manager detection failed: {0}", exception.Message); }
		}
	}

	public bool IsControlled(string modId) { return !string.IsNullOrWhiteSpace(modId) && Claims.ContainsKey(modId); }

	public IReadOnlyList<ImmExternalManagerClaim> GetClaims(string modId)
	{
		if (string.IsNullOrWhiteSpace(modId) || !Claims.TryGetValue(modId, out List<ImmExternalManagerClaim>? claims)) { return Array.Empty<ImmExternalManagerClaim>(); }
		return claims;
	}

	public string GetPrimaryManagerName(ICoreAPI api, string modId)
	{
		if (string.IsNullOrWhiteSpace(modId) || !Claims.TryGetValue(modId, out List<ImmExternalManagerClaim>? claims) || claims.Count == 0) { return ""; }

		ImmExternalManagerClaim claim = claims[0];
		return api.ModLoader.GetMod(claim.ManagerId)?.Info?.Name ?? claim.ManagerName;
	}

	internal void RegisterManager(string managerId)
	{
		if (!string.IsNullOrWhiteSpace(managerId)) { ActiveManagers.Add(managerId); }
	}

	internal void AddClaim(string managerId, string managerName, string modId)
	{
		if (string.IsNullOrWhiteSpace(managerId) || string.IsNullOrWhiteSpace(modId)) { return; }

		RegisterManager(managerId);

		if (!Claims.TryGetValue(modId, out List<ImmExternalManagerClaim>? claims))
		{
			claims = new List<ImmExternalManagerClaim>();
			Claims[modId] = claims;
		}

		if (claims.Exists(claim => string.Equals(claim.ManagerId, managerId, StringComparison.OrdinalIgnoreCase))) { return; }

		claims.Add(new ImmExternalManagerClaim(managerId, managerName, modId));
	}
}
