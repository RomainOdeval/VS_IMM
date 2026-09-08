#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace IntegratedModManager.Config;

public sealed class ImmContentPatchService
{
	public const string BootstrapWorldConfigKey = "integratedmodmanager:patchsettings-active";

	private const string BootstrapEncodingPrefix = "b64:";
	private const double BooleanEpsilon = 1e-10;

	private readonly ICoreAPI Api;
	private readonly ImmConfigRegistry Registry;
	private readonly ImmPatchSettingsStore SettingsStore;
	private readonly ImmExternalManagerOwnership ExternalManagers;

	private readonly Dictionary<string, Dictionary<string, JToken>> ActiveValues = new(StringComparer.OrdinalIgnoreCase);

	private readonly List<CompiledBinding> Bindings = new();

	private readonly HashSet<PatchSettingKey> PatchSettingsAffectingContent = new();

	public ImmContentPatchService(ICoreAPI api, ImmConfigRegistry registry, ImmPatchSettingsStore settingsStore, ImmExternalManagerOwnership externalManagers)
	{
		Api = api;
		Registry = registry;
		SettingsStore = settingsStore;
		ExternalManagers = externalManagers;
	}

	public void Initialize()
	{
		ActiveValues.Clear();
		Bindings.Clear();
		PatchSettingsAffectingContent.Clear();

		if (Api.Side == EnumAppSide.Server)
		{
			CaptureServerValues();
			PublishBootstrapSnapshot();
		}
		else { CaptureClientValues(); }

		CompileBindings();
	}

	public bool TryGetActiveValue(string modId, string code, out JToken value)
	{
		value = JValue.CreateNull();

		if (!ActiveValues.TryGetValue(modId, out Dictionary<string, JToken>? values) || !values.TryGetValue(code, out JToken? active)) { return false; }

		value = active.DeepClone();
		return true;
	}

	public bool IsPendingReload(string modId, ImmConfigEntry entry, ImmConfigSide side, JToken savedEffective)
	{
		if (ExternalManagers.IsControlled(modId)) { return false; }
		if (!PatchSettingsAffectingContent.Contains(new PatchSettingKey(modId, entry.Code, side))) { return false; }

		if (!TryGetActiveValue(modId, entry.Code, out JToken active)) { return false; }

		return !JToken.DeepEquals(active, savedEffective);
	}

	public bool HasPendingReload(string modId, ImmConfigSide side)
	{
		if (ExternalManagers.IsControlled(modId)) { return false; }
		if (!Registry.TryGet(modId, out ImmConfigDescriptor descriptor)) { return false; }
		JObject document = SettingsStore.ReadDocument(modId);

		foreach (ImmConfigBlock block in descriptor.Configuration)
		{
			if (block.ConfigSource != ImmConfigSource.PatchSettings) { continue; }

			foreach (ImmConfigEntry entry in block.Settings)
			{
				ImmConfigSide effectiveSide = entry.ConfigSide ?? block.ConfigSide;
				if (effectiveSide != side) { continue; }

				JToken saved = SettingsStore.GetEffectiveValue(modId, entry, effectiveSide, document);
				if (IsPendingReload(modId, entry, effectiveSide, saved)) { return true; }
			}
		}

		return false;
	}

	public void Apply(ImmPatchTiming stage)
	{
		List<ResolvedBinding> resolved = new();

		Dictionary<string, AssetLocation[]> patternMatches = new(StringComparer.Ordinal);

		foreach (CompiledBinding binding in Bindings)
		{
			if ((binding.Stage != stage && binding.Stage != ImmPatchTiming.Auto) || !ShouldRunOnThisSide(binding)) { continue; }

			try
			{
				if (!EvaluateCondition(binding)) { continue; }
			}
			catch (Exception exception)
			{
				if (!binding.Target.Optional) { Api.Logger.Warning("[integratedmodmanager] Content patch {0} in {1}:config/imm.json condition failed: {2}", binding.Description, binding.SourceModId, exception.Message); }
				continue;
			}

			ResolveAssets(binding, stage, patternMatches, resolved);
		}

		if (resolved.Count == 0) { return; }

		Dictionary<AssetLocation, List<CompiledBinding>> grouped = new();

		foreach (ResolvedBinding entry in resolved.OrderBy(item => item.Binding.Sequence).ThenBy(item => item.Location.ToString(), StringComparer.Ordinal))
		{
			if (!grouped.TryGetValue(entry.Location, out List<CompiledBinding>? list))
			{
				list = new List<CompiledBinding>();
				grouped[entry.Location] = list;
			}

			list.Add(entry.Binding);
		}

		int changedAssets = 0;
		int changedValues = 0;
		int failures = 0;

		foreach (KeyValuePair<AssetLocation, List<CompiledBinding>> item in grouped.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
		{
			IAsset? asset;

			try { asset = Api.Assets.TryGet(item.Key); }
			catch (Exception exception)
			{
				Api.Logger.Warning("[integratedmodmanager] Failed to load content patch asset '{0}': {1}", item.Key, exception.Message);

				failures++;
				continue;
			}

			if (asset == null)
			{
				if (item.Value.Any(binding => !binding.Target.Optional)) { Api.Logger.Warning("[integratedmodmanager] Content patch asset '{0}' was not found on {1}.", item.Key, Api.Side); }

				continue;
			}

			JToken root;

			try { root = JToken.Parse(asset.ToText()); }
			catch (Exception exception)
			{
				Api.Logger.Warning("[integratedmodmanager] Could not parse content patch asset '{0}': {1}", item.Key, exception.Message);

				failures++;
				continue;
			}

			bool assetChanged = false;

			foreach (CompiledBinding binding in item.Value.OrderBy(entry => entry.Sequence))
			{
				try
				{
					int changes = ApplyBinding(binding, root);

					if (changes > 0)
					{
						assetChanged = true;
						changedValues += changes;
					}
				}
				catch (Exception exception)
				{
					if (!binding.Target.Optional) { Api.Logger.Warning("[integratedmodmanager] Content patch {0} in {1}:config/imm.json failed for '{2}' at '{3}': {4}", binding.Description, binding.SourceModId, item.Key, binding.PathText, exception.Message); }

					failures++;
				}
			}

			if (!assetChanged) { continue; }

			try
			{
				asset.Data = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));

				asset.IsPatched = true;
				changedAssets++;
			}
			catch (Exception exception)
			{
				Api.Logger.Warning("[integratedmodmanager] Failed to serialize content patch asset '{0}': {1}", item.Key, exception.Message);

				failures++;
			}
		}

		if (changedAssets > 0 || failures > 0) { Api.Logger.Notification("[integratedmodmanager] Content patches {0}: changed {1} values across {2} assets, {3} failures.", stage, changedValues, changedAssets, failures); }
	}

	private void CaptureServerValues()
	{
		foreach (ImmRegisteredDescriptor registered in Registry.RegisteredDescriptors)
		{
			if (ExternalManagers.IsControlled(registered.ModId)) { continue; }

			JObject document = SettingsStore.ReadDocument(registered.ModId);
			Dictionary<string, JToken> values = GetOrCreateActiveValues(registered.ModId);

			foreach ((ImmConfigBlock block, ImmConfigEntry entry, ImmConfigSide side) in EnumeratePatchSettings(registered.Descriptor))
			{
				if (side != ImmConfigSide.Server) { continue; }

				values[entry.Code] = SettingsStore.GetEffectiveValue(registered.ModId, entry, side, document);
			}
		}
	}

	private void CaptureClientValues()
	{
		JObject bootstrap = ReadBootstrapSnapshot();

		foreach (ImmRegisteredDescriptor registered in Registry.RegisteredDescriptors)
		{
			if (ExternalManagers.IsControlled(registered.ModId)) { continue; }

			JObject document = SettingsStore.ReadDocument(registered.ModId);
			Dictionary<string, JToken> values = GetOrCreateActiveValues(registered.ModId);

			foreach ((ImmConfigBlock block, ImmConfigEntry entry, ImmConfigSide side) in EnumeratePatchSettings(registered.Descriptor))
			{
				if (side == ImmConfigSide.Client) { values[entry.Code] = SettingsStore.GetEffectiveValue(registered.ModId, entry, side, document); continue; }

				JToken? snapshotValue = bootstrap[registered.ModId]?[entry.Code];
				if (snapshotValue == null) { Api.Logger.Warning("[integratedmodmanager] Server PatchSetting '{0}:{1}' was not present in the active bootstrap snapshot; client content targets using it will be skipped.", registered.ModId, entry.Code); continue; }

				if (!ImmConfigValueValidator.TryNormalizePatchSetting(entry, snapshotValue, out JToken normalized, out string error)) { Api.Logger.Warning("[integratedmodmanager] Server PatchSetting '{0}:{1}' in the active bootstrap snapshot is invalid: {2}", registered.ModId, entry.Code, error); continue; }
				values[entry.Code] = normalized;
			}
		}
	}

	private void PublishBootstrapSnapshot() // Legacy support & repair stuff should be removed by version 1.1.0
	{
		ITreeAttribute? worldConfig = Api.World?.Config;

		if (worldConfig == null) { Api.Logger.Warning("[integratedmodmanager] World.Config was unavailable while publishing active PatchSettings."); return; }

		string previousValue = worldConfig.GetAsString(BootstrapWorldConfigKey, "");
		bool migratingLegacy = !string.IsNullOrWhiteSpace(previousValue) && !previousValue.StartsWith(BootstrapEncodingPrefix, StringComparison.Ordinal);
		JObject snapshot = new();

		foreach (KeyValuePair<string, Dictionary<string, JToken>> mod in ActiveValues)
		{
			if (mod.Value.Count == 0) { continue; }

			JObject values = new();
			foreach (KeyValuePair<string, JToken> setting in mod.Value) { values[setting.Key] = setting.Value.DeepClone(); }

			if (values.Count > 0) { snapshot[mod.Key] = values; }
		}

		if (snapshot.Count == 0) { worldConfig.RemoveAttribute(BootstrapWorldConfigKey); }
		else
		{
			string json = snapshot.ToString(Formatting.None);
			string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
			worldConfig.SetString(BootstrapWorldConfigKey, BootstrapEncodingPrefix + encoded);
		}

		if (migratingLegacy) { Api.Logger.Notification("[integratedmodmanager] Fixed encoding issues in the file!"); }
	}

	private JObject ReadBootstrapSnapshot()
	{
		string? value = Api.World?.Config?.GetAsString(BootstrapWorldConfigKey, "");

		if (string.IsNullOrWhiteSpace(value)) { return new JObject(); }

		try
		{
			if (value.StartsWith(BootstrapEncodingPrefix, StringComparison.Ordinal))
			{
				string json = Encoding.UTF8.GetString(Convert.FromBase64String(value[BootstrapEncodingPrefix.Length..]));
				return JObject.Parse(json);
			}

			// Keep legacy raw JSON readable so old worlds and backups can load once and be rewritten server-side using the safe encoding.
			return JObject.Parse(value);
		}
		catch (Exception exception) { Api.Logger.Warning("[integratedmodmanager] Active PatchSettings bootstrap snapshot could not be parsed: {0}", exception.Message); return new JObject(); }
	}

	private void CompileBindings()
	{
		int sequence = 0;

		foreach (ImmRegisteredDescriptor registered in Registry.RegisteredDescriptors.OrderBy(item => item.LoadOrder))
		{
			if (ExternalManagers.IsControlled(registered.ModId)) { continue; }

			ImmConfigDescriptor descriptor = registered.Descriptor;
			Dictionary<string, JToken> constants = descriptor.Constants.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.Ordinal);

			for (int patchIndex = 0; patchIndex < descriptor.ContentPatches.Count; patchIndex++) { AddTargetBindings(registered.ModId, descriptor.ContentPatches[patchIndex], owningSettingCode: null, owningSettingSide: null, constants, $"ContentPatches[{patchIndex}]", ref sequence); }
			for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
			{
				ImmConfigBlock block = descriptor.Configuration[blockIndex];

				if (block.ConfigSource != ImmConfigSource.PatchSettings) { continue; }

				for (int entryIndex = 0; entryIndex < block.Settings.Count; entryIndex++)
				{
					ImmConfigEntry entry = block.Settings[entryIndex];
					ImmConfigSide side = entry.ConfigSide ?? block.ConfigSide;

					for (int targetIndex = 0; targetIndex < entry.Targets.Count; targetIndex++) { AddTargetBindings(registered.ModId, entry.Targets[targetIndex], entry.Code, side, constants, $"Configuration[{blockIndex}].Settings[{entryIndex}].Targets[{targetIndex}]", ref sequence); }
				}
			}
		}
	}

	private void AddTargetBindings(string sourceModId, ImmContentPatchTarget target, string? owningSettingCode, ImmConfigSide? owningSettingSide, Dictionary<string, JToken> constants, string description, ref int sequence)
	{
		ImmPatchTiming stage = ResolveTiming(target);
		ImmPatchExpression? expression = string.IsNullOrWhiteSpace(target.Expression) ? null : ImmPatchExpression.Compile(target.Expression);
		ImmPatchExpression? condition = string.IsNullOrWhiteSpace(target.Condition) ? null : ImmPatchExpression.Compile(target.Condition);

		RegisterSettingDependencies(sourceModId, target, expression, condition, owningSettingCode, owningSettingSide);
		IEnumerable<string> paths = !string.IsNullOrWhiteSpace(target.Path) ? new[] { target.Path } : target.Paths;

		foreach (string path in paths) { Bindings.Add(new CompiledBinding(sourceModId, target, ImmContentPath.Compile(path), path, expression, condition, target.Value?.DeepClone(), owningSettingCode, owningSettingSide, constants, stage, sequence++, description)); }
	}

	private void ResolveAssets(CompiledBinding binding, ImmPatchTiming stage, Dictionary<string, AssetLocation[]> patternMatches, List<ResolvedBinding> result)
	{
		if (!string.IsNullOrWhiteSpace(binding.Target.Asset))
		{
			AssetLocation location = new(binding.Target.Asset);

			if (!CategoryExistsOnThisSide(location) || (binding.Stage == ImmPatchTiming.Auto && ResolveTiming(location) != stage)) { return; }
			result.Add(new ResolvedBinding(binding, location));

			return;
		}

		string pattern = binding.Target.AssetPattern;

		if (!patternMatches.TryGetValue(pattern, out AssetLocation[]? allMatches))
		{
			allMatches = GetPatternCandidates(pattern).Where(location => CategoryExistsOnThisSide(location) && WildcardUtil.Match(pattern, location.ToString())).OrderBy(location => location.ToString(), StringComparer.Ordinal).ToArray();
			patternMatches[pattern] = allMatches;
		}

		if (allMatches.Length == 0 && !binding.Target.Optional && (binding.Stage != ImmPatchTiming.Auto || stage == ImmPatchTiming.AfterPatches)) { Api.Logger.Warning("[integratedmodmanager] Content patch AssetPattern '{0}' from {1}:config/imm.json matched no assets on {2}.", pattern, binding.SourceModId, Api.Side); }

		IEnumerable<AssetLocation> matches = binding.Stage == ImmPatchTiming.Auto ? allMatches.Where(location => ResolveTiming(location) == stage) : allMatches;

		foreach (AssetLocation location in matches) { result.Add(new ResolvedBinding(binding, location)); }
	}

	private int ApplyBinding(CompiledBinding binding, JToken root)
	{
		if (binding.OwningSettingCode != null && (!ActiveValues.TryGetValue(binding.SourceModId, out Dictionary<string, JToken>? values) || !values.ContainsKey(binding.OwningSettingCode)))
		{
			// A server-owned value missing from the client bootstrap is deliberately not replaced by a local default.
			return 0;
		}

		IReadOnlyList<JToken> matches = binding.Path.Resolve(root);

		if (matches.Count == 0)
		{
			if (!binding.Target.Optional) { Api.Logger.Warning("[integratedmodmanager] Content patch path '{0}' from {1}:config/imm.json matched no values.", binding.PathText, binding.SourceModId); }

			return 0;
		}

		if (binding.Target.Operation == ImmPatchOperation.Append)
		{
			List<(JArray Array, JToken Value)> appends = new(matches.Count);

			// Build the complete change set before mutating the JSON tree. A failure on any match therefore leaves this binding untouched.
			foreach (JToken current in matches)
			{
				if (current is not JArray array)
				{
					throw new InvalidOperationException("Append targets must resolve to JSON arrays.");
				}

				JToken requested = EvaluateRequestedValue(binding, current);
				JToken appendValue = ConvertResult(requested, current, binding.Target.ResultType, forAppend: true);

				appends.Add((array, appendValue));
			}

			foreach ((JArray array, JToken value) in appends) { array.Add(value.DeepClone()); }

			return appends.Count;
		}

		List<(JToken Target, JToken Value)> replacements = new(matches.Count);

		// As with Append, evaluate and convert every match first.
		// This prevents a failed multi-match binding from leaking partial edits into a later successful binding on the same asset.
		foreach (JToken current in matches)
		{
			JToken requested = EvaluateRequestedValue(binding, current);
			JToken replacement = ConvertResult(requested, current, binding.Target.ResultType, forAppend: false);

			if (JToken.DeepEquals(current, replacement)) { continue; }

			replacements.Add((current, replacement));
		}

		foreach ((JToken target, JToken value) in replacements) { target.Replace(value); }

		return replacements.Count;
	}

	private bool EvaluateCondition(CompiledBinding binding)
	{
		if (binding.Condition == null) { return true; }

		Dictionary<string, JToken> settings = ActiveValues.TryGetValue(binding.SourceModId, out Dictionary<string, JToken>? values) ? values : new Dictionary<string, JToken>(StringComparer.Ordinal);
		JToken? owningSetting = null;

		if (binding.OwningSettingCode != null && settings.TryGetValue(binding.OwningSettingCode, out JToken? value)) { owningSetting = value; }
		return binding.Condition.EvaluateBoolean(new ImmPatchExpressionContext { Setting = owningSetting?.DeepClone(), Settings = settings, Constants = binding.Constants });
	}

	private JToken EvaluateRequestedValue(CompiledBinding binding, JToken current)
	{
		Dictionary<string, JToken> settings = ActiveValues.TryGetValue(binding.SourceModId, out Dictionary<string, JToken>? values) ? values : new Dictionary<string, JToken>(StringComparer.Ordinal);
		JToken? owningSetting = null;

		if (binding.OwningSettingCode != null && settings.TryGetValue(binding.OwningSettingCode, out JToken? value)) { owningSetting = value; }
		if (binding.Expression != null) { return binding.Expression.Evaluate(new ImmPatchExpressionContext { Setting = owningSetting?.DeepClone(), Current = current.DeepClone(), Settings = settings, Constants = binding.Constants }); }
		if (binding.StaticValue != null) { return binding.StaticValue.DeepClone(); }
		if (owningSetting != null) { return owningSetting.DeepClone(); }

		throw new InvalidOperationException("Content patch has no value source.");
	}

	private static JToken ConvertResult(JToken requested, JToken current, ImmPatchResultType resultType, bool forAppend)
	{
		ImmPatchResultType effective = resultType;

		if (effective == ImmPatchResultType.Auto)
		{
			if (forAppend) { effective = ImmPatchResultType.Json; }
			else { effective = current.Type switch { JTokenType.Boolean => ImmPatchResultType.Boolean, JTokenType.Integer => ImmPatchResultType.Integer, JTokenType.Float => ImmPatchResultType.Decimal, JTokenType.String => ImmPatchResultType.String, _ => ImmPatchResultType.Json }; }
		}

		switch (effective)
		{
			case ImmPatchResultType.Boolean:
				if (requested.Type == JTokenType.Boolean) { return new JValue(requested.Value<bool>()); }

				if (TryGetNumber(requested, out double booleanNumber)) { return new JValue(Math.Abs(booleanNumber) > BooleanEpsilon); }

			throw new InvalidOperationException("Boolean patch result must be Boolean or numeric.");

			case ImmPatchResultType.Integer:
				if (!TryGetNumber(requested, out double integerNumber) || integerNumber < int.MinValue || integerNumber > int.MaxValue)
				{
					throw new InvalidOperationException("Integer patch result must be a finite Int32-range number.");
				}

			return new JValue((int)integerNumber);

			case ImmPatchResultType.Decimal:
				if (!TryGetNumber(requested, out double decimalNumber))
				{
					throw new InvalidOperationException("Decimal patch result must be finite and numeric.");
				}

			return new JValue(decimalNumber);

			case ImmPatchResultType.String:
				if (requested.Type != JTokenType.String)
				{
					throw new InvalidOperationException("String patch result must be a JSON string.");
				}

			return new JValue(requested.Value<string>());

			case ImmPatchResultType.Json:
				EnsureFiniteJson(requested);
			return requested.DeepClone();

			default: throw new InvalidOperationException("Unsupported content patch result type.");
		}
	}

	private static bool TryGetNumber(JToken token, out double value)
	{
		value = 0;

		if (token.Type == JTokenType.Boolean) { value = token.Value<bool>() ? 1 : 0; return true; }

		if (token.Type is not (JTokenType.Integer or JTokenType.Float)) { return false; }

		try { value = token.Value<double>(); return double.IsFinite(value); }
		catch { return false; }
	}

	private static void EnsureFiniteJson(JToken token)
	{
		Stack<JToken> pending = new();
		pending.Push(token);

		while (pending.Count > 0)
		{
			JToken current = pending.Pop();

			if (current.Type == JTokenType.Float && !double.IsFinite(current.Value<double>()))
			{
				throw new InvalidOperationException("Patch result contains a non-finite number.");
			}

			if (current is not JContainer container) { continue; }

			foreach (JToken child in container.Children()) { pending.Push(child); }
		}
	}

	private bool ShouldRunOnThisSide(CompiledBinding binding)
	{
		bool sideAllowed = binding.Target.Side switch { ImmPatchSide.Server => Api.Side == EnumAppSide.Server, ImmPatchSide.Client => Api.Side == EnumAppSide.Client, ImmPatchSide.Both => true, _ => binding.OwningSettingSide != ImmConfigSide.Client || Api.Side == EnumAppSide.Client };

		return sideAllowed && ReferencesAvailable(binding, binding.Expression) && ReferencesAvailable(binding, binding.Condition);
	}

	private bool ReferencesAvailable(CompiledBinding binding, ImmPatchExpression? expression)
	{
		if (expression == null) { return true; }

		ActiveValues.TryGetValue(binding.SourceModId, out Dictionary<string, JToken>? active);

		foreach (string code in expression.SettingReferences)
		{
			if (active?.ContainsKey(code) != true) { return false; }
		}

		foreach (string code in expression.BareReferences)
		{
			if (binding.Constants.ContainsKey(code)) { continue; }
			if (active?.ContainsKey(code) != true) { return false; }
		}

		if (expression.UsesOwningSetting && binding.OwningSettingCode != null && active?.ContainsKey(binding.OwningSettingCode) != true) { return false; }

		return true;
	}

	private bool CategoryExistsOnThisSide(AssetLocation location)
	{
		AssetCategory? category = location.Category;

		if (category == null) { return true; }
		return (category.SideType & Api.Side) != (EnumAppSide)0;
	}

	private void RegisterSettingDependencies(string sourceModId, ImmContentPatchTarget target, ImmPatchExpression? expression, ImmPatchExpression? condition, string? owningSettingCode, ImmConfigSide? owningSettingSide)
	{
		bool usesOwningSetting = owningSettingCode != null && owningSettingSide.HasValue && (expression?.UsesOwningSetting == true || condition?.UsesOwningSetting == true || (expression == null && target.Value == null));

		if (usesOwningSetting) { PatchSettingsAffectingContent.Add(new PatchSettingKey(sourceModId, owningSettingCode!, owningSettingSide!.Value)); }

		IEnumerable<string> referencedCodes = (expression?.SettingReferences ?? Array.Empty<string>())
			.Concat(expression?.BareReferences ?? Array.Empty<string>())
			.Concat(condition?.SettingReferences ?? Array.Empty<string>())
			.Concat(condition?.BareReferences ?? Array.Empty<string>());

		foreach (string code in referencedCodes.Distinct(StringComparer.Ordinal))
		{
			if (Registry.TryGetPatchSetting(sourceModId, code, out _, out _, out ImmConfigSide side)) { PatchSettingsAffectingContent.Add(new PatchSettingKey(sourceModId, code, side)); }
		}
	}

	private IEnumerable<AssetLocation> GetPatternCandidates(string pattern)
	{
		if (pattern.StartsWith("@", StringComparison.Ordinal)) { return Api.Assets.AllAssets.Keys; }
		int colon = pattern.IndexOf(':');

		if (colon <= 0) { return Api.Assets.AllAssets.Keys; }
		string domain = pattern[..colon].ToLowerInvariant();

		if (domain.Contains('*')) { return Api.Assets.AllAssets.Keys; }

		string pathPattern = pattern[(colon + 1)..];
		int wildcard = pathPattern.IndexOf('*');
		string prefix = (wildcard >= 0 ? pathPattern[..wildcard] : pathPattern).ToLowerInvariant();

		return Api.Assets.GetLocations(prefix, domain);
	}

	private static ImmPatchTiming ResolveTiming(ImmContentPatchTarget target)
	{
		if (target.Timing != ImmPatchTiming.Auto) { return target.Timing; }

		string source = !string.IsNullOrWhiteSpace(target.Asset) ? target.Asset : target.AssetPattern;

		if (!string.IsNullOrWhiteSpace(target.AssetPattern) && source.StartsWith("@", StringComparison.Ordinal))
		{
			// WildcardUtil treats a leading @ as a regular expression. Its category can therefore span multiple lifecycle stages.
			return ImmPatchTiming.Auto;
		}

		int colon = source.IndexOf(':');
		string path = colon >= 0 ? source[(colon + 1)..] : source;
		int slash = path.IndexOf('/');
		string category = slash >= 0 ? path[..slash] : path;

		if (!string.IsNullOrWhiteSpace(target.AssetPattern) && category.Contains('*'))
		{
			// A wildcard category can span several lifecycle stages.
			// Keep the binding dynamic and classify each matched asset when that stage actually executes.
			return ImmPatchTiming.Auto;
		}

		return ResolveTiming(category);
	}

	private static ImmPatchTiming ResolveTiming(AssetLocation location)
	{
		string category = location.Category?.Code ?? "";

		if (string.IsNullOrWhiteSpace(category))
		{
			string path = location.Path;
			int slash = path.IndexOf('/');

			category = slash >= 0 ? path[..slash] : path;
		}

		return ResolveTiming(category);
	}

	private static ImmPatchTiming ResolveTiming(string category)
	{
		if (string.Equals(category, "compatibility", StringComparison.OrdinalIgnoreCase)) { return ImmPatchTiming.Early; }
		if (string.Equals(category, "patches", StringComparison.OrdinalIgnoreCase)) { return ImmPatchTiming.BeforePatches; }

		return ImmPatchTiming.AfterPatches;
	}

	private Dictionary<string, JToken>
		GetOrCreateActiveValues(string modId)
	{
		if (!ActiveValues.TryGetValue(modId, out Dictionary<string, JToken>? values))
		{
			values = new Dictionary<string, JToken>(StringComparer.Ordinal);
			ActiveValues[modId] = values;
		}

		return values;
	}

	private static IEnumerable<(ImmConfigBlock Block, ImmConfigEntry Entry, ImmConfigSide Side)> EnumeratePatchSettings(ImmConfigDescriptor descriptor)
	{
		foreach (ImmConfigBlock block in descriptor.Configuration)
		{
			if (block.ConfigSource != ImmConfigSource.PatchSettings) { continue; }

			foreach (ImmConfigEntry entry in block.Settings) { yield return (block, entry, entry.ConfigSide ?? block.ConfigSide); }
		}
	}

	private readonly record struct PatchSettingKey(string ModId, string Code, ImmConfigSide Side);
	private sealed record CompiledBinding(string SourceModId, ImmContentPatchTarget Target, ImmContentPath Path, string PathText, ImmPatchExpression? Expression, ImmPatchExpression? Condition, JToken? StaticValue, string? OwningSettingCode, ImmConfigSide? OwningSettingSide, Dictionary<string, JToken> Constants, ImmPatchTiming Stage, int Sequence, string Description);
	private sealed record ResolvedBinding(CompiledBinding Binding, AssetLocation Location);
}
