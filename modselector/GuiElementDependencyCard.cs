#nullable enable

using Cairo;
using IntegratedModManager.Config;
using Vintagestory.API.Client;

namespace IntegratedModManager.ModSelector;

public sealed class GuiElementDependencyCard : GuiElement
{
	private readonly ImmDependencySeverity Severity;
	private readonly bool PendingRestart;

	public GuiElementDependencyCard(ICoreClientAPI capi, ImmDependencySeverity severity, bool pendingRestart, ElementBounds bounds) : base(capi, bounds)
	{
		Severity = severity;
		PendingRestart = pendingRestart;
	}

	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		// Visual chrome only. Do not consume clicks intended for controls layered inside the card.
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();

		double[] baseColor = GuiStyle.DialogDefaultBgColor;

		if (PendingRestart) { ctxStatic.SetSourceRGBA(0.18, 0.18, 0.18, baseColor[3]); }
		else
		{
			double targetRed	= Severity == ImmDependencySeverity.Error ? 0.62 : 0.58;
			double targetGreen	= Severity == ImmDependencySeverity.Error ? 0.12 : 0.46;
			double targetBlue	= Severity == ImmDependencySeverity.Error ? 0.10 : 0.08;

			const double tint = 0.42;

			ctxStatic.SetSourceRGBA(baseColor[0] * (1 - tint) + targetRed * tint, baseColor[1] * (1 - tint) + targetGreen * tint, baseColor[2] * (1 - tint) + targetBlue * tint, baseColor[3]);
		}

		RoundRectangle(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, GuiStyle.ElementBGRadius);

		ctxStatic.Fill();

		EmbossRoundRectangleElement(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, inverse: false, depth: 2, radius: (int)GuiStyle.ElementBGRadius);
	}
}
