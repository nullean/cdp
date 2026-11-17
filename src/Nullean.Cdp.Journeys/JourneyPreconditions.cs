using System.Globalization;
using System.Text.RegularExpressions;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp.Journeys;

/// <summary>
/// Something a site must show before journeys run against it: a response header or an element's text or attribute on one
/// page, equal to a value. A deploy that has not landed, or whose cache has not cleared, still shows the old value; waiting for
/// the new one first turns that into a clear failure and keeps it from showing up as a failure of every journey.
/// </summary>
public sealed record JourneyPrecondition
{
	/// <summary>The page to open: an absolute URL, or a path that <see cref="JourneyOptions.BaseUrl"/> resolves.</summary>
	public required string Path { get; init; }

	/// <summary>A response header of the page's document. Set this, or <see cref="Selector"/>.</summary>
	public string? Header { get; init; }

	/// <summary>A CSS selector for an element, such as <c>meta[name='docs:build']</c>. Set this, or <see cref="Header"/>.</summary>
	public string? Selector { get; init; }

	/// <summary>With <see cref="Selector"/>: the attribute to read, such as <c>content</c>. The element's text when <c>null</c>.</summary>
	public string? Attribute { get; init; }

	/// <summary>The value to wait for.</summary>
	public required string Expected { get; init; }

	/// <summary>How <see cref="Expected"/> is compared with what the page shows.</summary>
	public TextMatch Match { get; init; } = TextMatch.Exact;

	/// <summary>How long to wait for the value in all. Default: 10 seconds.</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>How long to wait between looks. Default: half a second.</summary>
	public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// How many times a look that fails (the page did not load, the connection dropped) is tried again before the wait gives up. A look
	/// that works and shows another value is not a failure: the wait goes on until <see cref="Timeout"/>. Default: 2.
	/// </summary>
	public int Retries { get; init; } = 2;

	/// <summary>What the precondition looks at, in words: <c>the docs:build header of /docs</c>.</summary>
	public string Subject =>
		Header is not null
			? $"the {Header} response header of {Path}"
			: $"{(Attribute is null ? "the text" : $"the {Attribute} attribute")} of {Selector} on {Path}";
}

/// <summary>How waiting for a <see cref="JourneyPrecondition"/> ended.</summary>
/// <param name="Passed">The page showed the value.</param>
/// <param name="Message">Plain text: what matched, or what was expected and what was last seen.</param>
/// <param name="Attempts">How many looks it took.</param>
/// <param name="Elapsed">How long it waited.</param>
public sealed record PreconditionResult(bool Passed, string Message, int Attempts, TimeSpan Elapsed);

/// <summary>Waits for what a site must show before a run starts.</summary>
public static class JourneyPreconditions
{
	private const int MaxSeen = 200;
	private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Opens the page in a fresh context (so nothing is cached) until it shows the value of <paramref name="precondition"/>, the wait
	/// times out, or looks fail more often than <see cref="JourneyPrecondition.Retries"/> allows. The run's allow list, user agent,
	/// headers and read-only rule of <paramref name="options"/> apply, so a monitor stays identifiable and cannot write.
	/// </summary>
	public static async Task<PreconditionResult> WaitAsync(
		Browser browser,
		JourneyPrecondition precondition,
		JourneyOptions? options = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(browser);
		ArgumentNullException.ThrowIfNull(precondition);
		options ??= new JourneyOptions();
		Validate(precondition);
		var url = Resolve(precondition.Path, options);
		var allowList = options.AllowList([]);
		if (allowList.Refusal(url) is { } refusal)
			return new PreconditionResult(false, $"Did not look at {url}: {refusal}.", 0, TimeSpan.Zero);

		var started = System.Diagnostics.Stopwatch.StartNew();
		var attempts = 0;
		var failures = 0;
		string? seen;
		while (true)
		{
			attempts++;
			try
			{
				seen = await LookAsync(browser, precondition, url, allowList, options, Remaining(precondition, started), cancellationToken).ConfigureAwait(
					false
				);
				if (seen is not null && Matches(precondition, seen))
					return new PreconditionResult(true, $"{Capitalise(precondition.Subject)} is {Quote(seen)}.", attempts, started.Elapsed);
			}
			catch (Exception e) when (IsLookFailure(e, cancellationToken))
			{
				failures++;
				seen = null;
				if (failures > precondition.Retries)
					return new PreconditionResult(
						false,
						$"Could not look at {url} for {Describe(precondition)}: {failures} looks failed, the last with: {e.Message}",
						attempts,
						started.Elapsed
					);
			}

			if (started.Elapsed + precondition.Interval >= precondition.Timeout)
				return new PreconditionResult(false, Timeout(precondition, seen, attempts, started.Elapsed), attempts, started.Elapsed);
			await Task.Delay(precondition.Interval, cancellationToken).ConfigureAwait(false);
		}
	}

	private static TimeSpan Remaining(JourneyPrecondition precondition, System.Diagnostics.Stopwatch started)
	{
		var left = precondition.Timeout - started.Elapsed;
		return left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1);
	}

	// What the page shows, or null when it shows nothing to compare: no such header, no such element.
	private static async Task<string?> LookAsync(
		Browser browser,
		JourneyPrecondition precondition,
		Uri url,
		JourneyAllowList allowList,
		JourneyOptions options,
		TimeSpan budget,
		CancellationToken cancellationToken
	)
	{
		await using var context = await browser.NewContextAsync(cancellationToken).ConfigureAwait(false);
		var page = await context.NewPageAsync(cancellationToken).ConfigureAwait(false);
		page.NavigationTimeout = options.NavigationTimeout is { } navigation && navigation < budget ? navigation : budget;
		await using var guard = options.GuardNetwork || options.ReadOnly
			? await page.GuardNetworkAsync(options.GuardNetwork ? allowList.Allows : _ => true, options.GuardOptions(), cancellationToken).ConfigureAwait(false)
			: null;
		await JourneyRunner.ApplyDefaultsAsync(page, options, cancellationToken).ConfigureAwait(false);
		await page.GotoAsync(url.AbsoluteUri, cancellationToken).ConfigureAwait(false);
		if (precondition.Header is { } header)
			return page.Response?.Header(header);
		var element = page.Css(precondition.Selector!);
		if (await element.CountAsync(cancellationToken).ConfigureAwait(false) == 0)
			return null;
		return precondition.Attribute is { } attribute
			? await element.First.AttributeAsync(attribute, cancellationToken).ConfigureAwait(false)
			: await element.First.TextAsync(cancellationToken).ConfigureAwait(false);
	}

	private static bool IsLookFailure(Exception e, CancellationToken cancellationToken) =>
		e is TimeoutException or PageNavigationException or CdpException or CdpDisconnectedException ||
			(e is OperationCanceledException && !cancellationToken.IsCancellationRequested);

	private static bool Matches(JourneyPrecondition precondition, string seen) => precondition.Match switch
	{
		TextMatch.Contains => seen.Contains(precondition.Expected, StringComparison.Ordinal),
		TextMatch.Regex => Regex.IsMatch(seen, precondition.Expected, RegexOptions.CultureInvariant, RegexTimeout),
		_ => string.Equals(seen, precondition.Expected, StringComparison.Ordinal),
	};

	private static void Validate(JourneyPrecondition p)
	{
		if ((p.Header is null) == (p.Selector is null))
			throw new ArgumentException(
				"A precondition looks at a response header or at an element: set Header or Selector, not both and not neither.",
				nameof(p)
			);
		if (p.Header is null && p.Attribute is not null && p.Attribute.Length == 0)
			throw new ArgumentException("The attribute name is empty.", nameof(p));
		if (p.Timeout <= TimeSpan.Zero || p.Interval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(p), "The timeout and the interval are longer than zero.");
		if (p.Retries < 0)
			throw new ArgumentOutOfRangeException(nameof(p), "Retries are zero or more.");
		if (p.Match == TextMatch.Regex)
		{
			try
			{
				_ = new Regex(p.Expected, RegexOptions.CultureInvariant, RegexTimeout);
			}
			catch (ArgumentException e)
			{
				throw new ArgumentException($"'{p.Expected}' is not a valid regular expression: {e.Message}", nameof(p), e);
			}
		}
	}

	private static Uri Resolve(string path, JourneyOptions options) =>
		Uri.TryCreate(options.BaseUrl, path, out var url) && url.IsAbsoluteUri
			? url
			: throw new ArgumentException($"'{path}' is not a URL, and the options have no base URL to resolve it against.", nameof(path));

	private static string Timeout(JourneyPrecondition p, string? seen, int attempts, TimeSpan elapsed) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"Timed out after {elapsed.TotalSeconds:0.#}s waiting for {p.Subject} to {Describe(p)}: last saw {(seen is null ? "nothing" : Quote(seen))} in {attempts} {(attempts == 1 ? "look" : "looks")}."
		);

	private static string Describe(JourneyPrecondition p) => p.Match switch
	{
		TextMatch.Contains => $"contain {Quote(p.Expected)}",
		TextMatch.Regex => $"match {Quote(p.Expected)}",
		_ => $"equal {Quote(p.Expected)}",
	};

	private static string Quote(string value)
	{
		var flat = value.ReplaceLineEndings(" ");
		return $"\"{(flat.Length > MaxSeen ? flat[..MaxSeen] + "..." : flat)}\"";
	}

	private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
