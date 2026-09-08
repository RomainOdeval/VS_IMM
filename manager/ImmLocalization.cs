#nullable enable

using System;
using System.Linq;
using Vintagestory.API.Config;

namespace IntegratedModManager.Config;

public static class ImmLocalization
{
	private const string Domain = IntegratedModManagerSystem.ModId;

	public static string Get(string key, params object[] args) { return Lang.Get($"{Domain}:{key}", args); }

	public static string GetForLanguage(string languageCode, string key, params object[] args) { return Lang.GetL(languageCode, $"{Domain}:{key}", args); }

	public static string Resolve(string? text)
	{
		if (string.IsNullOrEmpty(text)) { return ""; }
		if (!LooksLikeLocalizationKey(text)) { return text; }

		return Lang.Get(text);
	}

	public static string ResolveForLanguage(string? text, string languageCode)
	{
		if (string.IsNullOrEmpty(text)) { return ""; }
		if (!LooksLikeLocalizationKey(text)) { return text; }

		return Lang.GetL(languageCode, text);
	}

	private static bool LooksLikeLocalizationKey(string text)
	{
		int colon = text.IndexOf(':');

		if (colon <= 0 || colon != text.LastIndexOf(':') || colon == text.Length - 1 || text.Contains("://", StringComparison.Ordinal)) { return false; }

		for (int index = 0; index < colon; index++)
		{
			char character = text[index];
			if (!char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.') { return false; }
		}

		for (int index = colon + 1; index < text.Length; index++)
		{
			char character = text[index];
			if (char.IsWhiteSpace(character) || character is '<' or '>' or '"' or '\'' or '=') { return false; }
		}

		return true;
	}

	public static void LocalizePage(ImmConfigPageResponse page)
	{
		LocalizePage(page, Resolve, (key, args) => Get(key, args));
	}

	public static void LocalizePage(ImmConfigPageResponse page, string languageCode)
	{
		LocalizePage(page, text => ResolveForLanguage(text, languageCode), (key, args) => GetForLanguage(languageCode, key, args));
	}

	private static void LocalizePage(ImmConfigPageResponse page, Func<string?, string> resolve, Func<string, object[], string> get)
	{
		page.Error = resolve(page.Error);

		foreach (ImmConfigBlockPacket block in page.Configuration ?? Array.Empty<ImmConfigBlockPacket>())
		{
			block.ConfigLabel = resolve(block.ConfigLabel);
			block.Description = resolve(block.Description);

			foreach (ImmConfigControlPacket control in block.Controls ?? Array.Empty<ImmConfigControlPacket>())
			{
				control.Label = resolve(control.Label);
				control.Description = resolve(control.Description);
				control.UnavailableReason = resolve(control.UnavailableReason);

				foreach (ImmConfigOptionPacket option in control.Options ?? Array.Empty<ImmConfigOptionPacket>()) { option.Label = resolve(option.Label); }
			}
		}

		foreach (ImmDependencyPacket dependency in page.Dependencies ?? Array.Empty<ImmDependencyPacket>())
		{
			dependency.Label = resolve(dependency.Label);
			dependency.Description = resolve(dependency.Description);
			dependency.ResolutionDescription = !string.IsNullOrWhiteSpace(dependency.ResolutionDescriptionKey) ? get(dependency.ResolutionDescriptionKey, (dependency.ResolutionDescriptionArgs ?? Array.Empty<string>()).Cast<object>().ToArray()) : resolve(dependency.ResolutionDescription);
		}
	}
}
