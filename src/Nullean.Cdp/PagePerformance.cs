using System.Globalization;
using System.Text.Json;

namespace Nullean.Cdp;

/// <summary>
/// Paint and load timing of the current document, in milliseconds from the start of its navigation; <c>null</c> when
/// the page has not reached that point (or the browser does not report it).
/// </summary>
public sealed record PageTimings(
	double? TimeToFirstByte,
	double? FirstPaint,
	double? FirstContentfulPaint,
	double? DomInteractive,
	double? DomContentLoaded,
	double? Load
)
{
	/// <summary>Each timing by the name journeys use: <c>ttfb</c>, <c>first-paint</c>, <c>fcp</c>, <c>dom-interactive</c>, <c>dom-content-loaded</c>, <c>load</c>.</summary>
	public IReadOnlyList<(string Name, double? Value)> Named =>
		[
			("ttfb", TimeToFirstByte),
			("first-paint", FirstPaint),
			("fcp", FirstContentfulPaint),
			("dom-interactive", DomInteractive),
			("dom-content-loaded", DomContentLoaded),
			("load", Load),
		];
}

/// <summary>
/// Core Web Vitals of the current document, as agent-browser's <c>vitals</c> names them. Times in milliseconds; CLS is
/// unitless. <c>null</c> when there is nothing to measure yet (INP needs an interaction).
/// </summary>
public sealed record WebVitals(
	double? TimeToFirstByte,
	double? FirstContentfulPaint,
	double? LargestContentfulPaint,
	double CumulativeLayoutShift,
	double? InteractionToNextPaint,
	string? LargestContentfulPaintElement
)
{
	/// <summary>Each vital by name: <c>ttfb</c>, <c>fcp</c>, <c>lcp</c>, <c>cls</c>, <c>inp</c>.</summary>
	public IReadOnlyList<(string Name, double? Value)> Named =>
		[
			("ttfb", TimeToFirstByte),
			("fcp", FirstContentfulPaint),
			("lcp", LargestContentfulPaint),
			("cls", CumulativeLayoutShift),
			("inp", InteractionToNextPaint),
		];
}

/// <summary>Performance readings of a page: timings, Core Web Vitals and Chrome's runtime counters.</summary>
public static class PagePerformance
{
	// Paint entries are recorded once the frame is presented, which on a GPU-less Linux runner can be after load. Only
	// when the page has loaded but not yet reported a paint: let two frames pass, and give it up to 2 s more.
	private const string TimingsScript =
		"""
		(async () => {
			const unpainted = () => document.readyState === 'complete' && !performance.getEntriesByType('paint').length;
			if (unpainted())
				await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 0))));
			// A busy machine can be slow to paint; this costs nothing once the page has painted.
			for (let i = 0; i < 80 && unpainted(); i++)
				await new Promise(r => setTimeout(r, 25));
			const nav = performance.getEntriesByType('navigation')[0];
			const paint = name => performance.getEntriesByType('paint').find(e => e.name === name)?.startTime ?? null;
			const at = v => (nav && v > 0) ? v : null;
			return [at(nav?.responseStart), paint('first-paint'), paint('first-contentful-paint'),
				at(nav?.domInteractive), at(nav?.domContentLoadedEventEnd), at(nav?.loadEventEnd)];
		})()
		""";

	// Buffered observers hand over every entry so far. CLS uses session windows (gaps under 1 s, at most 5 s long);
	// INP is the slowest interaction (the 98th percentile once there are 50 or more).
	private const string VitalsScript =
		"""
		(async () => {
			// An interaction is reported after the frame it caused is painted: let two frames pass, and give one that
			// happened just now up to 200 ms to arrive.
			await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 0))));
			const recent = performance.getEntriesByType('event').length || document.querySelector(':focus');
			for (let i = 0; i < 8 && recent && !(window.__nulleanCdpInteractions?.size); i++)
				await new Promise(r => setTimeout(r, 25));
			const take = type => new Promise(resolve => {
				const seen = [];
				try {
					const observer = new PerformanceObserver(list => seen.push(...list.getEntries()));
					observer.observe({ type, buffered: true, ...(type === 'event' ? { durationThreshold: 16 } : {}) });
					setTimeout(() => { seen.push(...observer.takeRecords()); observer.disconnect(); resolve(seen); }, 0);
				} catch { resolve([]); }
			});
			const [lcps, shifts, events] = await Promise.all([take('largest-contentful-paint'), take('layout-shift'), take('event')]);
			const nav = performance.getEntriesByType('navigation')[0];
			const fcp = performance.getEntriesByType('paint').find(e => e.name === 'first-contentful-paint')?.startTime ?? null;
			const lcp = lcps.length ? lcps[lcps.length - 1] : null;
			let cls = 0, session = 0, first = 0, last = 0;
			for (const s of shifts.filter(s => !s.hadRecentInput)) {
				if (session && s.startTime - last < 1000 && s.startTime - first < 5000) session += s.value;
				else { session = s.value; first = s.startTime; }
				last = s.startTime;
				cls = Math.max(cls, session);
			}
			const byInteraction = new Map(window.__nulleanCdpInteractions ?? []);
			for (const e of events.filter(e => e.interactionId))
				byInteraction.set(e.interactionId, Math.max(byInteraction.get(e.interactionId) ?? 0, e.duration));
			const durations = [...byInteraction.values()].sort((a, b) => b - a);
			const inp = durations.length ? durations[Math.min(durations.length - 1, Math.floor(durations.length / 50))] : null;
			const element = lcp?.element ? (lcp.element.id ? lcp.element.tagName.toLowerCase() + '#' + lcp.element.id : lcp.element.tagName.toLowerCase()) : null;
			return [nav && nav.responseStart > 0 ? nav.responseStart : null, fcp, lcp ? lcp.startTime : null, cls, inp, element];
		})()
		""";

	/// <summary>
	/// Runs before any page script in every document: Chrome buffers only interactions of about 104 ms or more, so
	/// shorter ones (down to 16 ms, which INP counts) are kept from the start, by interaction.
	/// </summary>
	internal const string InteractionRecorder =
		"""
		(() => {
			const slowest = new Map();
			Object.defineProperty(window, '__nulleanCdpInteractions', { value: slowest });
			try {
				new PerformanceObserver(list => {
					for (const e of list.getEntries())
						if (e.interactionId) slowest.set(e.interactionId, Math.max(slowest.get(e.interactionId) ?? 0, e.duration));
				}).observe({ type: 'event', buffered: true, durationThreshold: 16 });
			} catch { }
		})();
		""";

	/// <summary>
	/// Records interactions from the start of every document the page loads from now on, so <see cref="VitalsAsync"/>
	/// can report INP for interactions shorter than the ~104 ms Chrome keeps by itself. Opt-in: nothing runs on pages
	/// that never ask for vitals. Call it before navigating.
	/// </summary>
	public static async Task RecordInteractionsAsync(this Page page, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		if (Interlocked.Exchange(ref page.RecordingInteractions, 1) == 1)
			return;
		await page.SendAsync("Page.addScriptToEvaluateOnNewDocument", w => w.WriteString("source", InteractionRecorder), cancellationToken).ConfigureAwait(
			false
		);
	}

	/// <summary>The current document's paint and load timings.</summary>
	public static async Task<PageTimings> TimingsAsync(this Page page, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		var t = await page.EvaluateAsync(TimingsScript, cancellationToken).ConfigureAwait(false);
		return new PageTimings(Number(t[0]), Number(t[1]), Number(t[2]), Number(t[3]), Number(t[4]), Number(t[5]));
	}

	/// <summary>
	/// The current document's Core Web Vitals, from the entries the page has reported so far. INP counts interactions
	/// of 16 ms or more only after <see cref="RecordInteractionsAsync"/>; without it, only those Chrome keeps itself.
	/// </summary>
	public static async Task<WebVitals> VitalsAsync(this Page page, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		var v = await page.EvaluateAsync(VitalsScript, cancellationToken).ConfigureAwait(false);
		return new WebVitals(
			Number(v[0]),
			Number(v[1]),
			Number(v[2]),
			Number(v[3]) ?? 0,
			Number(v[4]),
			v[5].ValueKind == JsonValueKind.String ? v[5].GetString() : null
		);
	}

	/// <summary>
	/// Chrome's runtime counters for the page (<c>Performance.getMetrics</c>): <c>Nodes</c>, <c>JSEventListeners</c>,
	/// <c>LayoutCount</c>, <c>RecalcStyleCount</c>, <c>ScriptDuration</c>, <c>TaskDuration</c>, <c>JSHeapUsedSize</c>, …
	/// Durations are in seconds, sizes in bytes, as Chrome reports them.
	/// </summary>
	public static async Task<IReadOnlyDictionary<string, double>> MetricsAsync(this Page page, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		await page.SendAsync("Performance.enable", null, cancellationToken).ConfigureAwait(false);
		var result = await page.SendAsync("Performance.getMetrics", null, cancellationToken).ConfigureAwait(false);
		var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
		foreach (var metric in result.GetProperty("metrics").EnumerateArray())
			metrics[metric.GetProperty("name").GetString()!] = metric.GetProperty("value").GetDouble();
		return metrics;
	}

	private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? Math.Round(value.GetDouble(), 4) : null;

	/// <summary>A reading as journeys print it: milliseconds to one decimal, CLS to four, counters whole.</summary>
	public static string Format(string name, double? value) => value switch
	{
		null => "-",
		{ } cls when name == "cls" => cls.ToString("0.####", CultureInfo.InvariantCulture),
		{ } v when Math.Abs(v - Math.Round(v)) < 1e-9 => v.ToString("0", CultureInfo.InvariantCulture),
		{ } v => v.ToString("0.#", CultureInfo.InvariantCulture),
	};
}
