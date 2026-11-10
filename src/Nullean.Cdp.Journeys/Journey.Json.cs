using System.Buffers;
using System.Text.Json;

namespace Nullean.Cdp.Journeys;

public sealed partial class Journey
{
	/// <summary>The version of the JSON <see cref="ToJson"/> writes. It changes when a reader of that JSON would break.</summary>
	public const int JsonVersion = 1;

	/// <summary>
	/// The parsed journey as JSON, for runners outside .NET: one object per step with its line, its arguments and its
	/// typed command (<c>type</c>, then the command's own fields, empty ones left out). Secret references stay
	/// references (<c>$env:NAME</c>), never values.
	/// </summary>
	public string ToJson()
	{
		var buffer = new ArrayBufferWriter<byte>();
		// The relaxed encoder writes quotes as \" rather than "; the output is for people and runners, never HTML.
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteNumber("version", JsonVersion);
			json.WriteString("source", Source);
			json.WriteStartArray("allow");
			foreach (var host in AllowedHosts)
				json.WriteStringValue(host);
			json.WriteEndArray();
			json.WriteStartArray("steps");
			foreach (var step in Steps)
			{
				json.WriteStartObject();
				json.WriteNumber("line", step.Line);
				json.WriteStartArray("args");
				foreach (var argument in step.Arguments)
					json.WriteStringValue(argument);
				json.WriteEndArray();
				json.WriteStartObject("command");
				WriteCommand(json, step.Command);
				json.WriteEndObject();
				json.WriteEndObject();
			}

			json.WriteEndArray();
			json.WriteEndObject();
		}

		return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
	}

	private static void WriteCommand(Utf8JsonWriter json, JourneyCommand command)
	{
		switch (command)
		{
			case OpenCommand c:
				json.WriteString("type", "open");
				json.WriteString("url", c.Url);
				break;
			case HistoryCommand c:
				json.WriteString("type", "history");
				json.WriteString("direction", c.Direction);
				break;
			case ActCommand c:
				json.WriteString("type", "act");
				json.WriteString("action", c.Action);
				WriteTarget(json, "target", c.Target);
				WriteStrings(json, "values", c.Values);
				break;
			case PressCommand c:
				json.WriteString("type", "press");
				json.WriteString("keys", c.Keys);
				break;
			case WaitCommand c:
				json.WriteString("type", "wait");
				if (c.Target is { } waitTarget)
					WriteTarget(json, "target", waitTarget);
				if (c.Milliseconds is { } ms)
					json.WriteNumber("milliseconds", ms);
				WriteOptional(json, "url", c.Url);
				WriteOptional(json, "loadState", c.LoadState);
				break;
			case SetCommand c:
				json.WriteString("type", "set");
				json.WriteString("setting", c.Setting);
				if (c.Width != 0)
					json.WriteNumber("width", c.Width);
				if (c.Height != 0)
					json.WriteNumber("height", c.Height);
				WriteOptional(json, "scheme", c.Scheme);
				if (c.Scale != 1)
					json.WriteNumber("scale", c.Scale);
				WriteOptional(json, "device", c.Device);
				if (c.Setting == "geo")
				{
					json.WriteNumber("latitude", c.Position.Latitude);
					json.WriteNumber("longitude", c.Position.Longitude);
				}

				if (c.Offline is { } offline)
					json.WriteBoolean("offline", offline);
				WriteOptional(json, "headers", c.Headers);
				WriteOptional(json, "userAgent", c.UserAgent);
				WriteOptional(json, "user", c.User);
				WriteOptional(json, "password", c.Password);
				break;
			case PerformanceCommand c:
				json.WriteString("type", "performance");
				json.WriteString("kind", c.Kind);
				if (c.Budgets.Count > 0)
				{
					json.WriteStartArray("budgets");
					foreach (var budget in c.Budgets)
					{
						json.WriteStartObject();
						json.WriteString("name", budget.Name);
						json.WriteNumber("limit", budget.Limit);
						json.WriteEndObject();
					}

					json.WriteEndArray();
				}

				break;
			case HarCommand c:
				json.WriteString("type", "har");
				json.WriteString("action", c.Action);
				WriteOptional(json, "path", c.Path);
				break;
			case RequestsCommand c:
				json.WriteString("type", "requests");
				WriteFlag(json, "clear", c.Clear);
				WriteOptional(json, "filter", c.Filter);
				WriteStrings(json, "types", c.Types);
				WriteOptional(json, "method", c.Method);
				WriteOptional(json, "status", c.Status);
				WriteFlag(json, "failed", c.Failed);
				WriteFlag(json, "strict", c.Strict);
				break;
			case LinksCommand c:
				json.WriteString("type", "links");
				WriteOptional(json, "selector", c.Selector);
				WriteFlag(json, "external", c.External);
				WriteFlag(json, "all", c.All);
				WriteFlag(json, "strict", c.Strict);
				break;
			case ScreenshotCommand c:
				json.WriteString("type", "screenshot");
				WriteOptional(json, "selector", c.Selector);
				json.WriteString("path", c.Path);
				WriteFlag(json, "full", c.Full);
				break;
			case AuditCommand c:
				json.WriteString("type", "audit");
				WriteOptional(json, "selector", c.Selector);
				WriteFlag(json, "strict", c.Strict);
				break;
			case ClearLogCommand c:
				json.WriteString("type", "clearLog");
				json.WriteString("log", c.Log);
				break;
			case DragCommand c:
				json.WriteString("type", "drag");
				WriteTarget(json, "source", c.Source);
				WriteTarget(json, "target", c.Target);
				break;
			case ScrollCommand c:
				json.WriteString("type", "scroll");
				json.WriteString("direction", c.Direction);
				json.WriteNumber("pixels", c.Pixels);
				if (c.Within is { } within)
					WriteTarget(json, "within", within);
				break;
			case KeyboardCommand c:
				json.WriteString("type", "keyboard");
				json.WriteString("mode", c.Mode);
				json.WriteString("text", c.Text);
				break;
			case SnapshotCommand c:
				json.WriteString("type", "snapshot");
				WriteFlag(json, "compact", c.Options.Compact);
				WriteFlag(json, "interactive", c.Options.Interactive);
				if (c.Options.Depth is { } depth)
					json.WriteNumber("depth", depth);
				WriteFlag(json, "urls", c.Options.Urls);
				WriteOptional(json, "selector", c.Options.Selector);
				break;
			case ExpectCommand c:
				json.WriteString("type", "expect");
				json.WriteString("matcher", c.Matcher.Kind.ToString().ToLowerInvariant());
				if (c.Target is { } expectTarget)
					WriteTarget(json, "target", expectTarget);
				WriteOptional(json, "name", c.Matcher.Name);
				WriteOptional(json, "expected", c.Matcher.Expected);
				if (c.Matcher.Number is { } number)
					json.WriteNumber("count", number);
				if (c.Matcher.Match != TextMatch.Exact)
					json.WriteString("match", c.Matcher.Match.ToString().ToLowerInvariant());
				WriteFlag(json, "not", c.Options.Negate);
				WriteFlag(json, "ignoreCase", c.Options.IgnoreCase);
				WriteFlag(json, "innerText", c.Options.UseInnerText);
				if (c.Options.Timeout is { } timeout)
					json.WriteNumber("timeoutMs", (int)timeout.TotalMilliseconds);
				break;
			case ExpectRequestCommand c:
				json.WriteString("type", "expectRequest");
				json.WriteString("url", c.Url);
				WriteOptional(json, "method", c.Method);
				WriteStrings(json, "types", c.Types ?? []);
				WriteOptional(json, "status", c.Status);
				WriteFlag(json, "failed", c.Failed);
				if (c.LargerThan is { } larger)
					json.WriteNumber("largerThan", larger);
				WriteOptional(json, "bodyContains", c.BodyContains);
				WriteOptional(json, "bodyRegex", c.BodyRegex);
				WriteFlag(json, "not", c.Options.Negate);
				if (c.Options.Timeout is { } requestTimeout)
					json.WriteNumber("timeoutMs", (int)requestTimeout.TotalMilliseconds);
				break;
			case ExpectSameDocumentCommand c:
				json.WriteString("type", "expectSameDocument");
				WriteFlag(json, "not", c.Options.Negate);
				if (c.Options.Timeout is { } documentTimeout)
					json.WriteNumber("timeoutMs", (int)documentTimeout.TotalMilliseconds);
				break;
			case ReadCommand c:
				json.WriteString("type", "read");
				json.WriteString("what", c.What);
				if (c.Target is { } readTarget)
					WriteTarget(json, "target", readTarget);
				WriteOptional(json, "attribute", c.Attribute);
				break;
			default:
				throw new NotSupportedException($"{command.GetType().Name} has no JSON form: add it to Journey.WriteCommand.");
		}
	}

	private static void WriteTarget(Utf8JsonWriter json, string name, JourneyTarget target)
	{
		json.WriteStartObject(name);
		json.WriteString("kind", target.Kind.ToString().ToLowerInvariant());
		json.WriteString("value", target.Value);
		WriteOptional(json, "name", target.Name);
		WriteFlag(json, "exact", target.Exact);
		if (target.Index is { } index)
			json.WriteNumber("index", index);
		json.WriteEndObject();
	}

	private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
	{
		if (values.Count == 0)
			return;
		json.WriteStartArray(name);
		foreach (var value in values)
			json.WriteStringValue(value);
		json.WriteEndArray();
	}

	private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
	{
		if (value is not null)
			json.WriteString(name, value);
	}

	private static void WriteFlag(Utf8JsonWriter json, string name, bool value)
	{
		if (value)
			json.WriteBoolean(name, true);
	}
}
