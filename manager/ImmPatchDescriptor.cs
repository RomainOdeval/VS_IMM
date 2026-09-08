#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmPatchTiming
{
	Auto,
	Early,
	BeforePatches,
	AfterPatches
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmPatchOperation
{
	Replace,
	Append
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmPatchResultType
{
	Auto,
	Boolean,
	Integer,
	Decimal,
	String,
	Json
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmPatchSide
{
	Auto,
	Both,
	Server,
	Client
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmReloadRequirement
{
	None,
	ReenterWorld,
	ServerRestart
}

public sealed class ImmContentPatchTarget
{
	public string Asset = "";
	public string AssetPattern = "";

	public string Path = "";
	public List<string> Paths = new();

	public ImmPatchOperation Operation = ImmPatchOperation.Replace;
	public ImmPatchTiming Timing = ImmPatchTiming.Auto;
	public ImmPatchResultType ResultType = ImmPatchResultType.Auto;
	public ImmPatchSide Side = ImmPatchSide.Auto;

	public string Expression = "";
	public string Condition = "";
	public JToken? Value;

	public bool Optional;
}
