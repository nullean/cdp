namespace Nullean.Cdp;

/// <summary>PNG screenshots of a page: the viewport, the whole page, or one element (<see cref="Locator.ScreenshotAsync"/>).</summary>
public static class PageScreenshots
{
	/// <summary>A PNG of the viewport, or of the whole page, past the viewport, with <paramref name="fullPage"/>.</summary>
	public static async Task<byte[]> ScreenshotAsync(this Page page, bool fullPage = false, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		if (!fullPage)
			return await CaptureAsync(page, null, cancellationToken).ConfigureAwait(false);
		var metrics = await page.SendAsync("Page.getLayoutMetrics", null, cancellationToken).ConfigureAwait(false);
		var size = metrics.GetProperty("cssContentSize");
		return await CaptureAsync(
			page,
			(0, 0, size.GetProperty("width").GetDouble(), size.GetProperty("height").GetDouble()),
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>Captures a PNG; a clip is in CSS pixels relative to the document, not the viewport.</summary>
	internal static async Task<byte[]> CaptureAsync(Page page, (double X, double Y, double Width, double Height)? clip, CancellationToken cancellationToken)
	{
		var result = await page.SendAsync(
			"Page.captureScreenshot",
			w =>
			{
				w.WriteString("format", "png");
				if (clip is not { } c)
					return;
				w.WriteBoolean("captureBeyondViewport", true);
				w.WriteStartObject("clip");
				w.WriteNumber("x", c.X);
				w.WriteNumber("y", c.Y);
				w.WriteNumber("width", c.Width);
				w.WriteNumber("height", c.Height);
				w.WriteNumber("scale", 1);
				w.WriteEndObject();
			},
			cancellationToken
		).ConfigureAwait(false);
		return Convert.FromBase64String(result.GetProperty("data").GetString() ?? string.Empty);
	}
}
