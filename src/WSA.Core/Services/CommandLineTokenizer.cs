using System.Text;

namespace WSA.Core.Services;

/// <summary>
/// Splits a command line into arguments without involving any shell.
/// Rules: whitespace separates tokens; single or double quotes group text (quotes are removed);
/// inside double quotes, <c>\"</c> yields a literal quote; every other backslash is literal so
/// Windows paths such as <c>C:\Users\me\app.apk</c> survive unchanged.
/// </summary>
public static class CommandLineTokenizer
{
    public static bool TryTokenize(string input, out List<string> tokens, out string? error)
    {
        tokens = new List<string>();
        error = null;
        var current = new StringBuilder();
        var inToken = false;
        char quote = '\0';

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (quote == '"' && c == '\\' && i + 1 < input.Length && input[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote != '\0')
        {
            error = $"Unterminated {quote} quote.";
            return false;
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return true;
    }
}
