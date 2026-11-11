using System.Collections.Frozen;

namespace Nullean.Cdp;

/// <summary>A device to emulate: its viewport in CSS pixels, pixel ratio, touch, and user agent.</summary>
public sealed record Device(string Name, int Width, int Height, double Scale, bool Mobile, string UserAgent);

/// <summary>
/// A few common devices for <see cref="Page.SetDeviceAsync"/>, named as agent-browser (Playwright) names them. The
/// sizes and user agents are representative, not a full device catalogue.
/// </summary>
public static class Devices
{
	private const string IPhone =
		"Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

	private const string Android = "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

	private const string IPad =
		"Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

	/// <summary>Every known device, by name, ignoring case.</summary>
	public static FrozenDictionary<string, Device> All { get; } = new Device[]
	{
		new("iPhone 12", 390, 844, 3, true, IPhone),
		new("iPhone 13", 390, 844, 3, true, IPhone),
		new("iPhone 14", 390, 844, 3, true, IPhone),
		new("iPhone 15", 393, 852, 3, true, IPhone),
		new("iPhone 15 Pro Max", 430, 932, 3, true, IPhone),
		new("iPhone SE", 375, 667, 2, true, IPhone),
		new("Pixel 5", 393, 851, 2.75, true, Android),
		new("Pixel 7", 412, 915, 2.625, true, Android),
		new("Galaxy S9+", 320, 658, 4.5, true, Android),
		new("iPad Mini", 768, 1024, 2, true, IPad),
		new("iPad Pro 11", 834, 1194, 2, true, IPad),
		new("Desktop Chrome", 1280, 720, 1, false, ""),
	}.ToFrozenDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
}
