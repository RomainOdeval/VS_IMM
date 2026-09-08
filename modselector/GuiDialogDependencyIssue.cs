#nullable enable

using System;
using Cairo;
using IntegratedModManager.Config;
using IntegratedModManager.UI;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogDependencyIssue : GuiDialog
{
	private const double Width = 620;
	private const double Padding = 28;
	private const double SectionGap = 22;
	private const double ButtonWidth = 150;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 20;
	private const double MinimumHeight = 360;

	private ImmDependencyPacket? Issue;
	private bool CanAutoResolve;
	private Action<int>? AutoResolve;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.45;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogDependencyIssue(ICoreClientAPI capi) : base(capi) { }

	public bool Show(ImmDependencyPacket issue, bool canAutoResolve, Action<int> autoResolve)
	{
		Issue = issue;
		CanAutoResolve = canAutoResolve && issue.HasResolution;
		AutoResolve = autoResolve;

		Compose();
		return TryOpen();
	}

	private void Compose()
	{
		if (Issue == null) { return; }

		string title = string.IsNullOrWhiteSpace(Issue.Label) ? ImmLocalization.Get("dependency-issue-title") : Issue.Label;

		string description = NormalizeText(Issue.Description);

		string resolution = NormalizeText(Issue.ResolutionDescription);

		double textWidth = Width - Padding * 2;

		CairoFont titleFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center);

		CairoFont bodyFont = CairoFont.WhiteSmallText();

		double titleHeight = Math.Max(24, Measure(title, titleFont, textWidth));

		double descriptionHeight = description.Length == 0 ? 0 : Math.Max(24, ImmRichText.Measure(capi, description, bodyFont, textWidth));

		double resolutionTextWidth = textWidth - 24;

		double resolutionHeight = resolution.Length == 0 ? 0 : Math.Max(46, ImmRichText.Measure(capi, resolution, bodyFont, resolutionTextWidth) + 20);

		double y = Padding;

		ElementBounds titleBounds = ElementBounds.Fixed(Padding, y, textWidth, titleHeight);

		y += titleHeight;

		ElementBounds? descriptionBounds = null;

		if (descriptionHeight > 0)
		{
			y += SectionGap;

			descriptionBounds = ElementBounds.Fixed(Padding, y, textWidth, descriptionHeight);

			y += descriptionHeight;
		}

		ElementBounds? resolutionBounds = null;
		ElementBounds? resolutionTextBounds = null;

		if (resolutionHeight > 0)
		{
			y += SectionGap;

			resolutionBounds = ElementBounds.Fixed(Padding, y, textWidth, resolutionHeight);

			resolutionTextBounds = ElementBounds.Fixed(Padding + 12, y + 10, resolutionTextWidth, resolutionHeight - 20);

			y += resolutionHeight;
		}

		y += SectionGap;

		y = Math.Max(y, MinimumHeight - Padding - ButtonHeight);

		double buttonsWidth = CanAutoResolve ? ButtonWidth * 2 + ButtonGap : ButtonWidth;

		double buttonsX = (Width - buttonsWidth) / 2;

		ElementBounds closeBounds;

		ElementBounds? resolveBounds = null;

		if (CanAutoResolve)
		{
			resolveBounds = ElementBounds.Fixed(buttonsX, y, ButtonWidth, ButtonHeight);

			closeBounds = ElementBounds.Fixed(buttonsX + ButtonWidth + ButtonGap, y, ButtonWidth, ButtonHeight);
		}
		else { closeBounds = ElementBounds.Fixed(buttonsX, y, ButtonWidth, ButtonHeight); }

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);

		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		backgroundBounds.WithChildren(titleBounds, closeBounds);

		if (descriptionBounds != null) { backgroundBounds.WithChildren(descriptionBounds); }

		if (resolutionBounds != null) { backgroundBounds.WithChildren(resolutionBounds); }

		if (resolveBounds != null) { backgroundBounds.WithChildren(resolveBounds); }

		SingleComposer?.Dispose();

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-dependency-issue", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle)).AddShadedDialogBG(backgroundBounds, withTitleBar: false).BeginChildElements(backgroundBounds).AddStaticText(title, titleFont, EnumTextOrientation.Center, titleBounds);

		if (descriptionBounds != null) { composer.AddInteractiveElement(ImmRichText.Create(capi, description, bodyFont, descriptionBounds)); }

		if (resolutionBounds != null && resolutionTextBounds != null) { composer.AddInset(resolutionBounds, depth: 3, brightness: 0.9f).AddInteractiveElement(ImmRichText.Create(capi, resolution, bodyFont, resolutionTextBounds)); }

		if (resolveBounds != null) { composer.AddSmallButton(ImmLocalization.Get("button-auto-resolve"), OnAutoResolveClicked, resolveBounds); }

		SingleComposer = composer.AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds).EndChildElements().Compose();
	}

	private bool OnAutoResolveClicked()
	{
		if (Issue == null || !CanAutoResolve) { return true; }

		int runtimeId = Issue.RuntimeId;

		TryClose();
		AutoResolve?.Invoke(runtimeId);

		return true;
	}

	private bool OnCloseClicked() { TryClose(); return true; }

	private static string NormalizeText(string? text) { return string.IsNullOrWhiteSpace(text) ? "" : text.Replace("\r\n", "\n").Replace('\r', '\n'); }

	private static double Measure(string text, CairoFont font, double width) { return Math.Ceiling(new TextDrawUtil().GetMultilineTextHeight(font, text, Math.Max(1, width))); }

	public override void Dispose()
	{
		Issue = null;
		AutoResolve = null;
		base.Dispose();
	}
}
