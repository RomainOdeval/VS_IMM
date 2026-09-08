#nullable enable

using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace IntegratedModManager.UI;

public static class ImmRichText
{
	public static double Measure(ICoreClientAPI capi, string? text, CairoFont font, double width)
	{
		if (string.IsNullOrWhiteSpace(text)) { return 0; }
		GuiElementRichtext element = Create(capi, text, font, ElementBounds.Fixed(0, 0, Math.Max(1, width), 1));

		try
		{
			element.CalcHeightAndPositions();
			return Math.Ceiling(element.Bounds.fixedHeight);
		}
		finally { element.Dispose(); }
	}

	public static GuiElementRichtext Create(ICoreClientAPI capi, string text, CairoFont font, ElementBounds bounds)
	{
		RichTextComponentBase[] components;

		try { components = VtmlUtil.Richtextify(capi, text, font, link => HandleLink(capi, link)); }
		catch (Exception exception)
		{
			capi.Logger.Warning("[integratedmodmanager] Failed to parse VTML description: {0}", exception.Message);
			components = new RichTextComponentBase[] { new RichTextComponent(capi, text, font) };
		}

		return new GuiElementRichtext(capi, components, bounds);
	}

	private static void HandleLink(ICoreClientAPI capi, LinkTextComponent link)
	{
		string href = link.Href ?? "";

		if (href.StartsWith("handbook://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("handbooksearch://", StringComparison.OrdinalIgnoreCase))
		{
			link.HandleLink();
			return;
		}

		if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? uri)) { return; }
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) { return; }

		capi.Gui.OpenLink(uri.AbsoluteUri);
	}
}
