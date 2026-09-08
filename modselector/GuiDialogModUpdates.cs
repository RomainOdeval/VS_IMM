#nullable enable

using System;
using System.Linq;
using Cairo;
using IntegratedModManager.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogModUpdates : GuiDialog
{
	private const double Width = 720;
	private const double Height = 620;
	private const double Padding = 28;
	private const double CardHeight = 92;
	private const double CardGap = 10;
	private const double CardPadding = 14;
	private const double InspectWidth = 105;
	private const double InspectHeight = 30;
	private const double ScrollbarWidth = 16;
	private const double ScrollbarGap = 8;
	private const double MouseWheelStep = 48;
	private const int PollMilliseconds = 650;

	private readonly ImmModUpdateClient UpdateClient;
	private readonly GuiDialogModUpdateDetails Details;

	private ImmModUpdateCatalogResponse? Catalog;
	private GuiElementContainer? UpdateContainer;
	private ElementBounds? ContentClipBounds;
	private float ScrollValue;
	private float ContentHeight;
	private bool NeedScrollbar;
	private long PollCallbackId = -1;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.42;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogModUpdates(ICoreClientAPI capi, ImmModUpdateClient updateClient) : base(capi)
	{
		UpdateClient = updateClient;
		Details = new GuiDialogModUpdateDetails(capi, updateClient);

		UpdateClient.CatalogReceived += OnCatalogReceived;
		UpdateClient.InstallReceived += OnInstallReceived;
	}

	public override bool TryOpen()
	{
		Catalog = null;
		ScrollValue = 0;
		Compose();

		bool opened = base.TryOpen();
		UpdateClient.RequestCatalog(requestFullCheck: true);

		return opened;
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		if (NeedScrollbar && ContentClipBounds != null && ContentClipBounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
		{
			SetScroll(ScrollValue - (float)(args.deltaPrecise * MouseWheelStep));
			args.SetHandled();
			return;
		}

		base.OnMouseWheel(args);
	}

	public override void OnGuiClosed()
	{
		CancelPolling();
		UpdateContainer = null;
		ContentClipBounds = null;
		ClearComposers();
		base.OnGuiClosed();
	}

	private void OnCatalogReceived(ImmModUpdateCatalogResponse packet)
	{
		if (!IsOpened()) { return; }

		Catalog = packet;
		Compose();

		if (packet.Success && packet.State == ImmModUpdateCheckState.Checking) { SchedulePoll(); }
		else { CancelPolling(); }
	}

	private void OnInstallReceived(ImmModUpdateInstallResponse packet)
	{
		if (!IsOpened()) { return; }

		UpdateClient.RequestCatalog(requestFullCheck: false);
	}

	private void SchedulePoll()
	{
		if (PollCallbackId >= 0) { return; }

		PollCallbackId = capi.Event.RegisterCallback(_ =>
		{
			PollCallbackId = -1;
			if (IsOpened()) { UpdateClient.RequestCatalog(requestFullCheck: false); }
		}, PollMilliseconds);
	}

	private void CancelPolling()
	{
		if (PollCallbackId < 0) { return; }

		capi.Event.UnregisterCallback(PollCallbackId);
		PollCallbackId = -1;
	}

	private void Compose()
	{
		double titleY = Padding;
		double titleHeight = 34;
		double listY = titleY + titleHeight + 18;
		double buttonHeight = 32;
		double buttonY = Height - Padding - buttonHeight;
		double visibleHeight = buttonY - 18 - listY;
		double availableWidth = Width - Padding * 2;

		ImmModUpdatePacket[] updates = Catalog?.Updates ?? Array.Empty<ImmModUpdatePacket>();
		bool showCards = Catalog?.Success == true && Catalog.State == ImmModUpdateCheckState.Ready && updates.Length > 0;

		double calculatedHeight = showCards ? 8 + updates.Length * (CardHeight + CardGap) : 40;
		ContentHeight = (float)Math.Max(visibleHeight, calculatedHeight);
		NeedScrollbar = showCards && ContentHeight > visibleHeight + 1;

		double contentWidth = availableWidth - (NeedScrollbar ? ScrollbarWidth + ScrollbarGap : 0);
		ScrollValue = Math.Clamp(ScrollValue, 0, Math.Max(0, ContentHeight - (float)visibleHeight));

		ContentClipBounds = ElementBounds.Fixed(Padding, listY, contentWidth, visibleHeight);
		ElementBounds listBounds = ElementBounds.Fixed(0, -ScrollValue, contentWidth, ContentHeight);
		ElementBounds panelBounds = ElementBounds.Fixed(Padding - 8, listY - 8, availableWidth + 16, visibleHeight + 16);
		ElementBounds scrollbarBounds = ElementBounds.Fixed(Padding + contentWidth + ScrollbarGap, listY, ScrollbarWidth, visibleHeight).WithFixedPadding(2);
		ElementBounds titleBounds = ElementBounds.Fixed(Padding, titleY, availableWidth, titleHeight);
		ElementBounds closeBounds = ElementBounds.Fixed((Width - 120) / 2, buttonY, 120, buttonHeight);

		SingleComposer?.Dispose();

		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height).WithAlignment(EnumDialogArea.CenterMiddle);
		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-mod-updates", dialogBounds)
			.AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
			.AddStaticText(ImmLocalization.Get("updates-title"), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center), EnumTextOrientation.Center, titleBounds)
			.AddInset(panelBounds, depth: 4, brightness: 0.85f);

		if (showCards)
		{
			UpdateContainer = new GuiElementContainer(capi, listBounds) { InsideClipBounds = ContentClipBounds, unscaledCellSpacing = 0 };
			PopulateCards(UpdateContainer, updates, contentWidth);
			UpdateContainer.Tabbable = UpdateContainer.Elements.Any(element => element.Focusable);

			composer.BeginClip(ContentClipBounds).AddInteractiveElement(UpdateContainer, "updatelist").EndClip();
			if (NeedScrollbar) { composer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, "updatescroll"); }
		}
		else
		{
			UpdateContainer = null;
			string message = GetStatusText();
			CairoFont statusFont = Catalog?.State == ImmModUpdateCheckState.Failed ? CairoFont.WhiteSmallText().WithColor(GuiStyle.ErrorTextColor) : CairoFont.WhiteSmallText();
			composer.AddStaticText(message, statusFont.WithOrientation(EnumTextOrientation.Center), EnumTextOrientation.Center, ElementBounds.Fixed(Padding + 12, listY + 20, availableWidth - 24, Math.Max(36, visibleHeight - 40)));

			if (Catalog?.State == ImmModUpdateCheckState.Failed)
			{
				ElementBounds retryBounds = ElementBounds.Fixed((Width - 120) / 2, listY + visibleHeight / 2 + 30, 120, 30);
				composer.AddSmallButton(ImmLocalization.Get("updates-retry"), OnRetryClicked, retryBounds);
			}
		}

		SingleComposer = composer.AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds).Compose();

		if (NeedScrollbar)
		{
			GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar("updatescroll");
			scrollbar.SetHeights((float)visibleHeight, ContentHeight);
			SetScroll(ScrollValue);
		}
	}

	private void PopulateCards(GuiElementContainer container, ImmModUpdatePacket[] updates, double contentWidth)
	{
		double cardWidth = Math.Max(160, contentWidth - 8);
		double y = 8;

		foreach (ImmModUpdatePacket update in updates)
		{
			bool pending = update.State == ImmModUpdateEntryState.PendingRestart;
			bool hasInspect = !pending;
			ElementBounds cardBounds = ElementBounds.Fixed(4, y, cardWidth, CardHeight);
			container.Add(new GuiElementModUpdateCard(capi, pending, update.Retracted, cardBounds));

			string title = ImmLocalization.Get("updates-card-title", update.Name, update.InstalledVersion, update.TargetVersion);
			double textWidth = cardWidth - CardPadding * 2 - (hasInspect ? InspectWidth + 12 : 0);

			container.Add(new GuiElementStaticText(capi, title, EnumTextOrientation.Left, ElementBounds.Fixed(4 + CardPadding, y + 12, textWidth, 24), CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold)));

			if (pending)
			{
				CairoFont restartFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithColor(GuiStyle.ErrorTextColor);
				ElementBounds restartBounds = ElementBounds.Fixed(4 + CardPadding, y + 42, textWidth, 38);
				GuiElementStaticText restartElement = new(capi, ImmLocalization.Get("updates-restart-required"), EnumTextOrientation.Left, restartBounds, restartFont);

				restartElement.AutoFontSize();
				restartFont.UnscaledFontsize *= 0.98; // Slightly tighter since there's more text than in the conflicts tab
				container.Add(restartElement);
			}
			else if (!update.Installable && !string.IsNullOrWhiteSpace(update.UnavailableReason))
			{
				container.Add(new GuiElementStaticText(capi, ImmLocalization.Get("updates-manual-only"), EnumTextOrientation.Left, ElementBounds.Fixed(4 + CardPadding, y + 42, textWidth, 20), CairoFont.WhiteSmallText()));
			}

			if (hasInspect)
			{
				ElementBounds inspectBounds = ElementBounds.Fixed(4 + cardWidth - CardPadding - InspectWidth, y + (CardHeight - InspectHeight) / 2, InspectWidth, InspectHeight);
				CairoFont buttonFont = CairoFont.SmallButtonText();
				CairoFont pressedFont = CairoFont.SmallButtonText();
				pressedFont.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();
				GuiElementTextButton inspectButton = new(capi, ImmLocalization.Get("button-inspect"), buttonFont, pressedFont, () => OnInspect(update), inspectBounds, EnumButtonStyle.Normal);
				container.Add(inspectButton);
			}

			y += CardHeight + CardGap;
		}
	}

	private string GetStatusText()
	{
		if (Catalog == null) { return ImmLocalization.Get("updates-loading"); }
		if (!Catalog.Success) { return string.IsNullOrWhiteSpace(Catalog.Error) ? ImmLocalization.Get("updates-error") : Catalog.Error; }

		return Catalog.State switch
		{
			ImmModUpdateCheckState.Checking => ImmLocalization.Get("updates-checking"),
			ImmModUpdateCheckState.Failed => string.IsNullOrWhiteSpace(Catalog.Error) ? ImmLocalization.Get("updates-error") : Catalog.Error,
			ImmModUpdateCheckState.Ready => ImmLocalization.Get("updates-none"),
			_ => ImmLocalization.Get("updates-loading")
		};
	}

	private bool OnInspect(ImmModUpdatePacket update)
	{
		Details.Show(update);
		return true;
	}

	private bool OnRetryClicked()
	{
		Catalog = null;
		Compose();
		UpdateClient.RequestCatalog(requestFullCheck: true);
		return true;
	}

	private bool OnCloseClicked() { TryClose(); return true; }

	private void OnScrollbarChanged(float value)
	{
		ScrollValue = Math.Clamp(value, 0, Math.Max(0, ContentHeight - (ContentClipBounds == null ? 0 : (float)ContentClipBounds.fixedHeight)));

		if (UpdateContainer != null)
		{
			UpdateContainer.Bounds.fixedY = -ScrollValue;
			UpdateContainer.Bounds.CalcWorldBounds();
		}
	}

	private void SetScroll(float value)
	{
		if (!NeedScrollbar)
		{
			ScrollValue = 0;
			OnScrollbarChanged(0);
			return;
		}

		ScrollValue = value;
		OnScrollbarChanged(ScrollValue);
		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("updatescroll");
		if (scrollbar != null) { scrollbar.CurrentYPosition = ScrollValue; }
	}

	public override void Dispose()
	{
		UpdateClient.CatalogReceived -= OnCatalogReceived;
		UpdateClient.InstallReceived -= OnInstallReceived;
		CancelPolling();
		Details.Dispose();
		base.Dispose();
	}
}

public sealed class GuiDialogModUpdateDetails : GuiDialog
{
	private const double Width = 640;
	private const double Height = 560;
	private const double Padding = 28;
	private const double ScrollbarWidth = 16;
	private const double ScrollbarGap = 8;
	private const double MouseWheelStep = 48;

	private readonly ImmModUpdateClient UpdateClient;

	private ImmModUpdatePacket? Update;
	private string Changelog = "";
	private string Error = "";
	private bool LoadingChangelog;
	private bool RequestingUpdate;
	private GuiElementContainer? TextContainer;
	private ElementBounds? ContentClipBounds;
	private float ScrollValue;
	private float ContentHeight;
	private bool NeedScrollbar;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.50;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogModUpdateDetails(ICoreClientAPI capi, ImmModUpdateClient updateClient) : base(capi)
	{
		UpdateClient = updateClient;
		UpdateClient.ChangelogReceived += OnChangelogReceived;
		UpdateClient.InstallReceived += OnInstallReceived;
	}

	public bool Show(ImmModUpdatePacket update)
	{
		Update = update;
		Changelog = "";
		Error = "";
		LoadingChangelog = true;
		RequestingUpdate = false;
		ScrollValue = 0;

		Compose();
		bool opened = IsOpened() || TryOpen();

		UpdateClient.RequestChangelog(update.ModId, update.TargetVersion);
		return opened;
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		if (NeedScrollbar && ContentClipBounds != null && ContentClipBounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
		{
			SetScroll(ScrollValue - (float)(args.deltaPrecise * MouseWheelStep));
			args.SetHandled();
			return;
		}

		base.OnMouseWheel(args);
	}

	private void OnChangelogReceived(ImmModUpdateChangelogResponse packet)
	{
		if (!IsOpened() || Update == null || !Matches(packet.ModId, packet.TargetVersion)) { return; }

		LoadingChangelog = false;
		Error = packet.Success ? "" : ImmLocalization.Resolve(packet.Error);
		Changelog = packet.Success ? packet.Changelog : "";
		Compose();
	}

	private void OnInstallReceived(ImmModUpdateInstallResponse packet)
	{
		if (!IsOpened() || Update == null || !Matches(packet.ModId, packet.TargetVersion)) { return; }

		RequestingUpdate = false;

		if (packet.Success)
		{
			Update.State = packet.State;
			Error = "";
		}
		else { Error = ImmLocalization.Resolve(packet.Error); }

		Compose();
	}

	private void Compose()
	{
		if (Update == null) { return; }

		double titleHeight = 42;
		double titleY = Padding;
		double sectionLabelY = titleY + titleHeight + 10;
		double sectionLabelHeight = 24;
		double listY = sectionLabelY + sectionLabelHeight + 8;
		double buttonsHeight = 32;
		double buttonsY = Height - Padding - buttonsHeight;
		double statusHeight = 44;
		double statusY = buttonsY - statusHeight - 10;
		double visibleHeight = Math.Max(120, statusY - 10 - listY);
		double availableWidth = Width - Padding * 2;

		string bodyText = LoadingChangelog
			? ImmLocalization.Get("updates-changelog-loading")
			: !string.IsNullOrWhiteSpace(Error) && string.IsNullOrWhiteSpace(Changelog)
				? Error
				: string.IsNullOrWhiteSpace(Changelog) ? ImmLocalization.Get("updates-changelog-none") : Changelog;
		string retractedText = Update.Retracted ? ImmLocalization.Get("updates-retracted-warning") : "";

		CairoFont bodyFont = CairoFont.WhiteSmallText();
		CairoFont retractedFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithColor(GuiStyle.ErrorTextColor);
		const double retractedGap = 14;

		double bodyHeight = MeasureText(bodyText, bodyFont, availableWidth - 20);
		double retractedHeight = retractedText.Length == 0 ? 0 : MeasureText(retractedText, retractedFont, availableWidth - 20);
		ContentHeight = (float)Math.Max(visibleHeight, bodyHeight + (retractedHeight > 0 ? retractedGap + retractedHeight : 0) + 12);
		NeedScrollbar = ContentHeight > visibleHeight + 1;

		double contentWidth = availableWidth - (NeedScrollbar ? ScrollbarWidth + ScrollbarGap : 0);
		if (NeedScrollbar)
		{
			bodyHeight = MeasureText(bodyText, bodyFont, contentWidth - 12);
			retractedHeight = retractedText.Length == 0 ? 0 : MeasureText(retractedText, retractedFont, contentWidth - 12);
			ContentHeight = (float)Math.Max(visibleHeight, bodyHeight + (retractedHeight > 0 ? retractedGap + retractedHeight : 0) + 12);
		}
		ScrollValue = Math.Clamp(ScrollValue, 0, Math.Max(0, ContentHeight - (float)visibleHeight));

		ContentClipBounds = ElementBounds.Fixed(Padding, listY, contentWidth, visibleHeight);
		ElementBounds listBounds = ElementBounds.Fixed(0, -ScrollValue, contentWidth, ContentHeight);
		ElementBounds panelBounds = ElementBounds.Fixed(Padding - 8, listY - 8, availableWidth + 16, visibleHeight + 16);
		ElementBounds scrollbarBounds = ElementBounds.Fixed(Padding + contentWidth + ScrollbarGap, listY, ScrollbarWidth, visibleHeight).WithFixedPadding(2);
		ElementBounds titleBounds = ElementBounds.Fixed(Padding, titleY, availableWidth, titleHeight);
		ElementBounds labelBounds = ElementBounds.Fixed(Padding, sectionLabelY, availableWidth, sectionLabelHeight);

		string title = ImmLocalization.Get("updates-card-title", Update.Name, Update.InstalledVersion, Update.TargetVersion);

		SingleComposer?.Dispose();

		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height).WithAlignment(EnumDialogArea.CenterMiddle);
		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-mod-update-details", dialogBounds)
			.AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
			.AddStaticText(title, CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center), EnumTextOrientation.Center, titleBounds)
			.AddStaticText(ImmLocalization.Get("updates-changelog"), CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold), labelBounds)
			.AddInset(panelBounds, depth: 4, brightness: 0.85f);

		TextContainer = new GuiElementContainer(capi, listBounds) { InsideClipBounds = ContentClipBounds, unscaledCellSpacing = 0 };
		double textWidth = Math.Max(100, contentWidth - 12);
		TextContainer.Add(new GuiElementStaticText(capi, bodyText, EnumTextOrientation.Left, ElementBounds.Fixed(6, 6, textWidth, bodyHeight), bodyFont));

		if (retractedHeight > 0)
		{
			TextContainer.Add(new GuiElementStaticText(capi, retractedText, EnumTextOrientation.Left, ElementBounds.Fixed(6, 6 + bodyHeight + retractedGap, textWidth, retractedHeight), retractedFont));
		}

		composer.BeginClip(ContentClipBounds).AddInteractiveElement(TextContainer, "changelogtext").EndClip();
		if (NeedScrollbar) { composer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, "changelogscroll"); }

		string status = "";
		CairoFont statusFont = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);

		if (Update.State == ImmModUpdateEntryState.PendingRestart)
		{
			status = ImmLocalization.Get("updates-restart-required");
			statusFont = statusFont.WithWeight(FontWeight.Bold).WithColor(GuiStyle.ErrorTextColor);
		}
		else if (!string.IsNullOrWhiteSpace(Error) && !LoadingChangelog)
		{
			status = Error;
			statusFont = statusFont.WithColor(GuiStyle.ErrorTextColor);
		}
		else if (!Update.Installable)
		{
			status = string.IsNullOrWhiteSpace(Update.UnavailableReason) ? ImmLocalization.Get("updates-manual-only") : ImmLocalization.Resolve(Update.UnavailableReason);
		}
		else if (RequestingUpdate) { status = ImmLocalization.Get("updates-requesting"); }

		composer.AddStaticText(status, statusFont, EnumTextOrientation.Center, ElementBounds.Fixed(Padding, statusY, availableWidth, statusHeight));

		bool showUpdate = Update.State == ImmModUpdateEntryState.Available && Update.Installable;
		double buttonWidth = 140;
		double gap = 18;
		double buttonGroupWidth = showUpdate ? buttonWidth * 2 + gap : buttonWidth;
		double buttonX = (Width - buttonGroupWidth) / 2;

		if (showUpdate)
		{
			composer.AddSmallButton(RequestingUpdate ? ImmLocalization.Get("updates-requesting-short") : ImmLocalization.Get("updates-update"), OnUpdateClicked, ElementBounds.Fixed(buttonX, buttonsY, buttonWidth, buttonsHeight), key: "update");
		}

		composer.AddSmallButton(showUpdate ? ImmLocalization.Get("button-cancel") : ImmLocalization.Get("button-close"), OnCloseClicked, ElementBounds.Fixed(showUpdate ? buttonX + buttonWidth + gap : buttonX, buttonsY, buttonWidth, buttonsHeight));

		SingleComposer = composer.Compose();

		if (showUpdate)
		{
			GuiElementTextButton updateButton = SingleComposer.GetButton("update");
			updateButton.Enabled = !RequestingUpdate;
		}

		if (NeedScrollbar)
		{
			GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar("changelogscroll");
			scrollbar.SetHeights((float)visibleHeight, ContentHeight);
			SetScroll(ScrollValue);
		}
	}

	private static double MeasureText(string text, CairoFont font, double width)
	{
		return Math.Ceiling(new TextDrawUtil().GetMultilineTextHeight(font, text, Math.Max(100, width)));
	}

	private bool OnUpdateClicked()
	{
		if (Update == null || RequestingUpdate || !Update.Installable || Update.State != ImmModUpdateEntryState.Available) { return true; }

		RequestingUpdate = true;
		Error = "";
		Compose();
		UpdateClient.RequestInstall(Update.ModId, Update.TargetVersion);
		return true;
	}

	private bool OnCloseClicked() { TryClose(); return true; }

	private bool Matches(string modId, string targetVersion)
	{
		return Update != null
			&& string.Equals(Update.ModId, modId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(Update.TargetVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
	}

	private void OnScrollbarChanged(float value)
	{
		ScrollValue = Math.Clamp(value, 0, Math.Max(0, ContentHeight - (ContentClipBounds == null ? 0 : (float)ContentClipBounds.fixedHeight)));

		if (TextContainer != null)
		{
			TextContainer.Bounds.fixedY = -ScrollValue;
			TextContainer.Bounds.CalcWorldBounds();
		}
	}

	private void SetScroll(float value)
	{
		if (!NeedScrollbar)
		{
			ScrollValue = 0;
			OnScrollbarChanged(0);
			return;
		}

		ScrollValue = value;
		OnScrollbarChanged(ScrollValue);
		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("changelogscroll");
		if (scrollbar != null) { scrollbar.CurrentYPosition = ScrollValue; }
	}

	public override void Dispose()
	{
		UpdateClient.ChangelogReceived -= OnChangelogReceived;
		UpdateClient.InstallReceived -= OnInstallReceived;
		base.Dispose();
	}
}

internal sealed class GuiElementModUpdateCard : GuiElement
{
	private readonly bool PendingRestart;
	private readonly bool Retracted;

	public GuiElementModUpdateCard(ICoreClientAPI capi, bool pendingRestart, bool retracted, ElementBounds bounds) : base(capi, bounds)
	{
		PendingRestart = pendingRestart;
		Retracted = retracted;
	}

	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		// Visual chrome only. Controls layered over the card receive clicks.
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();
		double[] baseColor = GuiStyle.DialogDefaultBgColor;

		if (PendingRestart) { ctxStatic.SetSourceRGBA(0.18, 0.18, 0.18, baseColor[3]); }
		else
		{
			double targetRed = Retracted ? 0.62 : 0.12;
			double targetGreen = Retracted ? 0.12 : 0.58;
			double targetBlue = Retracted ? 0.10 : 0.16;
			const double tint = 0.42;

			ctxStatic.SetSourceRGBA(
				baseColor[0] * (1 - tint) + targetRed * tint,
				baseColor[1] * (1 - tint) + targetGreen * tint,
				baseColor[2] * (1 - tint) + targetBlue * tint,
				baseColor[3]);
		}

		RoundRectangle(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, GuiStyle.ElementBGRadius);
		ctxStatic.Fill();
		EmbossRoundRectangleElement(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, inverse: false, depth: 2, radius: (int)GuiStyle.ElementBGRadius);
	}
}
