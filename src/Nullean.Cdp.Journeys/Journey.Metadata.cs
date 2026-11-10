using System.Text.RegularExpressions;

namespace Nullean.Cdp.Journeys;

/// <summary>What a run knows about the site under test, to decide whether a journey applies to it.</summary>
/// <param name="Features">The enabled feature flags, kebab-case (<c>navigation-preview</c>).</param>
/// <param name="BuildType">The build type, e.g. <c>isolated</c>, <c>assembler</c> or <c>codex</c>; <c>null</c> when unknown.</param>
/// <param name="Env">The environment's name, e.g. <c>prod</c> or <c>edge</c>; <c>null</c> when unknown.</param>
public sealed record JourneyContext(IReadOnlySet<string> Features, string? BuildType = null, string? Env = null)
{
	/// <summary>A context with no features and nothing known.</summary>
	public static JourneyContext Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// One condition of a <c>@requires</c> line: a feature flag (<c>flag</c>), or <c>build-type:a,b</c> or <c>env:a,b</c>
/// (any of the values), each optionally negated with <c>!</c>. Anything the context does not know about is not matched,
/// so a negated condition holds and a plain one does not.
/// </summary>
/// <param name="Kind"><c>feature</c>, <c>build-type</c> or <c>env</c>.</param>
/// <param name="Values">One flag name, or the values of a build type or an environment.</param>
/// <param name="Negated">Whether the condition is <c>!</c>.</param>
public sealed record JourneyRequirement(string Kind, IReadOnlyList<string> Values, bool Negated)
{
	/// <summary>The condition as written: <c>flag</c>, <c>!flag</c>, <c>build-type:assembler,codex</c>.</summary>
	public override string ToString() =>
		(Negated ? "!" : "") + (Kind == "feature" ? Values[0] : $"{Kind}:{string.Join(',', Values)}");

	/// <summary>Whether <paramref name="context"/> meets the condition.</summary>
	public bool IsMetBy(JourneyContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var matches = Kind switch
		{
			"feature" => context.Features.Contains(Values[0]),
			"build-type" => context.BuildType is { } build && Values.Contains(build, StringComparer.OrdinalIgnoreCase),
			_ => context.Env is { } env && Values.Contains(env, StringComparer.OrdinalIgnoreCase),
		};
		return matches != Negated;
	}
}

/// <summary>How a journey becomes an Elastic Synthetics monitor (<c>@monitor</c>).</summary>
/// <param name="Schedule">Minutes between runs, or <c>null</c> for the exporter's default.</param>
/// <param name="Locations">The locations to run from; empty for the exporter's default.</param>
public sealed record JourneyMonitor(int? Schedule, IReadOnlyList<string> Locations);

/// <summary>
/// What the header of a journey file says about the journey, as <c>@</c> directives in comments before the first step:
/// <code>
/// # @tags navigation smoke
/// # @requires navigation-preview !env:prod
/// # @monitor schedule=10 locations=germany,us_east
/// # @alert pagerduty
/// # @mutates
/// </code>
/// A journey without directives has empty metadata and runs exactly as before.
/// </summary>
public sealed record JourneyMetadata
{
	/// <summary>No directives.</summary>
	public static JourneyMetadata None { get; } = new();

	/// <summary>The <c>@tags</c>, for selecting journeys.</summary>
	public IReadOnlyList<string> Tags { get; init; } = [];

	/// <summary>The <c>@requires</c> conditions, all of which must hold for the journey to apply.</summary>
	public IReadOnlyList<JourneyRequirement> Requires { get; init; } = [];

	/// <summary>The <c>@monitor</c> line: this journey is exported as a monitor. <c>null</c> without one.</summary>
	public JourneyMonitor? Monitor { get; init; }

	/// <summary>The <c>@alert</c> channels, e.g. <c>pagerduty</c>. What a channel does is up to the exporter.</summary>
	public IReadOnlyList<string> Alerts { get; init; } = [];

	/// <summary>Whether the journey is marked <c>@mutates</c>: it changes data on the site, so a live run skips it.</summary>
	public bool Mutates { get; init; }

	/// <summary>Whether there are no directives.</summary>
	public bool IsEmpty => this == None || (Tags.Count == 0 && Requires.Count == 0 && Monitor is null && Alerts.Count == 0 && !Mutates);

	/// <summary>The first <c>@requires</c> condition <paramref name="context"/> does not meet, or <c>null</c> when it meets all.</summary>
	public JourneyRequirement? Unmet(JourneyContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return Requires.FirstOrDefault(r => !r.IsMetBy(context));
	}
}

public sealed partial class Journey
{
	/// <summary>What the header's <c>@</c> directives say; <see cref="JourneyMetadata.None"/> without any.</summary>
	public JourneyMetadata Metadata { get; private init; } = JourneyMetadata.None;

	[GeneratedRegex(@"^#\s*@(?<name>\S*)\s*(?<rest>.*)$")]
	private static partial Regex Directive();

	[GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$")]
	private static partial Regex TagName();

	[GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
	private static partial Regex FeatureName();

	[GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$")]
	private static partial Regex ContextValue();

	private const string Directives = "@tags, @requires, @monitor, @alert or @mutates";

	// Collects the header's directives, line by line; errors are reported with the line.
	private sealed class MetadataBuilder
	{
		private readonly List<string> _tags = [];
		private readonly List<JourneyRequirement> _requires = [];
		private readonly List<string> _alerts = [];
		private JourneyMonitor? _monitor;
		private bool _mutates;
		private bool _any;

		public bool Add(string line, int number, bool afterFirstStep, List<JourneyError> errors)
		{
			var match = Directive().Match(line.Trim());
			if (!match.Success)
				return false;
			var name = match.Groups["name"].Value;
			var words = match.Groups["rest"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
			if (afterFirstStep)
			{
				errors.Add(new JourneyError(number, $"@{name} belongs in the header: put the directive before the first step"));
				return true;
			}

			_any = true;
			try
			{
				Apply(name, words);
			}
			catch (JourneySyntaxException e)
			{
				errors.Add(new JourneyError(number, e.Message));
			}

			return true;
		}

		private void Apply(string name, string[] words)
		{
			switch (name)
			{
				case "tags":
					NeedsWords(name, words, "@tags <name...>, e.g. @tags navigation smoke");
					foreach (var tag in words)
						_tags.Add(TagName().IsMatch(tag) ? tag : throw new JourneySyntaxException($"'{tag}' is not a tag name: use lower case letters, digits, '.', '_' or '-'"));
					break;
				case "requires":
					NeedsWords(name, words, "@requires <condition...>, e.g. @requires navigation-preview !env:prod build-type:assembler,codex");
					_requires.AddRange(words.Select(ParseRequirement));
					break;
				case "monitor":
					_monitor = _monitor is null ? ParseMonitor(words) : throw new JourneySyntaxException("@monitor may appear once");
					break;
				case "alert":
					NeedsWords(name, words, "@alert <channel...>, e.g. @alert pagerduty");
					foreach (var channel in words)
						_alerts.Add(FeatureName().IsMatch(channel) ? channel : throw new JourneySyntaxException($"'{channel}' is not an alert channel: use lower case letters, digits and '-'"));
					break;
				case "mutates":
					_mutates = words.Length == 0 ? true : throw new JourneySyntaxException("usage: @mutates, with nothing after it");
					break;
				default:
					throw new JourneySyntaxException($"unknown directive '@{name}': use {Directives}");
			}
		}

		private static void NeedsWords(string name, string[] words, string usage)
		{
			if (words.Length == 0)
				throw new JourneySyntaxException($"@{name} needs a value: {usage}");
		}

		public JourneyMetadata Build() => !_any ? JourneyMetadata.None : new JourneyMetadata
		{
			Tags = [.. _tags.Distinct(StringComparer.Ordinal)],
			Requires = _requires,
			Monitor = _monitor,
			Alerts = [.. _alerts.Distinct(StringComparer.Ordinal)],
			Mutates = _mutates,
		};
	}

	private static JourneyRequirement ParseRequirement(string text)
	{
		var negated = text.StartsWith('!');
		var body = negated ? text[1..] : text;
		var colon = body.IndexOf(':', StringComparison.Ordinal);
		if (colon < 0)
			return FeatureName().IsMatch(body)
				? new JourneyRequirement("feature", [body], negated)
				: throw new JourneySyntaxException($"'{text}' is not a condition: use a feature flag (navigation-preview), build-type:<a,b> or env:<a,b>, each optionally with a leading '!'");
		var kind = body[..colon];
		var values = body[(colon + 1)..].Split(',');
		if (kind is not ("build-type" or "env"))
			throw new JourneySyntaxException($"'{text}' is not a condition: '{kind}:' is not one of build-type: or env:");
		return values.All(v => ContextValue().IsMatch(v))
			? new JourneyRequirement(kind, values, negated)
			: throw new JourneySyntaxException($"'{text}' needs one or more values of lower case letters, digits, '_' or '-', separated by commas");
	}

	private static JourneyMonitor ParseMonitor(string[] words)
	{
		int? schedule = null;
		var locations = new List<string>();
		foreach (var word in words)
		{
			var equals = word.IndexOf('=', StringComparison.Ordinal);
			var (key, value) = equals < 0 ? (word, "") : (word[..equals], word[(equals + 1)..]);
			switch (key)
			{
				case "schedule":
					schedule = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes > 0
						? minutes
						: throw new JourneySyntaxException($"'{word}' needs a number of minutes: schedule=10");
					break;
				case "locations":
					locations.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
					if (locations.Count == 0 || locations.Any(l => !ContextValue().IsMatch(l)))
						throw new JourneySyntaxException($"'{word}' needs location names separated by commas: locations=germany,us_east");
					break;
				default:
					throw new JourneySyntaxException($"unknown @monitor option '{word}': use schedule=<minutes> or locations=<a,b>");
			}
		}

		return new JourneyMonitor(schedule, locations);
	}
}
