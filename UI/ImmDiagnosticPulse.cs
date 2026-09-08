#nullable enable

using System;
using Cairo;
using IntegratedModManager.Config;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace IntegratedModManager.UI;

public enum ImmDiagnosticLevel
{
	None,
	Warning,
	Error
}

public static class ImmDiagnosticPulse
{
	// Render-only animation: no timers, callbacks, or texture regeneration.
	private const double PeriodMilliseconds = 3600.0;

	public static double GetPhase(long elapsedMilliseconds) { double radians = elapsedMilliseconds / PeriodMilliseconds * Math.PI * 2.0; return 0.5 + Math.Sin(radians) * 0.5; }

	public static bool IsHighlightEnabled(ImmImportantInformationHighlight mode) { return mode != ImmImportantInformationHighlight.Disabled; }

	public static double GetHighlightPhase(ImmImportantInformationHighlight mode, long elapsedMilliseconds) { return mode == ImmImportantInformationHighlight.Pulsating ? GetPhase(elapsedMilliseconds) : 0.5; }

	public static double[] CreateFlatGuiColor(ImmDiagnosticLevel level)
	{
		double[] baseColor = GuiStyle.DialogDefaultBgColor;

		double targetRed = level == ImmDiagnosticLevel.Error ? 0.62 : 0.58;

		double targetGreen = level == ImmDiagnosticLevel.Error ? 0.12 : 0.46;

		double targetBlue = level == ImmDiagnosticLevel.Error ? 0.10 : 0.08;

		const double tint = 0.42;

		return new double[] { baseColor[0] * (1 - tint) + targetRed * tint, baseColor[1] * (1 - tint) + targetGreen * tint, baseColor[2] * (1 - tint) + targetBlue * tint, baseColor[3] };
	}

	public static Vec4f SetNeutralCardColor(bool hovered, Vec4f color) { return SetColor(color, 205, 205, 205, hovered ? 52 : 30); }

	public static LoadedTexture CreateSolidTexture(ICoreClientAPI api) { return api.Gui.Icons.GenTexture(1, 1, (ctx, surface) => { ctx.SetSourceRGBA(1, 1, 1, 1); ctx.Paint(); }); }

	public static Vec4f SetCardColor(ImmDiagnosticLevel level, double phase, bool hovered, Vec4f color)
	{
		double brightness = 0.78 + phase * 0.22 + (hovered ? 0.10 : 0);

		if (level == ImmDiagnosticLevel.Error) { return SetColor(color, 142 * brightness, 48 * brightness, 40 * brightness, hovered ? 205 : 185); }

		return SetColor(color, 154 * brightness, 119 * brightness, 37 * brightness, hovered ? 205 : 185);
	}

	public static Vec4f SetNudgeColor(ImmDiagnosticLevel level, double phase, Vec4f color)
	{
		if (level == ImmDiagnosticLevel.None) { return SetGuiColor(GuiStyle.DialogDefaultBgColor, color); }

		double brightness = 0.78 + phase * 0.22;

		if (level == ImmDiagnosticLevel.Error) { return SetColor(color, 150 * brightness, 48 * brightness, 40 * brightness, 225); }

		return SetColor(color, 164 * brightness, 126 * brightness, 38 * brightness, 225);
	}


	public static Vec4f SetUpdateOverlayColor(bool retracted, double phase, Vec4f color)
	{
		double alpha = 42 + phase * 48;
		return retracted ? SetColor(color, 205, 61, 52, alpha) : SetColor(color, 66, 188, 82, alpha);
	}

	public static Vec4f SetTabOverlayColor(ImmDiagnosticLevel level, double phase, Vec4f color)
	{
		double alpha = 48 + phase * 42;

		if (level == ImmDiagnosticLevel.Error) { return SetColor(color, 205, 61, 52, alpha); }

		return SetColor(color, 218, 166, 48, alpha);
	}

	public static Vec4f SetGuiColor(double[] rgba, Vec4f color) { return color.Set((float)rgba[0], (float)rgba[1], (float)rgba[2], (float)rgba[3]); }

	private static Vec4f SetColor(Vec4f color, double red, double green, double blue, double alpha) { return color.Set(Clamp01(red / 255.0), Clamp01(green / 255.0), Clamp01(blue / 255.0), Clamp01(alpha / 255.0)); }

	private static float Clamp01(double value) { return (float)Math.Clamp(value, 0, 1); }
}
