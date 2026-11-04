using System.Text;

namespace Nullean.Cdp.Journeys;

/// <summary>
/// Splits a journey line into arguments the way a shell would, and joins arguments back into a line that splits the
/// same way.
/// </summary>
/// <remarks>
/// Whitespace separates arguments. Double quotes keep whitespace and treat <c>\"</c> and <c>\\</c> as escapes;
/// other backslashes inside them are literal. Single quotes keep everything literally. Outside quotes a backslash
/// escapes the next character. Adjacent quoted and plain parts form one argument. <c>#</c> followed by whitespace (or
/// ending the line) at the start of an argument starts a comment; <c>#id</c> is an argument, as CSS needs.
/// </remarks>
public static class CommandLine
{
	/// <summary>Splits <paramref name="line"/>; <paramref name="error"/> says what is wrong when it returns <c>null</c>.</summary>
	public static IReadOnlyList<string>? Split(string line, out string? error)
	{
		ArgumentNullException.ThrowIfNull(line);
		error = null;
		var arguments = new List<string>();
		var current = new StringBuilder();
		var inArgument = false;
		for (var i = 0; i < line.Length; i++)
		{
			var c = line[i];
			if (char.IsWhiteSpace(c))
			{
				if (inArgument)
				{
					arguments.Add(current.ToString());
					current.Clear();
					inArgument = false;
				}

				continue;
			}

			// "# " starts a comment; "#id" is a CSS selector, so a # directly followed by text is not one.
			if (c == '#' && !inArgument && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
				break;

			inArgument = true;
			switch (c)
			{
				case '"':
					{
						var closed = false;
						for (i++; i < line.Length; i++)
						{
							if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
								current.Append(line[++i]);
							else if (line[i] == '"')
							{
								closed = true;
								break;
							}
							else
								current.Append(line[i]);
						}

						if (!closed)
						{
							error = "the double quote is not closed";
							return null;
						}

						break;
					}
				case '\'':
					{
						var end = line.IndexOf('\'', i + 1);
						if (end < 0)
						{
							error = "the single quote is not closed";
							return null;
						}

						current.Append(line, i + 1, end - i - 1);
						i = end;
						break;
					}
				case '\\' when i + 1 < line.Length:
					current.Append(line[++i]);
					break;
				default:
					current.Append(c);
					break;
			}
		}

		if (inArgument)
			arguments.Add(current.ToString());
		return arguments;
	}

	/// <summary>
	/// Joins <paramref name="arguments"/> into one line that <see cref="Split"/> turns back into them. Arguments that
	/// need it are double quoted. Line breaks cannot be written on one line, so they are an error.
	/// </summary>
	public static string Join(IEnumerable<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		var line = new StringBuilder();
		foreach (var argument in arguments)
		{
			if (argument.Contains('\n') || argument.Contains('\r'))
				throw new ArgumentException("An argument with a line break cannot be written on one journey line.", nameof(arguments));
			if (line.Length > 0)
				line.Append(' ');
			// A lone "#" would read as a comment; "#id" would not.
			if (argument.Length > 0 && argument != "#" && !argument.Any(NeedsQuotes))
			{
				line.Append(argument);
				continue;
			}

			line.Append('"');
			foreach (var c in argument)
			{
				if (c is '"' or '\\')
					line.Append('\\');
				line.Append(c);
			}

			line.Append('"');
		}

		return line.ToString();
	}

	private static bool NeedsQuotes(char c) => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\';
}
