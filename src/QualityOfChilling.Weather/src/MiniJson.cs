using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealTimeWeatherForChill;

internal static class MiniJson
{
    internal static object? Deserialize(string json)
    {
        if (json == null)
        {
            return null;
        }

        return Parser.Parse(json);
    }

    private sealed class Parser
    {
        private readonly string json;
        private int index;

        private Parser(string json)
        {
            this.json = json;
        }

        internal static object? Parse(string json)
        {
            return new Parser(json).ParseValue();
        }

        private object? ParseValue()
        {
            EatWhitespace();
            if (index >= json.Length)
            {
                return null;
            }

            return json[index] switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => ParseString(),
                't' => ParseLiteral("true", true),
                'f' => ParseLiteral("false", false),
                'n' => ParseLiteral("null", null),
                _ => ParseNumber()
            };
        }

        private Dictionary<string, object> ParseObject()
        {
            var table = new Dictionary<string, object>();
            index++;

            while (true)
            {
                EatWhitespace();
                if (index >= json.Length)
                {
                    return table;
                }

                if (json[index] == '}')
                {
                    index++;
                    return table;
                }

                var key = ParseString();
                EatWhitespace();
                if (index < json.Length && json[index] == ':')
                {
                    index++;
                }

                var value = ParseValue();
                table[key] = value ?? string.Empty;
                EatWhitespace();

                if (index < json.Length && json[index] == ',')
                {
                    index++;
                }
            }
        }

        private List<object> ParseArray()
        {
            var array = new List<object>();
            index++;

            while (true)
            {
                EatWhitespace();
                if (index >= json.Length)
                {
                    return array;
                }

                if (json[index] == ']')
                {
                    index++;
                    return array;
                }

                array.Add(ParseValue() ?? string.Empty);
                EatWhitespace();
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                }
            }
        }

        private string ParseString()
        {
            var builder = new StringBuilder();
            index++;

            while (index < json.Length)
            {
                var c = json[index++];
                if (c == '"')
                {
                    break;
                }

                if (c != '\\' || index >= json.Length)
                {
                    builder.Append(c);
                    continue;
                }

                var escaped = json[index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 <= json.Length && ushort.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            index += 4;
                        }
                        break;
                }
            }

            return builder.ToString();
        }

        private object? ParseLiteral(string literal, object? value)
        {
            if (string.Compare(json, index, literal, 0, literal.Length, StringComparison.Ordinal) == 0)
            {
                index += literal.Length;
                return value;
            }

            return null;
        }

        private object ParseNumber()
        {
            var start = index;
            while (index < json.Length && "-+0123456789.eE".IndexOf(json[index]) >= 0)
            {
                index++;
            }

            var number = json.Substring(start, index - start);
            if (number.IndexOf('.') >= 0 || number.IndexOf('e') >= 0 || number.IndexOf('E') >= 0)
            {
                return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble) ? parsedDouble : 0d;
            }

            return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong) ? parsedLong : 0L;
        }

        private void EatWhitespace()
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }
    }
}
