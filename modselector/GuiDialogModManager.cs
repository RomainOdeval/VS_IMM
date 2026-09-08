#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cairo;
using IntegratedModManager.Config;
using IntegratedModManager.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogModManager : GuiDialog
{
	private const double PreferredWidth = 520;
	private const double PreferredHeight = 680;
	private const double ScreenMargin = 30;
	private const double PanelPadding = 16;
	private const double ScrollbarWidth = 16;
	private const double ScrollbarGap = 8;
	private const double MouseWheelStep = 72;
	private const double ButtonWidth = 150;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 16;
	private const double BottomPadding = 18;
	private const double TabsHeight = 28;

	private const double BlockTitleHeight = 22;
	private const double BlockDescriptionGap = 4;
	private const double BlockContentGap = 8;
	private const double BlockGap = 18;

	private const double CardX = 4;
	private const double CardGap = 10;
	private const double CardPadding = 10;
	private const double SettingLabelHeight = 18;
	private const double LabelControlGap = 5;
	private const double ControlHeight = 36;
	private const double BooleanRowHeight = 28;
	private const double BooleanToggleSize = 24;
	private const double BooleanToggleGap = 12;
	private const double DescriptionGap = 6;
	private const double DescriptionBottomPadding = 2;
	private const double DependencyCardMinHeight = 54;
	private const double DependencyInspectWidth = 105;
	private const double DependencyInspectHeight = 30;
	private const double DependencyLabelGap = 12;
	private const double DependencyRestartGap = 5;
	private const double DependencyRestartTextHeight = 24;

	private const int SearchDebounceMs = 250;
	private const double SearchHeight = 30;
	private const double SearchGap = 10;

	private readonly ImmConfigClient ConfigClient;
	private readonly GuiDialogNotification Notification;
	private readonly GuiDialogDependencyIssue DependencyIssueDialog;
	private readonly GuiDialogArrayEditor ArrayEditorDialog;
	private readonly Dictionary<int, string> PendingChanges = new();
	private readonly Dictionary<int, string> OriginalValues = new();
	private readonly HashSet<int> InvalidInputs = new();
	private readonly Dictionary<int, string> InvalidInputValues = new();
	private readonly Dictionary<int, GuiElement> ControlElements = new();
	private readonly Dictionary<int, PendingRestartDependency> PendingRestartDependencies = new();

	private Dictionary<int, string>? ApplyingChanges;
	private ImmConfigPageResponse? Page;

	private string SelectedModId = "";
	private string SelectedModName = "";
	private string SearchText = "";

	private ElementBounds? ContentClipBounds;
	private GuiElementContainer? ConfigContainer;

	private float ScrollValue;
	private float ContentHeight;
	private bool NeedScrollbar;
	private bool Applying;
	private bool SettingControlValues;
	private bool SettingSearchValue;
	private bool ClientChangesSavedDuringApply;
	private ImmReloadRequirement ClientReloadRequirementDuringApply = ImmReloadRequirement.None;
	private bool RefreshingAfterResolution;
	private bool RefreshingAfterApply;
	private string StatusMessage = "";
	private int ActiveTab;
	private int ResolvingDependencyId = -1;
	private long SearchCallbackId = -1;

	private double DependencyTabOffset;
	private double DependencyTabWidth;
	private bool DependencyTabPulseVisible;
	private ImmDiagnosticLevel DependencyTabDiagnosticLevel;
	private LoadedTexture? DependencyTabFillTexture;
	private readonly Vec4f DependencyTabPulseColor = new();
	private ImmImportantInformationHighlight HighlightMode = ImmImportantInformationHighlight.Pulsating;

	private int LastFrameWidth;
	private int LastFrameHeight;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.2;
	public override bool PrefersUngrabbedMouse => true;

	public GuiDialogModManager(ICoreClientAPI capi, ImmConfigClient configClient, GuiDialogNotification notification) : base(capi)
	{
		ConfigClient = configClient;
		Notification = notification;
		DependencyIssueDialog = new GuiDialogDependencyIssue(capi);

		ArrayEditorDialog = new GuiDialogArrayEditor(capi);

		ConfigClient.PageReceived += OnPageReceived;
		ConfigClient.ApplyReceived += OnApplyReceived;
		ConfigClient.DependencyResolveReceived += OnDependencyResolveReceived;
		capi.Event.LevelFinalize += OnLevelFinalize;
	}

	public void SetMod(string modId, string modName)
	{
		SelectedModId = modId;
		SelectedModName = modName;
		ResetState();
	}

	public override bool TryOpen()
	{
		if (string.IsNullOrWhiteSpace(SelectedModId)) { return false; }

		ResetState();
		Compose();

		bool opened = base.TryOpen();

		ConfigClient.RequestPage(SelectedModId);

		return opened;
	}

	public override void OnRenderGUI(float deltaTime)
	{
		if (capi.Render.FrameWidth > 0 && capi.Render.FrameHeight > 0 && (LastFrameWidth != capi.Render.FrameWidth || LastFrameHeight != capi.Render.FrameHeight)) { Compose(); }

		base.OnRenderGUI(deltaTime);
		RenderDependencyTabPulse();
	}

	private void RenderDependencyTabPulse()
	{
		if (Page?.CanManageServer != true || !DependencyTabPulseVisible || DependencyTabDiagnosticLevel == ImmDiagnosticLevel.None || SingleComposer == null) { return; }

		GuiElementHorizontalTabs? tabs = SingleComposer.GetHorizontalTabs("managertabs");

		if (tabs == null) { return; }

		tabs.Bounds.CalcWorldBounds();

		double pulse = ImmDiagnosticPulse.GetHighlightPhase(HighlightMode, capi.ElapsedMilliseconds);

		if (DependencyTabFillTexture == null) { return; }

		ImmDiagnosticPulse.SetTabOverlayColor(DependencyTabDiagnosticLevel, pulse, DependencyTabPulseColor);

		capi.Render.Render2DTexture(DependencyTabFillTexture.TextureId, (float)(tabs.Bounds.renderX + DependencyTabOffset), (float)tabs.Bounds.renderY, (float)DependencyTabWidth, (float)tabs.Bounds.InnerHeight, 60, DependencyTabPulseColor);
	}

	private static ImmDiagnosticLevel GetDependencyDiagnosticLevel(ImmDependencyPacket[] dependencies)
	{
		if (dependencies.Any(dependency => dependency.Severity == ImmDependencySeverity.Error)) { return ImmDiagnosticLevel.Error; }

		return dependencies.Any(dependency => dependency.Severity == ImmDependencySeverity.Warning) ? ImmDiagnosticLevel.Warning : ImmDiagnosticLevel.None;
	}

	public override void OnKeyDown(KeyEvent args)
	{
		GuiElementTextInput? searchInput = SingleComposer?.GetElement("configsearch") as GuiElementTextInput;

		if (searchInput?.HasFocus == true && (args.KeyCode == (int)GlKeys.Enter || args.KeyCode == (int)GlKeys.KeypadEnter))
		{
			CancelPendingSearch();
			ApplySearch();
			args.Handled = true;
			return;
		}

		base.OnKeyDown(args);
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

	private void OnLevelFinalize() { PendingRestartDependencies.Clear(); }

	private void ResetState()
	{
		Page = null;
		PendingChanges.Clear();
		OriginalValues.Clear();
		InvalidInputs.Clear();
		InvalidInputValues.Clear();
		SearchText = "";
		SettingSearchValue = false;
		CancelPendingSearch();
		ApplyingChanges = null;
		Applying = false;
		ClientChangesSavedDuringApply = false;
		ClientReloadRequirementDuringApply = ImmReloadRequirement.None;
		RefreshingAfterResolution = false;
		RefreshingAfterApply = false;
		StatusMessage = "";
		ActiveTab = 0;
		ResolvingDependencyId = -1;
		DependencyTabOffset = 0;
		DependencyTabWidth = 0;
		DependencyTabPulseVisible = false;
		DependencyTabDiagnosticLevel = ImmDiagnosticLevel.None;
		HighlightMode = IntegratedModManagerConfig.ConfiguredInformationHighlight;
		ScrollValue = 0;
	}

	private void OnPageReceived(ImmConfigPageResponse packet)
	{
		if (string.IsNullOrWhiteSpace(SelectedModId) || !IsOpened() || !string.Equals(packet.ModId, SelectedModId, StringComparison.OrdinalIgnoreCase)) { return; }

		bool keepStatus = RefreshingAfterResolution || RefreshingAfterApply;

		RefreshingAfterResolution = false;
		RefreshingAfterApply = false;
		RestorePendingRestartDependencies(packet);
		Page = packet;
		HighlightMode = IntegratedModManagerConfig.ConfiguredInformationHighlight;
		PendingChanges.Clear();
		OriginalValues.Clear();
		CaptureOriginalValues();
		InvalidInputs.Clear();
		InvalidInputValues.Clear();
		ApplyingChanges = null;
		Applying = false;
		ClientChangesSavedDuringApply = false;
		ClientReloadRequirementDuringApply = ImmReloadRequirement.None;

		if (!keepStatus) { StatusMessage = ""; }

		if (packet.Success && packet.CanManageServer && !packet.ConfigurationExternallyManaged && (packet.Configuration?.Length ?? 0) == 0 && (packet.Dependencies?.Length ?? 0) > 0) { ActiveTab = 1; }

		ScrollValue = 0;

		Compose();
	}

	private void OnApplyReceived(ImmConfigApplyResponse packet)
	{
		if (string.IsNullOrWhiteSpace(SelectedModId) || !IsOpened() || !string.Equals(packet.ModId, SelectedModId, StringComparison.OrdinalIgnoreCase)) { return; }

		if (packet.Success && ApplyingChanges != null)
		{
			RemovePendingChanges(ApplyingChanges);

			ImmReloadRequirement reloadRequirement = CombineReloadRequirements(ClientReloadRequirementDuringApply, packet.ReloadRequirement);

			StatusMessage = GetSavedStatusMessage(reloadRequirement);

			ShowAppliedChatMessage(reloadRequirement);

			RefreshingAfterApply = true;
			ConfigClient.RequestPage(SelectedModId);
		}
		else if (!packet.Success) { StatusMessage = ClientChangesSavedDuringApply ? ImmLocalization.Get("status-client-saved-server-failed", packet.Error) : packet.Error; }

		ApplyingChanges = null;
		Applying = false;
		ClientChangesSavedDuringApply = false;
		ClientReloadRequirementDuringApply = ImmReloadRequirement.None;

		Compose();
	}

	private void OnDependencyResolveReceived(ImmDependencyResolveResponse packet)
	{
		if (!IsOpened() || packet.RuntimeId != ResolvingDependencyId) { return; }

		ImmDependencyPacket? resolvedDependency = Page?.Dependencies?.FirstOrDefault(dependency => dependency.RuntimeId == packet.RuntimeId);

		ResolvingDependencyId = -1;

		if (!packet.Success)
		{
			StatusMessage = packet.Error;
			Compose();
			return;
		}

		bool sentCommand = !string.IsNullOrWhiteSpace(packet.ChatCommand);

		if (sentCommand)
		{
			if (packet.ChatCommand.StartsWith(".", StringComparison.Ordinal)) { capi.TriggerChatMessage(packet.ChatCommand); }
			else { capi.SendChatMessage(packet.ChatCommand); }

			StatusMessage = ImmLocalization.Get("status-auto-resolution-command-sent");

			capi.ShowChatMessage(ImmLocalization.Get("chat-auto-resolution-command-sent"));
		}
		else
		{
			StatusMessage = ImmLocalization.Get("status-resolution-applied");

			capi.ShowChatMessage(ImmLocalization.Get("chat-auto-resolution-applied"));
		}

		if (resolvedDependency?.ResolutionType == ImmDependencyResolutionType.SetSetting && Page?.ExternalManagerActive == true) { capi.ShowChatMessage(ImmLocalization.Get("chat-external-manager-patch-risk")); }

		ShowResolutionWarning(packet.Warning);

		MarkEquivalentDependenciesPendingRestart(packet.RuntimeId, resolvedDependency);
		RefreshingAfterResolution = true;

		ConfigClient.RequestPage(SelectedModId);

		ConfigClient.RequestCatalog();

		Compose();
	}

	private void MarkEquivalentDependenciesPendingRestart(int runtimeId, ImmDependencyPacket? resolvedDependency)
	{
		string sourceModId = Page?.ModId ?? SelectedModId;

		PendingRestartDependencies[runtimeId] = new PendingRestartDependency(sourceModId, resolvedDependency);

		if (resolvedDependency == null) { return; }

		foreach (ImmDependencyPacket dependency in Page?.Dependencies ?? Array.Empty<ImmDependencyPacket>())
		{
			if (dependency.RuntimeId == runtimeId || !HasSameResolutionOutcome(sourceModId, resolvedDependency, dependency)) { continue; }
			PendingRestartDependencies[dependency.RuntimeId] = new PendingRestartDependency(sourceModId, dependency);
		}
	}

	private void RestorePendingRestartDependencies(ImmConfigPageResponse packet)
	{
		if (!packet.Success || !packet.CanManageServer) { return; }

		List<ImmDependencyPacket> dependencies = (packet.Dependencies ?? Array.Empty<ImmDependencyPacket>()).ToList();
		HashSet<int> runtimeIds = dependencies.Select(dependency => dependency.RuntimeId).ToHashSet();

		foreach (KeyValuePair<int, PendingRestartDependency> pair in PendingRestartDependencies)
		{
			PendingRestartDependency pending = pair.Value;

			if (!string.Equals(pending.ModId, packet.ModId, StringComparison.OrdinalIgnoreCase) || pending.Snapshot == null || !runtimeIds.Add(pair.Key)) { continue; }
			dependencies.Add(pending.Snapshot);
		}

		packet.Dependencies = dependencies.ToArray();
	}

	private static bool HasSameResolutionOutcome(string sourceModId, ImmDependencyPacket left, ImmDependencyPacket right)
	{
		if (!left.HasResolution || !right.HasResolution || left.ResolutionType != right.ResolutionType) { return false; }

		string[] leftArgs = left.ResolutionDescriptionArgs ?? Array.Empty<string>();
		string[] rightArgs = right.ResolutionDescriptionArgs ?? Array.Empty<string>();

		switch (left.ResolutionType)
		{
			case ImmDependencyResolutionType.InstallMod: return leftArgs.Length == 1 && rightArgs.Length == 1 && string.Equals(leftArgs[0], rightArgs[0], StringComparison.OrdinalIgnoreCase);

			case ImmDependencyResolutionType.RunCommand: return leftArgs.Length == 1 && rightArgs.Length == 1 && string.Equals(leftArgs[0], rightArgs[0], StringComparison.Ordinal);

			case ImmDependencyResolutionType.SetSetting:
				if (!string.Equals(left.ResolutionDescriptionKey, right.ResolutionDescriptionKey, StringComparison.Ordinal)) { return false; }

				if (string.Equals(left.ResolutionDescriptionKey, "resolution-set-modconfig", StringComparison.Ordinal))
				{
					return leftArgs.Length == 3 && rightArgs.Length == 3
						&& string.Equals(leftArgs[0], rightArgs[0], StringComparison.Ordinal)
						&& string.Equals(leftArgs[1], rightArgs[1], StringComparison.Ordinal)
						&& string.Equals(leftArgs[2], rightArgs[2], StringComparison.Ordinal);
				}

				if (string.Equals(left.ResolutionDescriptionKey, "resolution-set-patchsetting", StringComparison.Ordinal))
				{
					return TryGetPatchSettingResolution(sourceModId, leftArgs, out string leftModId, out string leftPatchSetting, out string leftValue)
						&& TryGetPatchSettingResolution(sourceModId, rightArgs, out string rightModId, out string rightPatchSetting, out string rightValue)
						&& string.Equals(leftModId, rightModId, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(leftPatchSetting, rightPatchSetting, StringComparison.Ordinal)
						&& string.Equals(leftValue, rightValue, StringComparison.Ordinal);
				}
			return false;

			default: return false;
		}
	}

	private static bool TryGetPatchSettingResolution(string sourceModId, string[] args, out string modId, out string patchSetting, out string value)
	{
		modId = "";
		patchSetting = "";
		value = "";

		if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0])) { return false; }

		string target = args[0];
		int separatorIndex = target.IndexOf(':');

		if (separatorIndex < 0)
		{
			modId = sourceModId;
			patchSetting = target;
		}
		else
		{
			if (separatorIndex == 0 || separatorIndex == target.Length - 1) { return false; }

			modId = target[..separatorIndex];
			patchSetting = target[(separatorIndex + 1)..];
		}

		value = args[1];
		return true;
	}

	private void Compose()
	{
		GuiElementTextInput? previousSearchInput = SingleComposer?.GetElement("configsearch") as GuiElementTextInput;
		bool restoreSearchFocus = previousSearchInput?.HasFocus == true;
		int previousSearchCaret = previousSearchInput?.CaretPosWithoutLineBreaks ?? SearchText.Length;
		CancelPendingSearch();

		LastFrameWidth = capi.Render.FrameWidth;
		LastFrameHeight = capi.Render.FrameHeight;

		ControlElements.Clear();
		ConfigContainer = null;

		double guiScale = Math.Max(0.1, RuntimeEnv.GUIScale);
		double screenWidth = capi.Render.FrameWidth / guiScale;
		double screenHeight = capi.Render.FrameHeight / guiScale;

		double width = Math.Min(PreferredWidth, Math.Max(300, screenWidth - ScreenMargin * 2));
		double height = Math.Min(PreferredHeight, Math.Max(360, screenHeight - ScreenMargin * 2));

		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds titleBounds = ElementBounds.Fixed(PanelPadding, 14, width - PanelPadding * 2, 28);
		ElementBounds tabsBounds = ElementBounds.Fixed(PanelPadding, 46, width - PanelPadding * 2, TabsHeight);

		double buttonsY = height - BottomPadding - ButtonHeight;

		double statusY = buttonsY - 28;
		double contentY = 82;
		double contentBottom = statusY - 10;

		bool canManageServer = Page?.CanManageServer == true;
		bool configurationExternallyManaged = Page?.Success == true && Page.ConfigurationExternallyManaged;

		if (!canManageServer) { ActiveTab = 0; }

		bool showingDependencies = canManageServer && ActiveTab == 1;
		bool showSearch = Page?.Success == true && !showingDependencies && !configurationExternallyManaged;
		double listY = contentY + (showSearch ? SearchHeight + SearchGap : 0);
		double contentPanelHeight = Math.Max(100, contentBottom - contentY);
		double visibleHeight = Math.Max(100, contentBottom - listY);
		string searchQuery = SearchText.Trim();

		ImmConfigBlockPacket[] blocks = GetBlocks();
		ImmDependencyPacket[] dependencyIssues = GetDependencies();

		double availableContentWidth = width - PanelPadding * 2;

		List<BlockLayout> layouts = showingDependencies ? new List<BlockLayout>() : BuildLayouts(blocks, availableContentWidth - 8, searchQuery);
		List<DependencyLayout> dependencyLayouts = showingDependencies ? BuildDependencyLayouts(dependencyIssues, availableContentWidth - 8) : new List<DependencyLayout>();

		double calculatedHeight = showingDependencies ? dependencyLayouts.Count == 0 ? 40 : dependencyLayouts[^1].Bottom + 4 : layouts.Count == 0 ? 40 : layouts[^1].Bottom + 4;

		ContentHeight = (float)Math.Max(visibleHeight, calculatedHeight);
		NeedScrollbar = Page?.Success == true && ContentHeight > visibleHeight + 1;

		double contentWidth = availableContentWidth - (NeedScrollbar ? ScrollbarWidth + ScrollbarGap : 0);

		if (NeedScrollbar)
		{
			if (showingDependencies)
			{
				dependencyLayouts = BuildDependencyLayouts(dependencyIssues, contentWidth - 8);
				calculatedHeight = dependencyLayouts.Count == 0 ? 40 : dependencyLayouts[^1].Bottom + 4;
			}
			else
			{
				layouts = BuildLayouts(blocks, contentWidth - 8, searchQuery);

				calculatedHeight = layouts.Count == 0 ? 40 : layouts[^1].Bottom + 4;
			}

			ContentHeight = (float)Math.Max(visibleHeight, calculatedHeight);
		}

		ScrollValue = Math.Clamp(ScrollValue, 0, Math.Max(0, ContentHeight - (float)visibleHeight));

		ContentClipBounds = ElementBounds.Fixed(PanelPadding, listY, contentWidth, visibleHeight);

		ElementBounds listBounds = ElementBounds.Fixed(0, -ScrollValue, contentWidth, ContentHeight);

		ElementBounds contentPanelBounds = ElementBounds.Fixed(PanelPadding - 8, contentY - 8, width - PanelPadding * 2 + 16, contentPanelHeight + 16);
		ElementBounds searchBounds = ElementBounds.Fixed(PanelPadding + 4, contentY, availableContentWidth - 8, SearchHeight);

		ElementBounds scrollbarBounds = ElementBounds.Fixed(PanelPadding + contentWidth + ScrollbarGap, listY, ScrollbarWidth, visibleHeight).WithFixedPadding(2);

		bool showApplyButton = !showingDependencies && !configurationExternallyManaged;
		double buttonsWidth = showApplyButton ? ButtonWidth * 2 + ButtonGap : ButtonWidth;

		double buttonsX = (width - buttonsWidth) / 2;

		ElementBounds applyBounds = ElementBounds.Fixed(buttonsX, buttonsY, ButtonWidth, ButtonHeight);
		ElementBounds closeBounds = ElementBounds.Fixed(showApplyButton ? buttonsX + ButtonWidth + ButtonGap : buttonsX, buttonsY, ButtonWidth, ButtonHeight);
		ElementBounds statusBounds = ElementBounds.Fixed(PanelPadding, statusY, width - PanelPadding * 2, 20);

		SingleComposer?.Dispose();

		string titleText = GetSelectedModName();
		CairoFont titleFont = CreateSingleLineTitleFont(titleText, titleBounds);
		CairoFont tabFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
		CairoFont selectedTabFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithColor(GuiStyle.ActiveButtonTextColor);

		string configurationTabText = ImmLocalization.Get("tab-configuration");
		string dependenciesTabText = ImmLocalization.Get("tab-dependencies");

		GuiTab[] managerTabDefinitions = canManageServer ? new[] { new GuiTab { Name = configurationTabText, DataInt = 0 }, new GuiTab { Name = dependenciesTabText, DataInt = 1 } } : new[] { new GuiTab { Name = configurationTabText, DataInt = 0 } };

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-modmanager", dialogBounds).AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false).AddStaticText(titleText, titleFont, titleBounds).AddHorizontalTabs(managerTabDefinitions, tabsBounds, OnManagerTabClicked, tabFont, selectedTabFont, "managertabs").AddInset(contentPanelBounds, depth: 4, brightness: 0.85f);

		if (showSearch) { composer.AddInteractiveElement(new GuiElementImmSearchInput(capi, searchBounds, OnSearchTextChanged, CairoFont.TextInput()), "configsearch"); }

		GuiElementHorizontalTabs managerTabs = composer.GetHorizontalTabs("managertabs");

		managerTabs.unscaledTabPadding = 16;
		managerTabs.unscaledTabSpacing = 8;
		managerTabs.activeElement = ActiveTab;

		double tabPadding = GuiElement.scaled(managerTabs.unscaledTabPadding);
		double tabSpacing = GuiElement.scaled(managerTabs.unscaledTabSpacing);
		double configurationTabWidth = (int)(tabFont.GetTextExtents(configurationTabText).Width + 2 * tabPadding + 1);

		if (canManageServer)
		{
			DependencyTabWidth = (int)(tabFont.GetTextExtents(dependenciesTabText).Width + 2 * tabPadding + 1);

			DependencyTabOffset = tabSpacing + configurationTabWidth + tabSpacing;

			DependencyTabDiagnosticLevel = GetDependencyDiagnosticLevel(dependencyIssues);
		}
		else
		{
			DependencyTabWidth = 0;
			DependencyTabOffset = 0;
			DependencyTabDiagnosticLevel = ImmDiagnosticLevel.None;
		}

		if (Page?.Success == true)
		{
			if (configurationExternallyManaged && !showingDependencies)
			{
				string message = ImmLocalization.Get("configuration-external-manager-controlled");
				if (!string.IsNullOrWhiteSpace(Page?.ExternalManagerName)) { message += ImmLocalization.Get("configuration-external-manager-controlled-by", Page.ExternalManagerName); }

				composer.AddStaticText(message, CairoFont.WhiteSmallText(), ElementBounds.Fixed(PanelPadding + 4, contentY + 4, contentWidth - 8, visibleHeight - 8));
			}
			else
			{
				ConfigContainer = new GuiElementContainer(capi, listBounds) { InsideClipBounds = ContentClipBounds, unscaledCellSpacing = 0 };

				if (showingDependencies) { PopulateDependencyContainer(ConfigContainer, dependencyLayouts, contentWidth); }
				else { PopulateContainer(ConfigContainer, layouts, contentWidth); }

				ConfigContainer.Tabbable = ConfigContainer.Elements.Any(element => element.Focusable);
				composer.BeginClip(ContentClipBounds).AddInteractiveElement(ConfigContainer, "managerlist").EndClip();

				if (NeedScrollbar) { composer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, "configscroll"); }
			}
		}
		else
		{
			string message = Page == null ? ImmLocalization.Get("status-loading") : Page.Error ?? ImmLocalization.Get("error-load-manager-data");
			composer.AddStaticText(message, CairoFont.WhiteSmallText(), ElementBounds.Fixed(PanelPadding + 4, contentY + 4, contentWidth - 8, visibleHeight - 8));
		}

		composer.AddStaticText(StatusMessage, CairoFont.WhiteDetailText(), statusBounds);

		if (showApplyButton) { composer.AddSmallButton(Applying ? ImmLocalization.Get("button-saving") : ImmLocalization.Get("button-apply"), OnApplyClicked, applyBounds, key: "apply"); }
		composer.AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds);

		SingleComposer = composer.Compose();

		if (showSearch)
		{
			GuiElementTextInput searchInput = SingleComposer.GetTextInput("configsearch");
			searchInput.SetPlaceHolderText(ImmLocalization.Get("manager-search-placeholder"));

			SettingSearchValue = true;
			searchInput.SetValue(SearchText);
			SettingSearchValue = false;

			if (restoreSearchFocus)
			{
				searchInput.SetCaretPos(Math.Min(previousSearchCaret, SearchText.Length));
				SingleComposer.FocusElement(searchInput.TabIndex);
			}
		}

		managerTabs.Bounds.CalcWorldBounds();

		double totalTabWidth = tabSpacing + configurationTabWidth + tabSpacing + DependencyTabWidth + tabSpacing;

		DependencyTabPulseVisible = canManageServer && DependencyTabDiagnosticLevel != ImmDiagnosticLevel.None && ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode) && totalTabWidth <= managerTabs.Bounds.InnerWidth;

		if (DependencyTabPulseVisible && DependencyTabFillTexture == null) { DependencyTabFillTexture = ImmDiagnosticPulse.CreateSolidTexture(capi); }
		else if (!DependencyTabPulseVisible && DependencyTabFillTexture != null)
		{
			DependencyTabFillTexture.Dispose();
			DependencyTabFillTexture = null;
		}

		if (canManageServer && DependencyTabDiagnosticLevel != ImmDiagnosticLevel.None && ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode) && !DependencyTabPulseVisible)
		{
			double[] alarmColor = DependencyTabDiagnosticLevel == ImmDiagnosticLevel.Error ? new double[] { 1.0, 0.45, 0.40, 1.0 } : new double[] { 1.0, 0.78, 0.28, 1.0 };

			managerTabs.WithAlarmTabs(CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold).WithColor(alarmColor));

			managerTabs.TabHasAlarm[1] = true;
		}

		if (Page?.Success == true)
		{
			if (!showingDependencies && !configurationExternallyManaged) { InitializeControlValues(); }

			if (NeedScrollbar)
			{
				GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar("configscroll");
				scrollbar.SetHeights((float)visibleHeight, ContentHeight);
				SetScroll(ScrollValue);
			}
		}

		UpdateApplyButton();
	}

	private List<BlockLayout> BuildLayouts(ImmConfigBlockPacket[] blocks, double contentWidth, string searchQuery)
	{
		List<BlockLayout> layouts = new();

		double y = 4;
		double cardWidth = Math.Max(120, contentWidth - CardX * 2);
		double cardTextWidth = Math.Max(80, cardWidth - CardPadding * 2);
		bool searching = searchQuery.Length > 0;

		foreach (ImmConfigBlockPacket block in blocks)
		{
			ImmConfigControlPacket[] blockControls = block.Controls ?? Array.Empty<ImmConfigControlPacket>();
			if (searching) { blockControls = blockControls.Where(control => control.Label.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).ToArray(); }
			if (searching && blockControls.Length == 0) { continue; }

			double blockDescriptionHeight = ImmRichText.Measure(capi, NormalizeDescription(block.Description), CairoFont.WhiteDetailText(), contentWidth);
			double headerHeight = BlockTitleHeight + (blockDescriptionHeight > 0 ? BlockDescriptionGap + blockDescriptionHeight : 0);
			double cardY = y + headerHeight + BlockContentGap;

			List<ControlLayout> controls = new();

			foreach (ImmConfigControlPacket control in blockControls)
			{
				bool inlineBoolean = control.Available && control.Type == "Boolean";

				double bodyHeight;

				if (inlineBoolean) { bodyHeight = BooleanRowHeight; }
				else { bodyHeight = control.Available ? ControlHeight : Math.Max(ControlHeight, MeasureTextHeight(control.UnavailableReason, CairoFont.WhiteDetailText(), cardTextWidth)); }

				double descriptionHeight = ImmRichText.Measure(capi, GetControlDescription(control), CairoFont.WhiteDetailText(), cardTextWidth);

				double cardHeight = inlineBoolean ? CardPadding + BooleanRowHeight + (descriptionHeight > 0 ? DescriptionGap + descriptionHeight + DescriptionBottomPadding : 0) + CardPadding : CardPadding + SettingLabelHeight + LabelControlGap + bodyHeight + (descriptionHeight > 0 ? DescriptionGap + descriptionHeight + DescriptionBottomPadding : 0) + CardPadding;

				controls.Add(new ControlLayout(control, cardY, cardHeight, bodyHeight, descriptionHeight, inlineBoolean));

				cardY += cardHeight + CardGap;
			}

			double blockBottom = controls.Count > 0 ? controls[^1].Y + controls[^1].Height : y + headerHeight;

			layouts.Add(new BlockLayout(block, y, headerHeight, blockDescriptionHeight, controls, blockBottom));

			y = blockBottom + BlockGap;
		}

		return layouts;
	}

	private List<DependencyLayout> BuildDependencyLayouts(ImmDependencyPacket[] dependencies, double contentWidth)
	{
		List<DependencyLayout> layouts = new();

		double y = 4;
		double cardWidth = Math.Max(120, contentWidth - CardX * 2);

		CairoFont labelFont = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);

		foreach (ImmDependencyPacket dependency in dependencies)
		{
			bool pendingRestart = PendingRestartDependencies.ContainsKey(dependency.RuntimeId);
			bool hasInspect = !pendingRestart && (!string.IsNullOrWhiteSpace(dependency.Description) || dependency.HasResolution);

			double labelWidth = Math.Max(60, cardWidth - CardPadding * 2 - (hasInspect ? DependencyInspectWidth + DependencyLabelGap : 0));

			double labelHeight = Math.Max(SettingLabelHeight, MeasureTextHeight(dependency.Label, labelFont, labelWidth));

			double bodyHeight = pendingRestart ? labelHeight + DependencyRestartGap + DependencyRestartTextHeight : Math.Max(labelHeight, hasInspect ? DependencyInspectHeight : 0);

			double cardHeight = Math.Max(DependencyCardMinHeight, CardPadding * 2 + bodyHeight);

			layouts.Add(new DependencyLayout(dependency, y, cardHeight, labelHeight, hasInspect, y + cardHeight));

			y += cardHeight + CardGap;
		}

		return layouts;
	}

	private void PopulateDependencyContainer(GuiElementContainer container, List<DependencyLayout> layouts, double contentWidth)
	{
		double innerWidth = Math.Max(120, contentWidth - 8);

		if (layouts.Count == 0) { AddText(container, ImmLocalization.Get("dependencies-none"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(4, 4, innerWidth, 24)); return; }

		foreach (DependencyLayout layout in layouts)
		{
			ImmDependencyPacket dependency = layout.Dependency;
			bool pendingRestart = PendingRestartDependencies.ContainsKey(dependency.RuntimeId);

			double cardWidth = Math.Max(120, innerWidth - CardX * 2);

			ElementBounds cardBounds = ElementBounds.Fixed(CardX, layout.Y, cardWidth, layout.Height);

			container.Add(new GuiElementDependencyCard(capi, dependency.Severity, pendingRestart, cardBounds));

			double contentX = CardX + CardPadding;

			double labelWidth = Math.Max(60, cardWidth - CardPadding * 2 - (layout.HasInspect ? DependencyInspectWidth + DependencyLabelGap : 0));
			double labelY = pendingRestart ? layout.Y + CardPadding : layout.Y + (layout.Height - layout.LabelHeight) / 2;

			AddText(container, dependency.Label, CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(contentX, labelY, labelWidth, layout.LabelHeight));

			if (pendingRestart)
			{
				string restartText = ImmLocalization.Get("dependency-restart-required");
				ElementBounds restartBounds = ElementBounds.Fixed(contentX, labelY + layout.LabelHeight + DependencyRestartGap, cardWidth - CardPadding * 2, DependencyRestartTextHeight);
				CairoFont restartFont = CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold).WithColor(GuiStyle.ErrorTextColor).WithOrientation(EnumTextOrientation.Center);
				GuiElementStaticText restartElement = new(capi, restartText, EnumTextOrientation.Center, restartBounds, restartFont);

				restartElement.AutoFontSize();
				restartFont.UnscaledFontsize *= 0.96;
				container.Add(restartElement);
				continue;
			}

			if (!layout.HasInspect) { continue; }

			ElementBounds inspectBounds = ElementBounds.Fixed(CardX + cardWidth - CardPadding - DependencyInspectWidth, layout.Y + (layout.Height - DependencyInspectHeight) / 2, DependencyInspectWidth, DependencyInspectHeight);

			CairoFont buttonFont = CairoFont.SmallButtonText();

			CairoFont pressedFont = CairoFont.SmallButtonText();

			pressedFont.Color = (double[]) GuiStyle.ActiveButtonTextColor.Clone();

			GuiElementTextButton inspectButton = new(capi, ImmLocalization.Get("button-inspect"), buttonFont, pressedFont, () => OnInspectDependency(dependency), inspectBounds, EnumButtonStyle.Normal);

			inspectButton.SetOrientation(buttonFont.Orientation);

			container.Add(inspectButton);
		}
	}

	private void PopulateContainer(GuiElementContainer container, List<BlockLayout> layouts, double contentWidth)
	{
		double innerWidth = Math.Max(120, contentWidth - 8);

		if (layouts.Count == 0)
		{
			string message = SearchText.Trim().Length > 0 ? ImmLocalization.Get("configuration-search-none") : ImmLocalization.Get("configuration-none");
			AddText(container, message, CairoFont.WhiteSmallText(), ElementBounds.Fixed(4, 4, innerWidth, 24));
			return;
		}

		foreach (BlockLayout layout in layouts)
		{
			AddText(container, layout.Block.ConfigLabel, CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(4, layout.Y, innerWidth, BlockTitleHeight));

			if (layout.DescriptionHeight > 0) { AddRichText(container, NormalizeDescription(layout.Block.Description), CairoFont.WhiteDetailText(), ElementBounds.Fixed(4, layout.Y + BlockTitleHeight + BlockDescriptionGap, innerWidth, layout.DescriptionHeight)); }

			foreach (ControlLayout controlLayout in layout.Controls) { AddControlCard(container, controlLayout, innerWidth); }
		}
	}

	private void AddControlCard(GuiElementContainer container, ControlLayout layout, double contentWidth)
	{
		ImmConfigControlPacket control = layout.Control;

		double cardWidth = Math.Max(120, contentWidth - CardX * 2);

		ElementBounds cardBounds = ElementBounds.Fixed(CardX, layout.Y, cardWidth, layout.Height);

		container.Add(new GuiElementConfigCard(capi, cardBounds));

		double contentX = CardX + CardPadding;

		double contentWidthInside = Math.Max(80, cardWidth - CardPadding * 2);

		double labelY = layout.Y + CardPadding;

		if (layout.InlineBoolean)
		{
			double labelWidth = Math.Max(40, contentWidthInside - BooleanToggleSize - BooleanToggleGap);

			AddText(container, control.Label, CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(contentX, labelY + (BooleanRowHeight - SettingLabelHeight) / 2, labelWidth, SettingLabelHeight));

			AddControlElement(container, control, ElementBounds.Fixed(contentX + contentWidthInside - BooleanToggleSize, labelY + (BooleanRowHeight - BooleanToggleSize) / 2, BooleanToggleSize, BooleanToggleSize));

			if (layout.DescriptionHeight > 0) { AddRichText(container, GetControlDescription(control), CairoFont.WhiteDetailText(), ElementBounds.Fixed(contentX, labelY + BooleanRowHeight + DescriptionGap, contentWidthInside, layout.DescriptionHeight)); }

			return;
		}

		AddText(container, control.Label, CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(contentX, labelY, contentWidthInside, SettingLabelHeight));

		double bodyY = labelY + SettingLabelHeight + LabelControlGap;

		if (!control.Available) { AddText(container, control.UnavailableReason, CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor), ElementBounds.Fixed(contentX, bodyY, contentWidthInside, layout.BodyHeight)); }
		else { AddControlElement(container, control, ElementBounds.Fixed(contentX, bodyY, contentWidthInside, ControlHeight)); }

		if (layout.DescriptionHeight > 0) { AddRichText(container, GetControlDescription(control), CairoFont.WhiteDetailText(), ElementBounds.Fixed(contentX, bodyY + layout.BodyHeight + DescriptionGap, contentWidthInside, layout.DescriptionHeight)); }
	}

	private void AddControlElement(GuiElementContainer container, ImmConfigControlPacket control, ElementBounds bounds)
	{
		control.Options ??= Array.Empty<ImmConfigOptionPacket>();

		ElementBounds inputBounds = ElementBounds.Fixed(bounds.fixedX, bounds.fixedY + 3, bounds.fixedWidth, 30);

		GuiElement element;

		switch (control.Type)
		{
			case "Boolean":
				element = new GuiElementImmSwitch(capi, value => SetPendingBoolean(control, value), ElementBounds.Fixed(bounds.fixedX, bounds.fixedY, bounds.fixedWidth, bounds.fixedHeight), size: BooleanToggleSize, padding: 3);
			break;

			case "String":
				element = new GuiElementImmTextInput(capi, inputBounds, value => SetPendingString(control, value), CairoFont.TextInput());
			break;

			case "Integer":
			case "Decimal":
				element = new GuiElementImmNumberInput(capi, inputBounds, value => SetPendingNumber(control, value), CairoFont.TextInput());
			break;

			case "Slider":
				element = new GuiElementImmSlider(capi, value => SetPendingSlider(control, value), ElementBounds.Fixed(bounds.fixedX, bounds.fixedY + 8, bounds.fixedWidth, 20));
			break;

			case "Dropdown":
				int selectedIndex = GetDropdownSelectedIndex(control);

				element = new GuiElementImmDropDown(capi, Enumerable.Range(0, control.Options.Length).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray(), control.Options.Select(option => option.Label).ToArray(), selectedIndex, (value, selected) => SetPendingDropdown(control, value, selected), inputBounds, CairoFont.WhiteSmallText(), multiSelect: false);
			break;

			case "Array":
				CairoFont buttonFont = CairoFont.SmallButtonText();

				CairoFont pressedFont = CairoFont.SmallButtonText();

				pressedFont.Color = (double[]) GuiStyle.ActiveButtonTextColor.Clone();

				GuiElementTextButton editListButton = new(capi, ImmLocalization.Get("button-edit-list"), buttonFont, pressedFont, () => OnEditArray(control), inputBounds, EnumButtonStyle.Normal);

				editListButton.SetOrientation(buttonFont.Orientation);

				element = editListButton;
			break;

			default: return;
		}

		container.Add(element);
		ControlElements[control.Index] = element;
	}

	private void AddText(GuiElementContainer container, string text, CairoFont font, ElementBounds bounds) { container.Add(new GuiElementStaticText(capi, text, font.Orientation, bounds, font)); }
	private void AddRichText(GuiElementContainer container, string text, CairoFont font, ElementBounds bounds) { container.Add(ImmRichText.Create(capi, text, font, bounds)); }

	private void InitializeControlValues()
	{
		SettingControlValues = true;

		try
		{
			foreach (ImmConfigControlPacket control in GetControls())
			{
				if (!control.Available || !ControlElements.TryGetValue(control.Index, out GuiElement? element)) { continue; }

				JToken value = JToken.Parse(control.ValueJson);

				switch (element)
				{
					case GuiElementSwitch toggle:
						toggle.SetValue(value.Value<bool>());
					break;

					case GuiElementNumberInput number:
						number.IntMode = control.Type == "Integer";
						number.Interval = control.Type == "Integer" ? 1 : 0.1f;
						number.SetValue(InvalidInputValues.TryGetValue(control.Index, out string? invalidValue) ? invalidValue : control.Type == "Integer" ? value.Value<int>().ToString(CultureInfo.InvariantCulture) : value.Value<double>().ToString("G", GlobalConstants.DefaultCultureInfo));
					break;

					case GuiElementTextInput input:
						input.SetValue(value.Value<string>() ?? "");
					break;

					case GuiElementSlider slider:
						InitializeSlider(slider, control, value);
					break;
				}
			}
		}
		finally { SettingControlValues = false; }
	}

	private static void InitializeSlider(GuiElementSlider slider, ImmConfigControlPacket control, JToken value)
	{
		bool integerTarget = value.Type == JTokenType.Integer;
		double step = control.HasStep ? control.Step : 1;
		int tickCount = (int)Math.Round((control.Max - control.Min) / step);
		int currentTick = (int)Math.Round((value.Value<double>() - control.Min) / step);

		slider.ShowTextWhenResting = false;
		slider.OnSliderTooltip = tick => FormatSliderValue(control.Min + tick * step, integerTarget);
		slider.OnSliderRestingText = null;
		slider.SetValues(currentTick, 0, tickCount, 1);
	}

	private bool OnEditArray(ImmConfigControlPacket control) { ArrayEditorDialog.Show(control.Label, control.ElementType, control.ValueJson, valueJson => SetPendingValue(control, valueJson)); return true; }

	private void SetPendingBoolean(ImmConfigControlPacket control, bool value) { SetPendingValue(control, value ? "true" : "false"); }

	private void SetPendingString(ImmConfigControlPacket control, string value) { SetPendingValue(control, JsonConvert.SerializeObject(value)); }

	private void SetPendingNumber(ImmConfigControlPacket control, string value)
	{
		if (SettingControlValues) { return; }

		if (control.Type == "Integer")
		{
			if (int.TryParse(value, NumberStyles.Integer, GlobalConstants.DefaultCultureInfo, out int integerValue))
			{
				InvalidInputs.Remove(control.Index);
				InvalidInputValues.Remove(control.Index);

				SetPendingValue(control, integerValue.ToString(CultureInfo.InvariantCulture));
			}
			else
			{
				InvalidInputs.Add(control.Index);
				InvalidInputValues[control.Index] = value;
				UpdateApplyButton();
			}

			return;
		}

		if (double.TryParse(value, NumberStyles.Float, GlobalConstants.DefaultCultureInfo, out double decimalValue) && double.IsFinite(decimalValue))
		{
			InvalidInputs.Remove(control.Index);
			InvalidInputValues.Remove(control.Index);

			SetPendingValue(control, JsonConvert.SerializeObject(decimalValue));
		}
		else
		{
			InvalidInputs.Add(control.Index);
			InvalidInputValues[control.Index] = value;
			UpdateApplyButton();
		}
	}

	private bool SetPendingSlider(ImmConfigControlPacket control, int tick)
	{
		if (SettingControlValues) { return true; }
		JToken current = JToken.Parse(control.ValueJson);

		bool integerTarget = current.Type == JTokenType.Integer;
		double step = control.HasStep ? control.Step : 1;
		double value = control.Min + tick * step;

		SetPendingValue(control, integerTarget ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : JsonConvert.SerializeObject(value));
		return true;
	}

	private void SetPendingDropdown(ImmConfigControlPacket control, string value, bool selected)
	{
		if (SettingControlValues || !selected || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= control.Options.Length) { return; }

		SetPendingValue(control, control.Options[index].ValueJson);
	}

	private void SetPendingValue(ImmConfigControlPacket control, string valueJson)
	{
		if (SettingControlValues) { return; }

		control.ValueJson = valueJson;

		if (OriginalValues.TryGetValue(control.Index, out string? original) && JsonValuesEqual(original, valueJson)) { PendingChanges.Remove(control.Index); }
		else { PendingChanges[control.Index] = valueJson; }

		UpdateApplyButton();
	}

	private int GetDropdownSelectedIndex(ImmConfigControlPacket control)
	{
		if (string.IsNullOrWhiteSpace(control.ValueJson) || control.Options.Length == 0) { return 0; }
		JToken current = JToken.Parse(control.ValueJson);

		for (int index = 0; index < control.Options.Length; index++)
		{
			if (JToken.DeepEquals(current, JToken.Parse(control.Options[index].ValueJson))) { return index; }
		}

		return 0;
	}

	private bool OnApplyClicked()
	{
		if (ActiveTab != 0 || Applying || PendingChanges.Count == 0 || InvalidInputs.Count > 0 || string.IsNullOrWhiteSpace(SelectedModId) || Page?.Success != true || Page.ConfigurationExternallyManaged) { return true; }

		Dictionary<int, ImmConfigControlPacket> controls = GetControls().ToDictionary(control => control.Index);
		ImmConfigChangePacket[] clientChanges = PendingChanges.Where(change => controls[change.Key].ConfigSide == ImmConfigSide.Client).Select(ToChangePacket).ToArray();
		ImmConfigChangePacket[] serverChanges = PendingChanges.Where(change => controls[change.Key].ConfigSide == ImmConfigSide.Server).Select(ToChangePacket).ToArray();

		if (Page.CanManageServer == false && serverChanges.Length > 0)
		{
			StatusMessage = ImmLocalization.Get("error-server-settings-unavailable");
			Compose();
			return true;
		}

		ClientChangesSavedDuringApply = false;
		ClientReloadRequirementDuringApply = ImmReloadRequirement.None;

		if (clientChanges.Length > 0)
		{
			if (!ConfigClient.TryApplyClient(Page, clientChanges, out ImmReloadRequirement clientReloadRequirement, out string clientError))
			{
				StatusMessage = clientError;
				Compose();
				return true;
			}

			RemovePendingChanges(clientChanges.ToDictionary(change => change.Index, change => change.ValueJson));

			ClientChangesSavedDuringApply = true;
			ClientReloadRequirementDuringApply = clientReloadRequirement;
		}

		if (serverChanges.Length == 0)
		{
			StatusMessage = GetSavedStatusMessage(ClientReloadRequirementDuringApply);

			ShowAppliedChatMessage(ClientReloadRequirementDuringApply);

			ClientChangesSavedDuringApply = false;
			ClientReloadRequirementDuringApply = ImmReloadRequirement.None;

			RefreshingAfterApply = true;
			ConfigClient.RequestPage(SelectedModId);

			Compose();
			return true;
		}

		Applying = true;
		ApplyingChanges = serverChanges.ToDictionary(change => change.Index, change => change.ValueJson);

		ConfigClient.ApplyServer(SelectedModId, serverChanges);

		UpdateApplyButton();
		return true;
	}

	private static ImmConfigChangePacket ToChangePacket(KeyValuePair<int, string> change) { return new ImmConfigChangePacket { Index = change.Key, ValueJson = change.Value }; }

	private void RemovePendingChanges(IReadOnlyDictionary<int, string> changes)
	{
		foreach (KeyValuePair<int, string> change in changes)
		{
			if (PendingChanges.TryGetValue(change.Key, out string? current) && string.Equals(current, change.Value, StringComparison.Ordinal))
			{
				PendingChanges.Remove(change.Key);
				OriginalValues[change.Key] = change.Value;
			}
		}
	}

	private void CaptureOriginalValues()
	{
		if (Page?.Configuration == null) { return; }

		foreach (ImmConfigBlockPacket block in Page.Configuration)
		{
			foreach (ImmConfigControlPacket control in block.Controls ?? Array.Empty<ImmConfigControlPacket>()) { OriginalValues[control.Index] = control.ValueJson ?? ""; }
		}
	}

	private static bool JsonValuesEqual(string first, string second)
	{
		if (string.Equals(first, second, StringComparison.Ordinal)) { return true; }

		try { return JToken.DeepEquals(JToken.Parse(first), JToken.Parse(second)); }
		catch { return false; }
	}

	private void UpdateApplyButton()
	{
		if (ActiveTab != 0) { return; }

		GuiElementTextButton? applyButton = SingleComposer?.GetButton("apply");

		if (applyButton != null) { applyButton.Enabled = Page?.Success == true && !Applying && InvalidInputs.Count == 0 && PendingChanges.Count > 0; }
	}

	private void OnScrollbarChanged(float value)
	{
		ScrollValue = Math.Clamp(value, 0, Math.Max(0, ContentHeight - (float)(ContentClipBounds?.fixedHeight ?? 0)));

		if (ConfigContainer == null) { return; }

		ConfigContainer.Bounds.fixedY = -ScrollValue;

		ConfigContainer.Bounds.MarkDirtyRecursive();
		ConfigContainer.Bounds.CalcWorldBounds();
	}

	private void SetScroll(float value)
	{
		if (!NeedScrollbar)
		{
			ScrollValue = 0;
			OnScrollbarChanged(0);
			return;
		}

		float maxScroll = Math.Max(0, ContentHeight - (float)(ContentClipBounds?.fixedHeight ?? 0));

		ScrollValue = Math.Clamp(value, 0, maxScroll);

		OnScrollbarChanged(ScrollValue);

		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("configscroll");

		if (scrollbar != null)
		{
			scrollbar.CurrentYPosition = ScrollValue;
			scrollbar.RecomposeHandle();
		}
	}

	private ImmConfigBlockPacket[] GetBlocks() { return Page?.Success == true ? (Page.Configuration ?? Array.Empty<ImmConfigBlockPacket>()).Where(block => block != null).ToArray() : Array.Empty<ImmConfigBlockPacket>(); }
	private ImmDependencyPacket[] GetDependencies() { ImmConfigPageResponse? page = Page; return page?.Success == true && page.CanManageServer ? (page.Dependencies ?? Array.Empty<ImmDependencyPacket>()).Where(dependency => dependency != null).ToArray() : Array.Empty<ImmDependencyPacket>(); }
	private IEnumerable<ImmConfigControlPacket> GetControls() { return GetBlocks().SelectMany(block => (block.Controls ?? Array.Empty<ImmConfigControlPacket>()).Where(control => control != null)); }

	private string GetSelectedModName() { string title = !string.IsNullOrWhiteSpace(SelectedModName) ? SelectedModName : string.IsNullOrWhiteSpace(SelectedModId) ? ImmLocalization.Get("mod-configuration-title") : SelectedModId; return title.Replace('\r', ' ').Replace('\n', ' '); }

	private static CairoFont CreateSingleLineTitleFont(string text, ElementBounds bounds)
	{
		CairoFont font = CairoFont.WhiteMediumText().WithWeight(FontWeight.Bold);

		double maxWidth = Math.Max(1, (bounds.fixedWidth - 4) * RuntimeEnv.GUIScale);

		double maxHeight = Math.Max(1, (bounds.fixedHeight - 2) * RuntimeEnv.GUIScale);

		double textWidth = Math.Max(1, font.GetTextExtents(text).Width);

		double textHeight = Math.Max(1, font.GetFontExtents().Height);

		double scale = Math.Min(1, Math.Min(maxWidth / textWidth, maxHeight / textHeight));

		font.UnscaledFontsize *= Math.Max(0.05, scale * 0.96);

		return font;
	}

	private static string FormatSliderValue(double value, bool integer) { return integer ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.####", CultureInfo.InvariantCulture); }

	private static string NormalizeDescription(string? description)
	{
		if (string.IsNullOrWhiteSpace(description)) { return ""; }

		return description.Replace("\r\n", "\n").Replace('\r', '\n');
	}

	private static double MeasureTextHeight(string? text, CairoFont font, double width)
	{
		if (string.IsNullOrWhiteSpace(text)) { return 0; }

		TextDrawUtil textUtil = new();

		return Math.Ceiling(textUtil.GetMultilineTextHeight(font, text, Math.Max(1, width)));
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
		if (IsOpened()) { ApplySearch(); }
	}

	private void CancelPendingSearch()
	{
		if (SearchCallbackId < 0) { return; }

		capi.Event.UnregisterCallback(SearchCallbackId);
		SearchCallbackId = -1;
	}

	private void ApplySearch()
	{
		if (!IsOpened()) { return; }

		ScrollValue = 0;
		Compose();
	}

	private void OnManagerTabClicked(int tabIndex)
	{
		int nextTab = Math.Clamp(tabIndex, 0, Page?.CanManageServer == true ? 1 : 0);

		if (ActiveTab == nextTab) { return; }

		ActiveTab = nextTab;
		ScrollValue = 0;
		Compose();
	}

	private bool OnInspectDependency(ImmDependencyPacket dependency)
	{
		bool canAutoResolve = dependency.HasResolution;

		DependencyIssueDialog.Show(dependency, canAutoResolve, OnAutoResolveDependency);

		return true;
	}

	private void OnAutoResolveDependency(int runtimeId)
	{
		if (ResolvingDependencyId >= 0) { return; }

		ResolvingDependencyId = runtimeId;
		StatusMessage = ImmLocalization.Get("status-applying-auto-resolution");

		ConfigClient.ResolveDependency(runtimeId);

		Compose();
	}

	private static ImmReloadRequirement CombineReloadRequirements(ImmReloadRequirement first, ImmReloadRequirement second) { return (ImmReloadRequirement)Math.Max((int)first, (int)second); }

	private static string GetSavedStatusMessage(ImmReloadRequirement reloadRequirement) { return reloadRequirement switch { ImmReloadRequirement.ServerRestart => ImmLocalization.Get("status-saved-server-restart"), ImmReloadRequirement.ReenterWorld => ImmLocalization.Get("status-saved-reenter-world"), _ => ImmLocalization.Get("status-saved") }; }

	private static string GetControlDescription(ImmConfigControlPacket control)
	{
		string description = NormalizeDescription(control.Description);

		if (!control.PendingReload) { return description; }

		string pending = ImmLocalization.Get("pending-reload-description");

		return string.IsNullOrWhiteSpace(description) ? pending : description + "\n" + pending;
	}

	private void ShowAppliedChatMessage(ImmReloadRequirement reloadRequirement)
	{
		string message = reloadRequirement switch { ImmReloadRequirement.ServerRestart => ImmLocalization.Get("chat-settings-saved-server-restart"), ImmReloadRequirement.ReenterWorld => ImmLocalization.Get("chat-settings-saved-reenter-world"), _ => ImmLocalization.Get("chat-settings-applied") };

		capi.ShowChatMessage(message);

		if (Page?.ExternalManagerActive == true) { capi.ShowChatMessage(ImmLocalization.Get("chat-external-manager-patch-risk")); }
	}

	private void ShowResolutionWarning(ImmDependencyResolutionWarning warning)
	{
		switch (warning)
		{
			case ImmDependencyResolutionWarning.ExternallyManagedModConfig:
				Notification.Show(ImmLocalization.Get("notification-autofix-external-modconfig"), ImmLocalization.Get("button-alright"));
			break;

			case ImmDependencyResolutionWarning.ExternallyManagedPatchSetting:
				Notification.Show(ImmLocalization.Get("notification-autofix-external-patchsetting"), ImmLocalization.Get("button-alright"));
			break;
		}
	}

	private bool OnCloseClicked()
	{
		DependencyIssueDialog.TryClose();
		ArrayEditorDialog.TryClose();
		TryClose();
		return true;
	}

	public override void OnGuiClosed()
	{
		CancelPendingSearch();
		DependencyIssueDialog.TryClose();
		ArrayEditorDialog.TryClose();
		base.OnGuiClosed();
	}

	public override void Dispose()
	{
		CancelPendingSearch();
		ConfigClient.PageReceived -= OnPageReceived;
		ConfigClient.ApplyReceived -= OnApplyReceived;
		ConfigClient.DependencyResolveReceived -= OnDependencyResolveReceived;
		capi.Event.LevelFinalize -= OnLevelFinalize;

		DependencyIssueDialog.Dispose();
		ArrayEditorDialog.Dispose();

		DependencyTabFillTexture?.Dispose();
		DependencyTabFillTexture = null;

		base.Dispose();
	}

	private sealed record PendingRestartDependency(string ModId, ImmDependencyPacket? Snapshot);
	private sealed record BlockLayout(ImmConfigBlockPacket Block, double Y, double HeaderHeight, double DescriptionHeight, List<ControlLayout> Controls, double Bottom);
	private sealed record DependencyLayout(ImmDependencyPacket Dependency, double Y, double Height, double LabelHeight, bool HasInspect, double Bottom);
	private sealed record ControlLayout(ImmConfigControlPacket Control, double Y, double Height, double BodyHeight, double DescriptionHeight, bool InlineBoolean);
}
