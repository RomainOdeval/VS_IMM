#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

public sealed class ImmRegisteredDependency
{
	public int RuntimeId { get; }
	public string SourceModId { get; }
	public int EntryIndex { get; }
	public ImmDependencyEntry Entry { get; }

	public ImmRegisteredDependency(int runtimeId, string sourceModId, int entryIndex, ImmDependencyEntry entry)
	{
		RuntimeId = runtimeId;
		SourceModId = sourceModId;
		EntryIndex = entryIndex;
		Entry = entry;
	}
}

public sealed class ImmRegisteredDescriptor
{
	public string ModId { get; }
	public int LoadOrder { get; }
	public ImmConfigDescriptor Descriptor { get; }

	public ImmRegisteredDescriptor(string modId, int loadOrder, ImmConfigDescriptor descriptor)
	{
		ModId = modId;
		LoadOrder = loadOrder;
		Descriptor = descriptor;
	}
}

public sealed class ImmConfigRegistry
{
	private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal) { "Boolean", "String", "Integer", "Decimal", "Slider", "Dropdown", "Array" };
	private readonly Dictionary<string, ImmConfigDescriptor> Descriptors = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<ImmRegisteredDescriptor> RegisteredDescriptorList = new();
	private readonly List<ImmRegisteredDependency> RegisteredDependencies = new();
	private readonly Dictionary<int, ImmRegisteredDependency> DependenciesByRuntimeId = new();
	public IReadOnlyList<ImmRegisteredDescriptor> RegisteredDescriptors => RegisteredDescriptorList;
	public IReadOnlyList<ImmRegisteredDependency> Dependencies => RegisteredDependencies;

	public void Discover(ICoreAPI api)
	{
		Descriptors.Clear();
		RegisteredDescriptorList.Clear();
		RegisteredDependencies.Clear();
		DependenciesByRuntimeId.Clear();

		int nextRuntimeId = 1;
		int loadOrder = 0;

		foreach (Mod mod in api.ModLoader.Mods)
		{
			string? modId = mod.Info?.ModID;

			if (string.IsNullOrWhiteSpace(modId)) { loadOrder++; continue; }

			IAsset? asset = api.Assets.TryGet(new AssetLocation(modId, "config/imm.json"));
			if (asset == null) { loadOrder++; continue; }

			try
			{
				ImmConfigDescriptor descriptor = ParseDescriptor(asset.ToText());
				ValidateDescriptor(modId, descriptor);

				Descriptors[modId] = descriptor;
				RegisteredDescriptorList.Add(new ImmRegisteredDescriptor(modId, loadOrder, descriptor));

				for (int entryIndex = 0; entryIndex < descriptor.Dependencies.Count; entryIndex++)
				{
					ImmDependencyEntry entry = descriptor.Dependencies[entryIndex];
					entry.RuntimeId = nextRuntimeId++;
					ImmRegisteredDependency registered = new(entry.RuntimeId, modId, entryIndex, entry);
					RegisteredDependencies.Add(registered);
					DependenciesByRuntimeId[registered.RuntimeId] = registered;
				}
			}
			catch (Exception exception) { api.Logger.Error("[integratedmodmanager] Failed to load {0}:config/imm.json: {1}", modId, exception.Message); }

			loadOrder++;
		}
	}

	public bool TryGet(string modId, out ImmConfigDescriptor descriptor) { return Descriptors.TryGetValue(modId, out descriptor!); }

	public bool TryGetPatchSetting(string modId, string code, out ImmConfigBlock block, out ImmConfigEntry entry, out ImmConfigSide effectiveSide)
	{
		block = null!;
		entry = null!;
		effectiveSide = ImmConfigSide.Server;

		if (!TryGet(modId, out ImmConfigDescriptor descriptor)) { return false; }

		foreach (ImmConfigBlock candidateBlock in descriptor.Configuration)
		{
			if (candidateBlock.ConfigSource != ImmConfigSource.PatchSettings) { continue; }

			foreach (ImmConfigEntry candidateEntry in candidateBlock.Settings)
			{
				if (!string.Equals(candidateEntry.Code, code, StringComparison.Ordinal)) { continue; }

				block = candidateBlock;
				entry = candidateEntry;
				effectiveSide = candidateEntry.ConfigSide ?? candidateBlock.ConfigSide;

				return true;
			}
		}

		return false;
	}

	public bool TryGetDependency(int runtimeId, out ImmRegisteredDependency dependency) { return DependenciesByRuntimeId.TryGetValue(runtimeId, out dependency!); }

	private static ImmConfigDescriptor ParseDescriptor(string text)
	{
		using StringReader stringReader = new(text);
		using JsonTextReader jsonReader = new(stringReader) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };

		JsonSerializer serializer = JsonSerializer.CreateDefault();
		return serializer.Deserialize<ImmConfigDescriptor>(jsonReader) ?? throw new InvalidDataException("Descriptor was empty.");
	}

	private static void ValidateDescriptor(string modId, ImmConfigDescriptor descriptor)
	{
		descriptor.Configuration ??= new List<ImmConfigBlock>();
		descriptor.Dependencies ??= new List<ImmDependencyEntry>();
		descriptor.Constants ??= new Dictionary<string, JToken>();
		descriptor.ContentPatches ??= new List<ImmContentPatchTarget>();

		if (descriptor.Configuration.Count == 0 && descriptor.Dependencies.Count == 0 && descriptor.ContentPatches.Count == 0)
		{
			throw new InvalidDataException("Descriptor must contain Configuration, Dependencies, or ContentPatches.");
		}

		HashSet<string> constantCodes = new(StringComparer.Ordinal);

		foreach (KeyValuePair<string, JToken> pair in descriptor.Constants)
		{
			if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
			{
				throw new InvalidDataException("Constants require non-empty codes and non-null JSON values.");
			}

			if (!constantCodes.Add(pair.Key))
			{
				throw new InvalidDataException($"Constant '{pair.Key}' is duplicated.");
			}
		}

		HashSet<string> patchSettingCodes = new(StringComparer.Ordinal);

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++)
		{
			ImmConfigBlock? block = descriptor.Configuration[blockIndex];

			if (block == null)
			{
				throw new InvalidDataException($"Configuration[{blockIndex}] cannot be null.");
			}

			if (block.ConfigSource != ImmConfigSource.PatchSettings) { continue; }

			block.Settings ??= new List<ImmConfigEntry>();

			for (int entryIndex = 0; entryIndex < block.Settings.Count; entryIndex++)
			{
				ImmConfigEntry? entry = block.Settings[entryIndex];

				if (entry == null)
				{
					throw new InvalidDataException($"Configuration[{blockIndex}].Settings[{entryIndex}] cannot be null.");
				}

				if (string.IsNullOrWhiteSpace(entry.Code))
				{
					throw new InvalidDataException($"Configuration[{blockIndex}].Settings[{entryIndex}].Code is required for PatchSettings.");
				}

				if (!patchSettingCodes.Add(entry.Code))
				{
					throw new InvalidDataException($"PatchSetting Code '{entry.Code}' is duplicated in {modId}:config/imm.json.");
				}

				if (constantCodes.Contains(entry.Code))
				{
					throw new InvalidDataException($"PatchSetting Code '{entry.Code}' conflicts with a Constant of the same name.");
				}
			}
		}

		for (int blockIndex = 0; blockIndex < descriptor.Configuration.Count; blockIndex++) { ValidateBlock(descriptor.Configuration[blockIndex], blockIndex, patchSettingCodes, constantCodes); }
		for (int patchIndex = 0; patchIndex < descriptor.ContentPatches.Count; patchIndex++)
		{
			ImmContentPatchTarget? target = descriptor.ContentPatches[patchIndex];

			if (target == null)
			{
				throw new InvalidDataException($"ContentPatches[{patchIndex}] cannot be null.");
			}

			ValidatePatchTarget(target, $"ContentPatches[{patchIndex}]", hasOwningSetting: false, patchSettingCodes, constantCodes);
		}

		for (int entryIndex = 0; entryIndex < descriptor.Dependencies.Count; entryIndex++)
		{
			ImmDependencyEntry? entry = descriptor.Dependencies[entryIndex];

			if (entry == null)
			{
				throw new InvalidDataException($"Dependencies[{entryIndex}] cannot be null.");
			}

			ValidateDependency(entry, $"Dependencies[{entryIndex}]");
		}
	}

	private static void ValidateBlock(ImmConfigBlock block, int blockIndex, HashSet<string> patchSettingCodes, HashSet<string> constantCodes)
	{
		string prefix = $"Configuration[{blockIndex}]";

		if (!Enum.IsDefined(typeof(ImmConfigSource), block.ConfigSource))
		{
			throw new InvalidDataException($"{prefix}.ConfigSource is not supported.");
		}

		if (!Enum.IsDefined(typeof(ImmConfigSide), block.ConfigSide))
		{
			throw new InvalidDataException($"{prefix}.ConfigSide is not supported.");
		}

		if (string.IsNullOrWhiteSpace(block.ConfigLabel))
		{
			throw new InvalidDataException($"{prefix}.ConfigLabel is required.");
		}

		if (block.ConfigSource == ImmConfigSource.ModConfig)
		{
			if (string.IsNullOrWhiteSpace(block.ConfigFile))
			{
				throw new InvalidDataException($"{prefix}.ConfigFile is required.");
			}

			ValidateConfigPath(block.ConfigFile, prefix);
		}
		else
		{
			if (!string.IsNullOrWhiteSpace(block.ConfigFile))
			{
				throw new InvalidDataException($"{prefix}.ConfigFile must be omitted for PatchSettings.");
			}

			if (block.ParseDescriptions)
			{
				throw new InvalidDataException($"{prefix}.ParseDescriptions is only available for ModConfig blocks.");
			}
		}

		block.Settings ??= new List<ImmConfigEntry>();
		HashSet<string> maps = new(StringComparer.Ordinal);

		for (int entryIndex = 0; entryIndex < block.Settings.Count; entryIndex++)
		{
			ImmConfigEntry? entry = block.Settings[entryIndex];

			if (entry == null)
			{
				throw new InvalidDataException($"{prefix}.Settings[{entryIndex}] cannot be null.");
			}

			string entryPrefix = $"{prefix}.Settings[{entryIndex}]";

			ValidateEntry(entry, block.ConfigSource, entryPrefix, patchSettingCodes, constantCodes);

			if (block.ConfigSource == ImmConfigSource.ModConfig && !maps.Add(entry.Map))
			{
				throw new InvalidDataException($"{entryPrefix}.Map '{entry.Map}' is duplicated.");
			}
		}
	}

	private static void ValidateEntry(ImmConfigEntry entry, ImmConfigSource source, string prefix, HashSet<string> patchSettingCodes, HashSet<string> constantCodes)
	{
		if (!SupportedTypes.Contains(entry.Type))
		{
			throw new InvalidDataException($"{prefix}.Type '{entry.Type}' is not supported.");
		}

		if (string.IsNullOrWhiteSpace(entry.Label))
		{
			throw new InvalidDataException($"{prefix}.Label is required.");
		}

		if (entry.ConfigSide.HasValue && !Enum.IsDefined(typeof(ImmConfigSide), entry.ConfigSide.Value))
		{
			throw new InvalidDataException($"{prefix}.ConfigSide is not supported.");
		}

		entry.Options ??= new List<ImmConfigOption>();

		entry.Targets ??= new List<ImmContentPatchTarget>();

		ValidateControlMetadata(entry, source, prefix);

		if (source == ImmConfigSource.ModConfig)
		{
			if (string.IsNullOrWhiteSpace(entry.Map))
			{
				throw new InvalidDataException($"{prefix}.Map is required.");
			}

			ValidateMap(entry.Map, $"{prefix}.Map");

			if (!string.IsNullOrWhiteSpace(entry.Code) || entry.Default != null || entry.Targets.Count > 0)
			{
				throw new InvalidDataException($"{prefix} ModConfig settings cannot declare Code, Default, or Targets.");
			}

			return;
		}

		if (string.IsNullOrWhiteSpace(entry.Code))
		{
			throw new InvalidDataException($"{prefix}.Code is required for PatchSettings.");
		}

		if (!string.IsNullOrWhiteSpace(entry.Map))
		{
			throw new InvalidDataException($"{prefix}.Map must be omitted for PatchSettings.");
		}

		if (entry.Default == null)
		{
			throw new InvalidDataException($"{prefix}.Default is required for PatchSettings.");
		}

		if (!ImmConfigValueValidator.TryNormalizePatchSetting(entry, entry.Default, out JToken normalizedDefault, out string defaultError))
		{
			throw new InvalidDataException($"{prefix}.Default is invalid: {defaultError}");
		}

		entry.Default = normalizedDefault;

		for (int targetIndex = 0; targetIndex < entry.Targets.Count; targetIndex++)
		{
			ImmContentPatchTarget? target = entry.Targets[targetIndex];

			if (target == null)
			{
				throw new InvalidDataException($"{prefix}.Targets[{targetIndex}] cannot be null.");
			}

			ValidatePatchTarget(target, $"{prefix}.Targets[{targetIndex}]", hasOwningSetting: true, patchSettingCodes, constantCodes);
		}
	}

	private static void ValidateControlMetadata(ImmConfigEntry entry, ImmConfigSource source, string prefix)
	{
		if (entry.Type == "Slider")
		{
			if (!entry.Min.HasValue || !entry.Max.HasValue)
			{
				throw new InvalidDataException($"{prefix} Slider requires Min and Max.");
			}

			if (!double.IsFinite(entry.Min.Value) || !double.IsFinite(entry.Max.Value) || entry.Max.Value <= entry.Min.Value)
			{
				throw new InvalidDataException($"{prefix}.Max must be finite and greater than Min.");
			}

			if (entry.Step.HasValue && (!double.IsFinite(entry.Step.Value) || entry.Step.Value <= 0))
			{
				throw new InvalidDataException($"{prefix}.Step must be finite and greater than zero.");
			}
		}

		if (entry.Type == "Array")
		{
			if (entry.ElementType is not ("Boolean" or "Integer" or "Decimal" or "String"))
			{
				throw new InvalidDataException($"{prefix} Array requires ElementType Boolean, Integer, Decimal, or String.");
			}
		}

		if (entry.Type != "Dropdown") { return; }

		if (entry.Options.Count == 0)
		{
			throw new InvalidDataException($"{prefix} Dropdown requires at least one Option.");
		}

		for (int optionIndex = 0; optionIndex < entry.Options.Count; optionIndex++)
		{
			ImmConfigOption? option = entry.Options[optionIndex];

			if (option == null || string.IsNullOrWhiteSpace(option.Label))
			{
				throw new InvalidDataException($"{prefix}.Options[{optionIndex}] requires Label.");
			}

			if (source == ImmConfigSource.ModConfig && (option.Value == null || option.Value is JContainer || option.Value.Type == JTokenType.Null))
			{
				throw new InvalidDataException($"{prefix}.Options[{optionIndex}].Value must be a primitive for ModConfig.");
			}
		}
	}

	private static void ValidatePatchTarget(ImmContentPatchTarget target, string prefix, bool hasOwningSetting, HashSet<string> patchSettingCodes, HashSet<string> constantCodes)
	{
		target.Paths ??= new List<string>();

		bool hasAsset = !string.IsNullOrWhiteSpace(target.Asset);
		bool hasPattern = !string.IsNullOrWhiteSpace(target.AssetPattern);

		if (hasAsset == hasPattern)
		{
			throw new InvalidDataException($"{prefix} requires exactly one of Asset or AssetPattern.");
		}

		string assetText = hasAsset ? target.Asset : target.AssetPattern;

		if (assetText.IndexOf(':') <= 0 || assetText.EndsWith(":", StringComparison.Ordinal))
		{
			throw new InvalidDataException($"{prefix} asset references must include an explicit domain.");
		}

		bool hasPath = !string.IsNullOrWhiteSpace(target.Path);
		bool hasPaths = target.Paths.Count > 0;

		if (hasPath == hasPaths)
		{
			throw new InvalidDataException($"{prefix} requires exactly one of Path or Paths.");
		}

		IEnumerable<string> paths = hasPath ? new[] { target.Path } : target.Paths;

		int pathIndex = 0;

		foreach (string path in paths)
		{
			try { ImmContentPath.Compile(path); }
			catch (Exception exception)
			{
				throw new InvalidDataException($"{prefix} path {pathIndex} is invalid: {exception.Message}");
			}

			pathIndex++;
		}

		if (!Enum.IsDefined(typeof(ImmPatchOperation), target.Operation) || !Enum.IsDefined(typeof(ImmPatchTiming), target.Timing) || !Enum.IsDefined(typeof(ImmPatchResultType), target.ResultType) || !Enum.IsDefined(typeof(ImmPatchSide), target.Side))
		{
			throw new InvalidDataException($"{prefix} contains an unsupported patch option.");
		}

		bool hasExpression = !string.IsNullOrWhiteSpace(target.Expression);

		bool hasValue = target.Value != null;

		if (hasExpression && hasValue)
		{
			throw new InvalidDataException($"{prefix} cannot declare both Expression and Value.");
		}

		if (!hasOwningSetting && !hasExpression && !hasValue)
		{
			throw new InvalidDataException($"{prefix} requires Expression or Value because it has no owning PatchSetting.");
		}

		if (hasExpression) { ValidatePatchExpression(target.Expression, $"{prefix}.Expression", hasOwningSetting, patchSettingCodes, constantCodes, allowCurrent: true); }
		if (!string.IsNullOrWhiteSpace(target.Condition)) { ValidatePatchExpression(target.Condition, $"{prefix}.Condition", hasOwningSetting, patchSettingCodes, constantCodes, allowCurrent: false); }
	}

	private static void ValidatePatchExpression(string source, string prefix, bool hasOwningSetting, HashSet<string> patchSettingCodes, HashSet<string> constantCodes, bool allowCurrent)
	{
		ImmPatchExpression expression;

		try { expression = ImmPatchExpression.Compile(source); }
		catch (Exception exception) { throw new InvalidDataException($"{prefix} is invalid: {exception.Message}"); }

		if (expression.UsesOwningSetting && !hasOwningSetting) { throw new InvalidDataException($"{prefix} uses Setting but the patch has no owning PatchSetting."); }
		if (!allowCurrent && expression.UsesCurrentValue) { throw new InvalidDataException($"{prefix} cannot reference Current/value because conditions are evaluated before asset/path resolution."); }

		foreach (string code in expression.SettingReferences)
		{
			if (!patchSettingCodes.Contains(code)) { throw new InvalidDataException($"{prefix} references unknown PatchSetting '{code}'."); }
		}

		foreach (string code in expression.ConstantReferences)
		{
			if (!constantCodes.Contains(code)) { throw new InvalidDataException($"{prefix} references unknown Constant '{code}'."); }
		}

		foreach (string code in expression.BareReferences)
		{
			if (!patchSettingCodes.Contains(code) && !constantCodes.Contains(code)) { throw new InvalidDataException($"{prefix} contains unknown identifier '{code}'."); }
		}
	}

	private static void ValidateDependency(ImmDependencyEntry entry, string prefix)
	{
		if (string.IsNullOrWhiteSpace(entry.ModId))
		{
			throw new InvalidDataException($"{prefix}.ModId is required.");
		}

		if (string.IsNullOrWhiteSpace(entry.Label))
		{
			throw new InvalidDataException($"{prefix}.Label is required.");
		}

		if (!Enum.IsDefined(typeof(ImmDependencySeverity), entry.Severity))
		{
			throw new InvalidDataException($"{prefix}.Severity is not supported.");
		}

		entry.Criteria ??= new List<ImmDependencyCriterion>();

		for (int criterionIndex = 0; criterionIndex < entry.Criteria.Count; criterionIndex++)
		{
			ImmDependencyCriterion? criterion = entry.Criteria[criterionIndex];

			if (criterion == null)
			{
				throw new InvalidDataException($"{prefix}.Criteria[{criterionIndex}] cannot be null.");
			}

			ValidateCriterion(criterion, $"{prefix}.Criteria[{criterionIndex}]");
		}

		if (entry.Resolution != null) { ValidateResolution(entry.Resolution, $"{prefix}.Resolution"); }
	}

	private static void ValidateCriterion(ImmDependencyCriterion criterion, string prefix)
	{
		if (criterion.Type == ImmDependencyCriterionType.Unknown || !Enum.IsDefined(typeof(ImmDependencyCriterionType), criterion.Type))
		{
			throw new InvalidDataException($"{prefix}.Type is not supported.");
		}

		if (!Enum.IsDefined(typeof(ImmDependencyOperator), criterion.Operator))
		{
			throw new InvalidDataException($"{prefix}.Operator is not supported.");
		}

		switch (criterion.Type)
		{
			case ImmDependencyCriterionType.Setting:
				ValidateComparisonOperator(criterion.Operator, $"{prefix}.Operator");

				if (criterion.Target == null)
				{
					throw new InvalidDataException($"{prefix}.Target is required for Setting.");
				}

				bool patchSetting = ValidateSettingTarget(criterion.Target, $"{prefix}.Target");

				ValidateSettingComparisonValue(criterion.Value, criterion.Operator, patchSetting, $"{prefix}.Value");
			break;

			case ImmDependencyCriterionType.GridRecipeCount:
				ValidateComparisonOperator(criterion.Operator, $"{prefix}.Operator");
				ValidateAssetCode(criterion.Output, $"{prefix}.Output");

				if (criterion.Value == null || criterion.Value.Type != JTokenType.Integer || criterion.Value.Value<long>() < 0)
				{
					throw new InvalidDataException($"{prefix}.Value must be a non-negative integer.");
				}
			break;

			case ImmDependencyCriterionType.HasModID:
				if (criterion.Value == null || criterion.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(criterion.Value.Value<string>()))
				{
					throw new InvalidDataException($"{prefix}.Value must be a non-empty mod ID string.");
				}

				if (criterion.Operator != ImmDependencyOperator.Equal)
				{
					throw new InvalidDataException($"{prefix}.Operator must be Equal for HasModID.");
				}
			break;

			case ImmDependencyCriterionType.CollectibleBehavior:
				ValidatePresenceOperator(criterion.Operator, $"{prefix}.Operator");
				ValidateCollectibleCriterionTarget(criterion.Target, $"{prefix}.Target");

				if (string.IsNullOrWhiteSpace(criterion.Behavior))
				{
					throw new InvalidDataException($"{prefix}.Behavior is required for CollectibleBehavior.");
				}

				if (!string.IsNullOrWhiteSpace(criterion.Path))
				{
					throw new InvalidDataException($"{prefix}.Path must be omitted for CollectibleBehavior.");
				}

				if (criterion.Value != null)
				{
					throw new InvalidDataException($"{prefix}.Value must be omitted for CollectibleBehavior.");
				}
			break;

			case ImmDependencyCriterionType.CollectibleAttribute:
				ValidateCollectibleCriterionTarget(criterion.Target, $"{prefix}.Target");

				if (!string.IsNullOrWhiteSpace(criterion.Behavior))
				{
					throw new InvalidDataException($"{prefix}.Behavior must be omitted for CollectibleAttribute.");
				}

				if (string.IsNullOrWhiteSpace(criterion.Path))
				{
					throw new InvalidDataException($"{prefix}.Path is required for CollectibleAttribute.");
				}

				try { criterion.CompiledPath = ImmContentPath.Compile(criterion.Path); }
				catch (Exception exception)
				{
					throw new InvalidDataException($"{prefix}.Path is invalid: {exception.Message}");
				}

				if (criterion.Operator is ImmDependencyOperator.Exists or ImmDependencyOperator.NotExists)
				{
					if (criterion.Value != null)
					{
						throw new InvalidDataException($"{prefix}.Value must be omitted for {criterion.Operator}.");
					}
				}
				else
				{
					ValidateComparisonOperator(criterion.Operator, $"{prefix}.Operator");

					if (criterion.Value == null)
					{
						throw new InvalidDataException($"{prefix}.Value is required for {criterion.Operator}.");
					}

					if ((criterion.Operator is ImmDependencyOperator.GreaterThan or ImmDependencyOperator.LessThan) && !IsNumeric(criterion.Value))
					{
						throw new InvalidDataException($"{prefix}.Value must be numeric for {criterion.Operator}.");
					}
				}
			break;
		}
	}

	private static void ValidateResolution(ImmDependencyResolution resolution, string prefix)
	{
		if (resolution.Type == ImmDependencyResolutionType.Unknown || !Enum.IsDefined(typeof(ImmDependencyResolutionType), resolution.Type))
		{
			throw new InvalidDataException($"{prefix}.Type is not supported.");
		}

		switch (resolution.Type)
		{
			case ImmDependencyResolutionType.SetSetting:
				if (resolution.Target == null)
				{
					throw new InvalidDataException($"{prefix}.Target is required for SetSetting.");
				}

				bool patchSetting = ValidateSettingTarget(resolution.Target, $"{prefix}.Target");

				if (resolution.Value == null)
				{
					throw new InvalidDataException($"{prefix}.Value is required.");
				}

				if (!patchSetting) { ValidatePrimitiveValue(resolution.Value, $"{prefix}.Value"); }
			break;

			case ImmDependencyResolutionType.InstallMod:
				if (string.IsNullOrWhiteSpace(resolution.ModId))
				{
					throw new InvalidDataException($"{prefix}.ModId is required for InstallMod.");
				}
			break;

			case ImmDependencyResolutionType.RunCommand:
				if (resolution.Value == null || resolution.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(resolution.Value.Value<string>()))
				{
					throw new InvalidDataException($"{prefix}.Value must be a non-empty command string for RunCommand.");
				}
			break;
		}
	}

	private static void ValidateComparisonOperator(ImmDependencyOperator comparison, string prefix)
	{
		if (comparison is not (ImmDependencyOperator.Equal or ImmDependencyOperator.NotEqual or ImmDependencyOperator.GreaterThan or ImmDependencyOperator.LessThan))
		{
			throw new InvalidDataException($"{prefix} must be Equal, NotEqual, GreaterThan, or LessThan.");
		}
	}

	private static void ValidatePresenceOperator(ImmDependencyOperator comparison, string prefix)
	{
		if (comparison is not (ImmDependencyOperator.Exists or ImmDependencyOperator.NotExists))
		{
			throw new InvalidDataException($"{prefix} must be Exists or NotExists.");
		}
	}

	private static void ValidateCollectibleCriterionTarget(ImmDependencyCriterionTarget? target, string prefix)
	{
		if (target == null)
		{
			throw new InvalidDataException($"{prefix} is required.");
		}

		if (!target.Class.HasValue || !Enum.IsDefined(typeof(ImmDependencyCollectibleClass), target.Class.Value))
		{
			throw new InvalidDataException($"{prefix}.Class must be Item or Block.");
		}

		ValidateAssetCode(target.Code, $"{prefix}.Code");

		if (!string.IsNullOrWhiteSpace(target.ConfigFile) || !string.IsNullOrWhiteSpace(target.Map) || !string.IsNullOrWhiteSpace(target.ModId) || !string.IsNullOrWhiteSpace(target.PatchSetting))
		{
			throw new InvalidDataException($"{prefix} collectible targets cannot declare ConfigFile, Map, ModId, or PatchSetting.");
		}
	}

	// Returns true when the target refers to an IMM PatchSetting.
	private static bool ValidateSettingTarget(ImmDependencySettingTarget target, string prefix)
	{
		if (target is ImmDependencyCriterionTarget criterionTarget && (criterionTarget.Class.HasValue || !string.IsNullOrWhiteSpace(criterionTarget.Code)))
		{
			throw new InvalidDataException($"{prefix} setting targets cannot declare Class or Code.");
		}

		bool hasPatchSetting = !string.IsNullOrWhiteSpace(target.PatchSetting);

		bool hasConfig = !string.IsNullOrWhiteSpace(target.ConfigFile) || !string.IsNullOrWhiteSpace(target.Map);

		if (hasPatchSetting == hasConfig)
		{
			throw new InvalidDataException($"{prefix} requires either PatchSetting or ConfigFile + Map.");
		}

		if (hasPatchSetting)
		{
			if (string.IsNullOrWhiteSpace(target.PatchSetting))
			{
				throw new InvalidDataException($"{prefix}.PatchSetting is required.");
			}

			return true;
		}

		if (string.IsNullOrWhiteSpace(target.ConfigFile) || string.IsNullOrWhiteSpace(target.Map))
		{
			throw new InvalidDataException($"{prefix} requires both ConfigFile and Map.");
		}

		ValidateConfigPath(target.ConfigFile, prefix);
		ValidateMap(target.Map, $"{prefix}.Map");

		return false;
	}

	private static void ValidateSettingComparisonValue(JToken? value, ImmDependencyOperator comparison, bool patchSetting, string prefix)
	{
		if (value == null)
		{
			throw new InvalidDataException($"{prefix} is required.");
		}

		if (comparison is ImmDependencyOperator.GreaterThan or ImmDependencyOperator.LessThan)
		{
			if (!IsNumeric(value))
			{
				throw new InvalidDataException($"{prefix} must be numeric for {comparison}.");
			}

			return;
		}

		if (!patchSetting) { ValidatePrimitiveValue(value, prefix); }
	}

	private static void ValidateAssetCode(string code, string prefix)
	{
		int separatorIndex = string.IsNullOrWhiteSpace(code) ? -1 : code.IndexOf(':');

		if (separatorIndex <= 0 || separatorIndex == code.Length - 1)
		{
			throw new InvalidDataException($"{prefix} must include an explicit asset domain.");
		}
	}

	private static void ValidatePrimitiveValue(JToken? value, string prefix)
	{
		if (value == null || value is JContainer || value.Type is JTokenType.Null or JTokenType.Undefined)
		{
			throw new InvalidDataException($"{prefix} must be a non-null primitive.");
		}
	}

	private static bool IsNumeric(JToken value) { return value.Type is JTokenType.Integer or JTokenType.Float; }

	private static void ValidateConfigPath(string configFile, string prefix)
	{
		if (Path.IsPathRooted(configFile) || configFile.Contains(':'))
		{
			throw new InvalidDataException($"{prefix}.ConfigFile must be relative to ModConfig.");
		}

		string[] segments = configFile.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (segments.Length == 0 || Array.Exists(segments, segment => segment is "." or ".."))
		{
			throw new InvalidDataException($"{prefix}.ConfigFile contains an invalid path.");
		}

		if (!configFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"{prefix}.ConfigFile must point to a JSON file.");
		}
	}

	private static void ValidateMap(string map, string prefix)
	{
		try { new JObject().SelectToken(map); }
		catch (Exception exception)
		{
			throw new InvalidDataException($"{prefix} is not a valid JPath: {exception.Message}");
		}
	}
}
