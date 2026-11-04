using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nullean.Cdp.Journeys;

/// <summary>
/// Which URLs a journey may reach. Hosts come from the journey's <c>allow</c> lines, the host's own additions and the
/// base URL's host; <c>file://</c> only under one folder the host chose (the base URL's folder when it is a file URL).
/// Everything else is refused.
/// </summary>
public sealed partial class JourneyAllowList
{
	private readonly string[] _patterns;
	private readonly string? _fileRoot;

	/// <summary>An allow list of host patterns and an optional folder for <c>file://</c> URLs.</summary>
	/// <param name="patterns"><c>example.com</c>, <c>*.example.com</c> (which also allows <c>example.com</c>), or <c>*</c>.</param>
	/// <param name="fileRoot">The only folder file URLs may point into, or <c>null</c> for none.</param>
	public JourneyAllowList(IEnumerable<string> patterns, string? fileRoot)
	{
		_patterns = [.. patterns.Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
		_fileRoot = fileRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(fileRoot)) + Path.DirectorySeparatorChar;
	}

	/// <summary>The host patterns, lower case.</summary>
	public IReadOnlyList<string> Patterns => _patterns;

	/// <summary>Whether <paramref name="pattern"/> is a valid <c>allow</c> pattern.</summary>
	public static bool IsValidPattern(string pattern) => Pattern().IsMatch(pattern);

	[GeneratedRegex(@"^(\*|(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*)$")]
	private static partial Regex Pattern();

	/// <summary>Whether a request to <paramref name="uri"/> is allowed.</summary>
	public bool Allows(Uri uri) => Refusal(uri) is null;

	/// <summary>Why <paramref name="uri"/> is refused, or <c>null</c> when it is allowed.</summary>
	public string? Refusal(Uri uri)
	{
		ArgumentNullException.ThrowIfNull(uri);
		switch (uri.Scheme)
		{
			case "http" or "https" or "ws" or "wss":
				var host = uri.IdnHost.ToLowerInvariant();
				foreach (var pattern in _patterns)
				{
					if (pattern == "*" || pattern == host
						|| (pattern.StartsWith("*.", StringComparison.Ordinal) && (host == pattern[2..] || host.EndsWith(pattern[1..], StringComparison.Ordinal))))
						return null;
				}

				return $"{host} is not in the journey's allow list (add: allow {host})";
			case "file":
				if (_fileRoot is null)
					return "file URLs are not allowed";
				var path = Path.GetFullPath(uri.LocalPath);
				var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
				return path.StartsWith(_fileRoot, comparison) ? null : $"file URLs are only allowed under {_fileRoot}";
			default:
				return $"{uri.Scheme} URLs are not allowed";
		}
	}
}

/// <summary>
/// Secrets in journeys: a value written <c>$env:NAME</c> is read from the environment (or the host's resolver) when
/// the step runs, and every resolved value is replaced by its reference again in whatever the run reports.
/// </summary>
public sealed partial class JourneySecrets
{
	/// <summary>Secret values shorter than this are refused: redacting them would mangle ordinary text.</summary>
	public const int MinimumLength = 4;

	private readonly Func<string, string?> _resolve;
	private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

	internal JourneySecrets(Func<string, string?> resolve) => _resolve = resolve;

	[GeneratedRegex(@"^\$env:([A-Za-z_][A-Za-z0-9_]*)$")]
	private static partial Regex Reference();

	/// <summary>Whether <paramref name="value"/> is a secret reference (<c>$env:NAME</c>).</summary>
	public static bool IsReference(string value) => Reference().IsMatch(value);

	/// <summary>Whether <paramref name="value"/> looks like a reference but is not a valid one.</summary>
	internal static bool IsMalformedReference(string value) => value.StartsWith("$env:", StringComparison.Ordinal) && !IsReference(value);

	/// <summary>The value to type: a reference resolved, anything else as written.</summary>
	internal string Resolve(string value)
	{
		var match = Reference().Match(value);
		if (!match.Success)
			return value;
		var name = match.Groups[1].Value;
		var secret = _resolve(name);
		if (string.IsNullOrEmpty(secret))
			throw new ArgumentException($"The secret {name} is not set.");
		if (secret.Length < MinimumLength)
			throw new ArgumentException($"The secret {name} is shorter than {MinimumLength} characters, too short to redact safely.");
		_values[secret] = value;
		return secret;
	}

	/// <summary><paramref name="text"/> with every resolved secret replaced by its reference.</summary>
	public string Redact(string text)
	{
		foreach (var (secret, reference) in _values.OrderByDescending(v => v.Key.Length))
			text = text.Replace(secret, reference, StringComparison.Ordinal);
		return text;
	}
}

/// <summary>
/// Marks text that came from a web page before an agent reads it: a header and footer carrying a random nonce the page
/// cannot know, so it cannot fake the end of its own content. Lines inside that imitate a marker are escaped. This
/// labels the content; it does not make it safe to follow.
/// </summary>
public static class PageContent
{
	/// <summary>The start of the opening marker.</summary>
	public const string Begin = "--- PAGE_CONTENT";

	/// <summary>The start of the closing marker.</summary>
	public const string End = "--- END_PAGE_CONTENT";

	/// <summary>A new 128-bit nonce, as lower-case hex.</summary>
	public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	/// <summary>
	/// Wraps <paramref name="content"/> in markers carrying <paramref name="nonce"/>, cut to
	/// <paramref name="maxCharacters"/> with a note when it is longer.
	/// </summary>
	public static string Fence(string content, string nonce, string? origin = null, int? maxCharacters = null)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentException.ThrowIfNullOrEmpty(nonce);
		var total = content.Length;
		var truncated = maxCharacters is { } max && total > max;
		if (truncated)
			content = content[..maxCharacters!.Value];

		var text = new StringBuilder();
		text.Append(Begin).Append(" nonce=").Append(nonce);
		if (origin is not null)
			text.Append(" origin=").Append(origin);
		text.Append(" ---\n");
		foreach (var line in content.Split('\n'))
		{
			var trimmed = line.TrimStart();
			// A page cannot know the nonce, but a look-alike marker could still mislead a reader; escape it.
			if (trimmed.StartsWith(Begin, StringComparison.Ordinal) || trimmed.StartsWith(End, StringComparison.Ordinal))
				text.Append('\\');
			text.Append(line).Append('\n');
		}

		if (truncated)
			text.Append("[truncated: showing ").Append(maxCharacters).Append(" of ").Append(total).Append(" characters]\n");
		return text.Append(End).Append(" nonce=").Append(nonce).Append(" ---\n").ToString();
	}
}
