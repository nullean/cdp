namespace Nullean.Cdp;

/// <summary>Assertions about the page itself: its title and URL.</summary>
public static class PageExpectations
{
	/// <summary>
	/// Checks a <see cref="MatcherKind.Title"/> or <see cref="MatcherKind.Url"/> matcher against the page, again and again
	/// until it holds or the timeout passes, and returns what happened without throwing.
	/// </summary>
	public static Task<ExpectationResult> CheckAsync(
		this Page page,
		Matcher matcher,
		ExpectOptions? options = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(page);
		ArgumentNullException.ThrowIfNull(matcher);
		if (!matcher.IsPageLevel)
			throw new ArgumentException($"{matcher.Kind} is about an element, not the page: use Locator.CheckAsync.", nameof(matcher));
		options ??= new ExpectOptions();
		var isTitle = matcher.Kind == MatcherKind.Title;
		return Expectations.RunAsync(
			matcher,
			options,
			page.ActionTimeout,
			isTitle ? "the page title" : "the page URL",
			async ct => new Observation(true, Text: isTitle ? await page.TitleAsync(ct).ConfigureAwait(false) : await page.UrlAsync(ct).ConfigureAwait(false)),
			cancellationToken
		);
	}

	/// <summary>Like <see cref="CheckAsync(Page, Matcher, ExpectOptions?, CancellationToken)"/>, but throws an <see cref="ExpectationException"/> when the assertion does not hold.</summary>
	public static async Task ExpectAsync(this Page page, Matcher matcher, ExpectOptions? options = null, CancellationToken cancellationToken = default)
	{
		var result = await page.CheckAsync(matcher, options, cancellationToken).ConfigureAwait(false);
		if (!result.Passed)
			throw new ExpectationException(result);
	}
}
