#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using IntegratedModManager.Config;
using IntegratedModManager.UI;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogModSelector : GuiDialog
{
	private const int SearchDebounceMs = 350;

	private const double MinimumScreenWidth = 640;
	private const double MinimumScreenHeight = 420;
	private const double HeaderHeight = 42;
	private const double HeaderGap = 18;

	private readonly List<ModSelectorEntry> Entries = new();
	private readonly ImmConfigClient ConfigClient;
	private readonly ImmModUpdateClient UpdateClient;
	private readonly Action<ModSelectorEntry> ModSelected;
	private readonly Action UpdatesSelected;

	private GuiElementModGrid? ModGrid;
	private string SearchText = "";
	private bool FilterWarnings;
	private bool FilterErrors;
	private bool SettingSearchValue;
	private bool CanManageServer;
	private long SearchCallbackId = -1;
	private long UpdatePollCallbackId = -1;
	private int SelectorRows = 2;
	private ImmImportantInformationHighlight HighlightMode = ImmImportantInformationHighlight.Pulsating;
	private int LastFrameWidth;
	private int LastFrameHeight;
	private int UpdateAvailableCount;
	private int UpdatePendingRestartCount;
	private int UpdateRetractedCount;
	private ImmModUpdateCheckState UpdateCheckState = ImmModUpdateCheckState.NotChecked;
	private ElementBounds? UpdateButtonBounds;
	private LoadedTexture? UpdateFillTexture;
	private readonly Vec4f UpdatePulseColor = new();

	public override string ToggleKeyCombinationCode => null!;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogModSelector(ICoreClientAPI capi, ImmConfigClient configClient, ImmModUpdateClient updateClient, Action<ModSelectorEntry> modSelected, Action updatesSelected) : base(capi)
	{
		ConfigClient = configClient;
		UpdateClient = updateClient;
		ModSelected = modSelected;
		UpdatesSelected = updatesSelected;

		ConfigClient.CatalogReceived += OnCatalogReceived;
		UpdateClient.SummaryReceived += OnUpdateSummaryReceived;
	}

	public override bool TryOpen()
	{
		SearchText = "";
		FilterWarnings = false;
		FilterErrors = false;
		CanManageServer = false;
		Entries.Clear();
		ModGrid = null;
		SelectorRows = IntegratedModManagerConfig.SelectorRows;
		HighlightMode = IntegratedModManagerConfig.ConfiguredInformationHighlight;
		CancelPendingSearch();
		CancelUpdatePolling();
		Compose();

		bool opened = base.TryOpen();

		ConfigClient.RequestCatalog();

		return opened;
	}

	public override void OnRenderGUI(float deltaTime)
	{
		if (capi.Render.FrameWidth > 0 && capi.Render.FrameHeight > 0 && (LastFrameWidth != capi.Render.FrameWidth || LastFrameHeight != capi.Render.FrameHeight)) { Compose(); }

		base.OnRenderGUI(deltaTime);
		RenderUpdateHighlight();
	}

	public override void OnKeyDown(KeyEvent args)
	{
		if (args.KeyCode == (int)GlKeys.Enter)
		{
			CancelPendingSearch();
			ApplyFilters();
			args.Handled = true;
			return;
		}

		base.OnKeyDown(args);
	}

	public override void OnGuiClosed()
	{
		CancelPendingSearch();
		CancelUpdatePolling();
		ModGrid = null;
		UpdateButtonBounds = null;
		ClearComposers();
		base.OnGuiClosed();
	}

	private void OnCatalogReceived(ImmConfigCatalogResponse packet)
	{
		if (!IsOpened()) { return; }

		if (!packet.Success)
		{
			CanManageServer = false;
			FilterWarnings = false;
			FilterErrors = false;
			Entries.Clear();
			Compose();

			capi.Logger.Warning("[integratedmodmanager] Failed to retrieve server mod catalog: {0}", packet.Error);

			return;
		}

		CanManageServer = packet.CanManageServer;

		if (!CanManageServer)
		{
			FilterWarnings = false;
			FilterErrors = false;
		}

		Dictionary<string, Mod> localMods = capi.ModLoader.Mods.Where(mod => !string.IsNullOrWhiteSpace(mod.Info?.ModID)).GroupBy(mod => mod.Info!.ModID, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

		Entries.Clear();

		SelectorRows = IntegratedModManagerConfig.SelectorRows;
		HighlightMode = IntegratedModManagerConfig.ConfiguredInformationHighlight;

		ImmServerModPacket[] serverMods = packet.Mods ?? Array.Empty<ImmServerModPacket>();

		foreach (ImmServerModPacket serverMod in serverMods)
		{
			if (serverMod == null) { continue; }
			localMods.TryGetValue(serverMod.ModId, out Mod? localMod);

			Entries.Add(new ModSelectorEntry(serverMod, localMod));
		}

		Compose();

		if (CanManageServer) { UpdateClient.RequestSummary(); }
	}

	private void Compose()
	{
		CancelPendingSearch();

		LastFrameWidth = capi.Render.FrameWidth;
		LastFrameHeight = capi.Render.FrameHeight;

		double guiScale = Math.Max(0.1, RuntimeEnv.GUIScale);
		double screenWidth = Math.Max(MinimumScreenWidth, LastFrameWidth / guiScale);
		double screenHeight = Math.Max(MinimumScreenHeight, LastFrameHeight / guiScale);

		double margin = Math.Clamp(screenWidth * 0.025, 16, 36);
		double top = Math.Clamp(screenHeight * 0.025, 14, 28);

		const double filterButtonWidth = 42;
		const double filterButtonGap = 8;
		const double updateButtonWidth = 120;
		const double closeButtonWidth = 90;

		double closeX = screenWidth - margin - closeButtonWidth;
		double updatesX = closeX - filterButtonGap - updateButtonWidth;
		double leftControlsRight = CanManageServer ? margin + filterButtonWidth * 2 + filterButtonGap : margin;
		double searchLeft = leftControlsRight + 16;
		double searchRight = (CanManageServer ? updatesX : closeX) - 16;
		double searchWidth = Math.Min(Math.Clamp(screenWidth * 0.34, 260, 560), Math.Max(160, searchRight - searchLeft));
		double searchX = Math.Clamp((screenWidth - searchWidth) / 2, searchLeft, Math.Max(searchLeft, searchRight - searchWidth));

		ElementBounds warningBounds = ElementBounds.Fixed(margin, top, filterButtonWidth, HeaderHeight);
		ElementBounds errorBounds = ElementBounds.Fixed(margin + filterButtonWidth + filterButtonGap, top, filterButtonWidth, HeaderHeight);
		ElementBounds updatesBounds = ElementBounds.Fixed(updatesX, top + 4, updateButtonWidth, HeaderHeight - 8);
		ElementBounds searchBounds = ElementBounds.Fixed(searchX, top + 4, searchWidth, HeaderHeight - 8);
		ElementBounds closeBounds = ElementBounds.Fixed(closeX, top + 4, closeButtonWidth, HeaderHeight - 8);

		double gridY = top + HeaderHeight + HeaderGap;
		double gridHeight = Math.Max(120, screenHeight - gridY - margin);

		ElementBounds gridBounds = ElementBounds.Fixed(margin, gridY, Math.Max(200, screenWidth - margin * 2), gridHeight);

		SingleComposer?.Dispose();

		double[] overlayColor = (double[])GuiStyle.DialogDefaultBgColor.Clone();

		overlayColor[3] = Math.Min(1, overlayColor[3] + 0.08);

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-modselector", ElementBounds.Fill).AddGameOverlay(ElementBounds.Fill, overlayColor);

		if (CanManageServer)
		{
			composer.AddImmDiagnosticToggleButton(ImmLocalization.Get("selector-warning-short"), CairoFont.SmallButtonText(), OnWarningFilterChanged, warningBounds, ImmDiagnosticLevel.Warning, "warnings")
				.AddImmDiagnosticToggleButton(ImmLocalization.Get("selector-error-short"), CairoFont.SmallButtonText(), OnErrorFilterChanged, errorBounds, ImmDiagnosticLevel.Error, "errors")
				.AddSmallButton(ImmLocalization.Get("updates-button"), OnUpdatesClicked, updatesBounds, key: "updates");
		}

		SingleComposer = composer.AddInteractiveElement(new GuiElementImmSearchInput(capi, searchBounds, OnSearchTextChanged, CairoFont.TextInput()), "search").AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds).AddModSelectorGrid(Entries, OnModClicked, SelectorRows, HighlightMode, gridBounds, "modgrid").Compose();

		if (CanManageServer)
		{
			SingleComposer.GetToggleButton("warnings").SetValue(FilterWarnings);

			SingleComposer.GetToggleButton("errors").SetValue(FilterErrors);
			UpdateButtonBounds = SingleComposer.GetButton("updates").Bounds;
		}

		GuiElementTextInput searchInput = SingleComposer.GetTextInput("search");
		searchInput.SetPlaceHolderText(ImmLocalization.Get("selector-search-placeholder"));

		SettingSearchValue = true;
		searchInput.SetValue(SearchText);
		SettingSearchValue = false;

		ModGrid = SingleComposer.GetModSelectorGrid("modgrid");
		ApplyFilters();
	}

	private void OnUpdateSummaryReceived(ImmModUpdateSummaryResponse packet)
	{
		if (!IsOpened() || !CanManageServer || !packet.Success) { return; }

		UpdateAvailableCount = packet.AvailableCount;
		UpdatePendingRestartCount = packet.PendingRestartCount;
		UpdateRetractedCount = packet.RetractedCount;
		UpdateCheckState = packet.State;

		if (UpdateCheckState == ImmModUpdateCheckState.Checking) { ScheduleUpdatePoll(); }
		else { CancelUpdatePolling(); }
	}

	private void ScheduleUpdatePoll()
	{
		if (UpdatePollCallbackId >= 0) { return; }

		UpdatePollCallbackId = capi.Event.RegisterCallback(_ =>
		{
			UpdatePollCallbackId = -1;
			if (IsOpened() && CanManageServer) { UpdateClient.RequestSummary(); }
		}, 650);
	}

	private void CancelUpdatePolling()
	{
		if (UpdatePollCallbackId < 0) { return; }
		capi.Event.UnregisterCallback(UpdatePollCallbackId);
		UpdatePollCallbackId = -1;
	}

	private void RenderUpdateHighlight()
	{
		if (!CanManageServer || UpdateButtonBounds == null || UpdateAvailableCount + UpdatePendingRestartCount <= 0 || !ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode)) { return; }

		UpdateFillTexture ??= ImmDiagnosticPulse.CreateSolidTexture(capi);
		UpdateButtonBounds.CalcWorldBounds();

		double phase = ImmDiagnosticPulse.GetHighlightPhase(HighlightMode, capi.ElapsedMilliseconds);
		ImmDiagnosticPulse.SetUpdateOverlayColor(UpdateRetractedCount > 0, phase, UpdatePulseColor);

		capi.Render.Render2DTexture(UpdateFillTexture.TextureId, (float)UpdateButtonBounds.renderX, (float)UpdateButtonBounds.renderY, (float)UpdateButtonBounds.InnerWidth, (float)UpdateButtonBounds.InnerHeight, 60, UpdatePulseColor);
	}

	private bool OnUpdatesClicked()
	{
		UpdatesSelected();
		TryClose();
		return true;
	}

	private void OnSearchTextChanged(string text)
	{
		SearchText = text ?? "";

		if (SettingSearchValue) { return; }

		CancelPendingSearch();
		SearchCallbackId = capi.Event.RegisterCallback(OnSearchDebounceElapsed, SearchDebounceMs);
	}

	private void OnSearchDebounceElapsed(float deltaTime)
	{
		SearchCallbackId = -1;

		if (IsOpened()) { ApplyFilters(); }
	}

	private void CancelPendingSearch()
	{
		if (SearchCallbackId < 0) { return; }

		capi.Event.UnregisterCallback(SearchCallbackId);
		SearchCallbackId = -1;
	}

	private void OnWarningFilterChanged(bool enabled)
	{
		FilterWarnings = enabled;
		ApplyFilters();
	}

	private void OnErrorFilterChanged(bool enabled)
	{
		FilterErrors = enabled;
		ApplyFilters();
	}

	private void ApplyFilters()
	{
		if (ModGrid == null) { return; }

		string query = SearchText.Trim();
		bool useHealthFilter = CanManageServer && (FilterWarnings || FilterErrors);

		ModGrid.SetVisibleEntries(Entries.Where(entry => { bool searchMatches = query.Length == 0 || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.ModId.Contains(query, StringComparison.OrdinalIgnoreCase); if (!searchMatches) { return false; } if (!useHealthFilter) { return true; } return (FilterWarnings && entry.HasWarnings) || (FilterErrors && entry.HasErrors); }));
	}

	private void OnModClicked(ModSelectorEntry entry)
	{
		// Open the next dialog before closing this one so Vintage Story never briefly re-grabs the mouse and recenters it between dialogs.
		ModSelected(entry);
		TryClose();
	}

	private bool OnCloseClicked() { TryClose(); return true; }

	public override void Dispose()
	{
		ConfigClient.CatalogReceived -= OnCatalogReceived;
		UpdateClient.SummaryReceived -= OnUpdateSummaryReceived;

		CancelPendingSearch();
		CancelUpdatePolling();
		ModGrid = null;
		UpdateFillTexture?.Dispose();
		UpdateFillTexture = null;
		base.Dispose();
	}
}

public sealed class GuiElementImmDiagnosticToggleButton : GuiElementToggleButton
{
	private readonly double[] BackgroundColor;

	public GuiElementImmDiagnosticToggleButton(ICoreClientAPI capi, string text, CairoFont font, Action<bool> onToggled, ElementBounds bounds, ImmDiagnosticLevel level) : base(capi, "", text, font, onToggled, bounds, toggleable: true) { BackgroundColor = ImmDiagnosticPulse.CreateFlatGuiColor(level); }

	public override void ComposeElements(Cairo.Context ctx, Cairo.ImageSurface surface)
	{
		double[] originalColor = GuiStyle.DialogDefaultBgColor;

		GuiStyle.DialogDefaultBgColor = BackgroundColor;

		try { base.ComposeElements(ctx, surface); }
		finally { GuiStyle.DialogDefaultBgColor = originalColor; }
	}
}

public static class GuiComposerImmDiagnosticToggleExtensions
{
	public static GuiComposer AddImmDiagnosticToggleButton(this GuiComposer composer, string text, CairoFont font, Action<bool> onToggle, ElementBounds bounds, ImmDiagnosticLevel level, string? key = null)
	{
		if (!composer.Composed) { composer.AddInteractiveElement(new GuiElementImmDiagnosticToggleButton(composer.Api, text, font, onToggle, bounds, level), key); }

		return composer;
	}
}



internal sealed class GuiElementImmSearchInput : GuiElementTextInput
{
	public GuiElementImmSearchInput(ICoreClientAPI capi, ElementBounds bounds, Action<string> onTextChanged, CairoFont font) : base(capi, bounds, onTextChanged, font) { }

	public override void ComposeTextElements(Context ctx, ImageSurface surface)
	{
		base.ComposeTextElements(ctx, surface);

		ctx.Save();
		ctx.SetSourceRGBA(0, 0, 0, 0.14);

		ElementRoundRectangle(ctx, Bounds, false, 1);

		ctx.Fill();
		ctx.Restore();
	}
}
