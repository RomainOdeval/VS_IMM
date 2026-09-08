#nullable enable

using System;
using Cairo;
using IntegratedModManager.Config;
using IntegratedModManager.UI;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace IntegratedModManager;

public sealed class IntegratedModManagerNudge : IRenderer
{
	public const string NetworkChannelCode = "integratedmodmanager-nudge";
	public const string DismissedModDataKey = "integratedmodmanager-nudge-dismissed";

	private const float SlideSpeed = 1.5f;
	private const float TextPaddingGui = 10f;
	private const float TitleBodyGapGui = 3f;

	// Vanilla's intro tip begins at 80 GUI pixels from the top and is roughly 60 GUI pixels tall.
	// Reserve that space plus a small gap, while also scaling the offset with the actual screen resolution.
	private const float VanillaIntroTipBottom = 140f;
	private const float NudgeGap = 12f;
	private const float MinimumScreenFraction = 0.14f;

	private readonly ICoreClientAPI ClientApi;

	private IClientNetworkChannel? ClientChannel;
	private LoadedTexture? TitleTexture;
	private LoadedTexture? BodyTexture;
	private LoadedTexture? BackgroundTexture;

	private readonly Vec4f BackgroundTint = new();
	private readonly Vec4f BorderTint = new();

	private bool StateRequested;
	private bool DismissedThisSession;
	private bool RendererRegistered;

	private float SlideAccum;
	private float ScreenY;
	private float NudgeWidth;
	private float NudgeHeight;
	private float TextPadding;
	private float TitleBodyGap;
	private ImmDiagnosticLevel DiagnosticLevel;
	private ImmImportantInformationHighlight HighlightMode = ImmImportantInformationHighlight.Pulsating;

	public double RenderOrder => 1.0;
	public int RenderRange => 0;

	[ProtoContract]
	public sealed class NudgeStateRequest
	{
		[ProtoMember(1)] public byte Request = 1;
	}

	[ProtoContract]
	public sealed class NudgeStateResponse
	{
		[ProtoMember(1)] public bool CanManageServer;
		[ProtoMember(2)] public bool PreviouslyDismissed;
		[ProtoMember(3)] public int WarningCount;
		[ProtoMember(4)] public int ErrorCount;
	}

	[ProtoContract]
	public sealed class NudgeDismissRequest
	{
		[ProtoMember(1)] public byte Request = 1;
	}

	public IntegratedModManagerNudge(ICoreClientAPI clientApi) { ClientApi = clientApi; }

	public static void RegisterNetwork(ICoreAPI api) { api.Network.RegisterChannel(NetworkChannelCode).RegisterMessageType<NudgeStateRequest>().RegisterMessageType<NudgeStateResponse>().RegisterMessageType<NudgeDismissRequest>(); }

	public static void StartServer(ICoreServerAPI api, ImmDependencyService dependencyService)
	{
		IServerNetworkChannel channel = api.Network.GetChannel(NetworkChannelCode);

		channel.SetMessageHandler<NudgeStateRequest>((fromPlayer, packet) =>
		{
			channel.SendPacket(new NudgeStateResponse
			{
				CanManageServer = fromPlayer.HasPrivilege(Privilege.controlserver),
				PreviouslyDismissed = fromPlayer.WorldData.GetModData(DismissedModDataKey, false),
				WarningCount = dependencyService.WarningCount,
				ErrorCount = dependencyService.ErrorCount
			}, fromPlayer);
		});

		channel.SetMessageHandler<NudgeDismissRequest>((fromPlayer, packet) => { fromPlayer.WorldData.SetModData(DismissedModDataKey, true); });
	}

	public void StartClient()
	{
		ClientChannel = ClientApi.Network.GetChannel(NetworkChannelCode).SetMessageHandler<NudgeStateResponse>(OnNudgeStateResponse);

		ClientApi.Event.PlayerEntitySpawn += OnPlayerEntitySpawn;
		ClientApi.Event.LeaveWorld += OnLeaveWorld;
	}

	private void OnPlayerEntitySpawn(IClientPlayer spawnedPlayer)
	{
		IClientPlayer? localPlayer = ClientApi.World?.Player;

		if (localPlayer == null || spawnedPlayer.PlayerUID != localPlayer.PlayerUID || StateRequested) { return; }

		StateRequested = true;
		ClientChannel?.SendPacket(new NudgeStateRequest());
	}

	private void OnNudgeStateResponse(NudgeStateResponse packet)
	{
		if (ClientApi.World?.Player != null && ShouldShowNudge(packet)) { Show(packet.WarningCount, packet.ErrorCount); }
	}

	private bool ShouldShowNudge(NudgeStateResponse packet)
	{
		if (!packet.CanManageServer || DismissedThisSession) { return false; }
		if (!packet.PreviouslyDismissed) { return true; }

		return IntegratedModManagerConfig.ConfiguredNudgeBehaviour switch
		{
			ImmNudgeBehaviour.WhenErrorsFound => packet.ErrorCount > 0,
			ImmNudgeBehaviour.WarningsOrErrors => packet.WarningCount > 0 || packet.ErrorCount > 0,
			_ => false
		};
	}

	public void Dismiss()
	{
		DismissedThisSession = true;
		Hide();
		ClientChannel?.SendPacket(new NudgeDismissRequest());
	}

	private static string BuildNudgeBodyText(int warningCount, int errorCount)
	{
		string instruction = ImmLocalization.Get("nudge-instruction");
		if (warningCount <= 0 && errorCount <= 0) { return instruction; }

		string warningText = warningCount == 1 ? ImmLocalization.Get("nudge-warning-one") : ImmLocalization.Get("nudge-warning-many", warningCount);
		string errorText = errorCount == 1 ? ImmLocalization.Get("nudge-error-one") : ImmLocalization.Get("nudge-error-many", errorCount);

		string issueText;

		if (warningCount > 0 && errorCount > 0)
		{
			if (errorCount == 1) { issueText = warningCount == 1 ? ImmLocalization.Get("nudge-error-one-warning-one") : ImmLocalization.Get("nudge-error-one-warning-many", warningCount); }
			else { issueText = warningCount == 1 ? ImmLocalization.Get("nudge-error-many-warning-one", errorCount) : ImmLocalization.Get("nudge-error-many-warning-many", errorCount, warningCount); }
		}
		else if (errorCount > 0) { issueText = errorCount == 1 ? ImmLocalization.Get("nudge-error-sentence-one") : ImmLocalization.Get("nudge-error-sentence-many", errorText); }
		else { issueText = warningCount == 1 ? ImmLocalization.Get("nudge-warning-sentence-one") : ImmLocalization.Get("nudge-warning-sentence-many", warningText); }

		return $"{issueText}\n{instruction}";
	}

	private void Show(int warningCount, int errorCount)
	{
		Hide();

		DiagnosticLevel = errorCount > 0 ? ImmDiagnosticLevel.Error : warningCount > 0 ? ImmDiagnosticLevel.Warning : ImmDiagnosticLevel.None;

		HighlightMode = IntegratedModManagerConfig.ConfiguredInformationHighlight;

		CairoFont titleFont = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
		CairoFont bodyFont = CairoFont.WhiteSmallText();

		if (DiagnosticLevel != ImmDiagnosticLevel.None && ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode))
		{
			titleFont.WithStroke(ColorUtil.BlackArgbDouble, 1.25);
			bodyFont.WithStroke(ColorUtil.BlackArgbDouble, 1.25);
		}

		TitleTexture = ClientApi.Gui.TextTexture.GenTextTexture(ImmLocalization.Get("title"), titleFont);

		BodyTexture = ClientApi.Gui.TextTexture.GenTextTexture(BuildNudgeBodyText(warningCount, errorCount), bodyFont);
		BackgroundTexture = ImmDiagnosticPulse.CreateSolidTexture(ClientApi);

		ImmDiagnosticPulse.SetGuiColor(GuiStyle.DialogBorderColor, BorderTint);

		TextPadding = (float)GuiElement.scaled(TextPaddingGui);
		TitleBodyGap = (float)GuiElement.scaled(TitleBodyGapGui);

		NudgeWidth = Math.Max(TitleTexture.Width, BodyTexture.Width) + TextPadding * 2;
		NudgeHeight = TitleTexture.Height + TitleBodyGap + BodyTexture.Height + TextPadding * 2;
		
		SlideAccum = 0f;
		ScreenY = Math.Max((VanillaIntroTipBottom + NudgeGap) * RuntimeEnv.GUIScale, ClientApi.Render.FrameHeight * MinimumScreenFraction);

		ClientApi.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "integratedmodmanager-nudge");
		RendererRegistered = true;
	}

	private void Hide()
	{
		SlideAccum = 0f;

		if (RendererRegistered)
		{
			ClientApi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);

			RendererRegistered = false;
		}

		TitleTexture?.Dispose();
		TitleTexture = null;

		BodyTexture?.Dispose();
		BodyTexture = null;

		BackgroundTexture?.Dispose();
		BackgroundTexture = null;

		NudgeWidth = 0;
		NudgeHeight = 0;
		DiagnosticLevel = ImmDiagnosticLevel.None;
		HighlightMode = ImmImportantInformationHighlight.Pulsating;
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (TitleTexture == null || BodyTexture == null || BackgroundTexture == null) { return; }

		SlideAccum += deltaTime;

		float slidePosition = GameMath.Clamp(SlideSpeed * SlideAccum, 0f, 1f) - 1f;
		float x = slidePosition * NudgeWidth;
		bool highlightEnabled = DiagnosticLevel != ImmDiagnosticLevel.None && ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode);
		double pulse = highlightEnabled ? ImmDiagnosticPulse.GetHighlightPhase(HighlightMode, ClientApi.ElapsedMilliseconds) : 0;

		ImmDiagnosticPulse.SetNudgeColor(highlightEnabled ? DiagnosticLevel : ImmDiagnosticLevel.None, pulse, BackgroundTint);

		ClientApi.Render.Render2DTexture(BackgroundTexture.TextureId, x, ScreenY, NudgeWidth, NudgeHeight, 48, BorderTint);
		ClientApi.Render.Render2DTexture(BackgroundTexture.TextureId, x + 1, ScreenY + 1, Math.Max(1, NudgeWidth - 2), Math.Max(1, NudgeHeight - 2), 49, BackgroundTint);

		float titleX = x + TextPadding;
		float titleY = ScreenY + TextPadding;

		ClientApi.Render.Render2DLoadedTexture(TitleTexture, titleX, titleY, 50);
		ClientApi.Render.Render2DLoadedTexture(BodyTexture, x + TextPadding, titleY + TitleTexture.Height + TitleBodyGap, 50);
	}

	private void OnLeaveWorld()
	{
		Hide();
		StateRequested = false;
		DismissedThisSession = false;
	}

	public void Dispose()
	{
		Hide();

		ClientApi.Event.PlayerEntitySpawn -= OnPlayerEntitySpawn;
		ClientApi.Event.LeaveWorld -= OnLeaveWorld;
	}
}
