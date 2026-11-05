// CommandLine.Split in Nullean.Cdp.Journeys: a shell-like split of one journey line.
// Whitespace separates arguments. Double quotes keep whitespace and treat \" and \\ as escapes; other backslashes
// inside them are literal. Single quotes keep everything. Outside quotes a backslash escapes the next character.
// "#" followed by whitespace (or the end) at the start of an argument starts a comment; "#id" is an argument.

const isSpace = (c: string) => /\s/.test(c);

/** The arguments of `line`, or the reason it cannot be split. */
export function splitLine(line: string): { args: string[] } | { error: string } {
  const args: string[] = [];
  let current = '';
  let inArgument = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (isSpace(c)) {
      if (inArgument) {
        args.push(current);
        current = '';
        inArgument = false;
      }
      continue;
    }
    if (c === '#' && !inArgument && (i + 1 === line.length || isSpace(line[i + 1]))) break;
    inArgument = true;
    if (c === '"') {
      let closed = false;
      for (i++; i < line.length; i++) {
        if (line[i] === '\\' && i + 1 < line.length && (line[i + 1] === '"' || line[i + 1] === '\\')) current += line[++i];
        else if (line[i] === '"') {
          closed = true;
          break;
        } else current += line[i];
      }
      if (!closed) return { error: 'the double quote is not closed' };
    } else if (c === "'") {
      const end = line.indexOf("'", i + 1);
      if (end < 0) return { error: 'the single quote is not closed' };
      current += line.slice(i + 1, end);
      i = end;
    } else if (c === '\\' && i + 1 < line.length) {
      current += line[++i];
    } else current += c;
  }
  if (inArgument) args.push(current);
  return { args };
}

/** Joins arguments into one line that `splitLine` turns back into them (CommandLine.Join). */
export function joinLine(args: readonly string[]): string {
  return args
    .map((a) => {
      if (/[\r\n]/.test(a)) throw new Error('An argument with a line break cannot be written on one journey line.');
      if (a.length > 0 && a !== '#' && !/[\s"'\\]/.test(a)) return a;
      return `"${a.replace(/["\\]/g, '\\$&')}"`;
    })
    .join(' ');
}
