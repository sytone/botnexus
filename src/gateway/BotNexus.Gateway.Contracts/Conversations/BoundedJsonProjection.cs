using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Reads a small top-level JSON projection with fixed buffers. Unlike DeserializeAsync with
/// ignored properties, oversized ignored string tokens never force the parser buffer to grow.
/// Selected values are limited to 4096 raw characters, the projection to 32768, and nesting to 64.
/// Oversized selected metadata fails closed rather than allocating an unbounded token.
/// </summary>
public static class BoundedJsonProjection
{
    public static async Task<T?> ReadAsync<T>(Stream stream, JsonSerializerOptions options,
        IReadOnlySet<string> properties, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var grammar = new JsonGrammar();
        var output = new StringBuilder("{");
        var key = new StringBuilder();
        var value = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var readingKey = false;
        var keyOverflow = false;
        var selected = false;
        var awaitingValue = false;
        var readingValue = false;
        var ended = false;
        string? name = null;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                grammar.Consume(ch);
                if (ended)
                {
                    if (!char.IsWhiteSpace(ch)) throw new JsonException("Unexpected trailing JSON.");
                    continue;
                }
                if (depth == 0)
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    if (ch != '{') throw new JsonException("Expected metadata object.");
                    depth = 1;
                    continue;
                }
                // A top-level delimiter completes the preceding value, outside strings only.
                if (!inString && depth == 1 && (ch == ',' || ch == '}'))
                {
                    if (readingValue && selected)
                    {
                        if (output.Length > 1) output.Append(',');
                        output.Append(JsonSerializer.Serialize(name)).Append(':').Append(value);
                        if (output.Length > 32768) throw new JsonException("Metadata projection too large.");
                    }
                    readingValue = false;
                    awaitingValue = false;
                    selected = false;
                    value.Clear();
                    if (ch == '}') { ended = true; depth = 0; }
                    continue;
                }
                if (!inString && depth == 1 && !readingValue && !readingKey)
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    if (ch == ':') { awaitingValue = true; continue; }
                    if (!awaitingValue)
                    {
                        if (ch != '"') throw new JsonException("Expected metadata property.");
                        readingKey = true;
                        key.Clear();
                        keyOverflow = false;
                    }
                    else readingValue = true;
                }
                if (readingKey)
                {
                    if (key.Length < 256) key.Append(ch); else keyOverflow = true;
                }
                else if (readingValue && selected)
                {
                    if (value.Length == 4096) throw new JsonException("Metadata field too large.");
                    value.Append(ch);
                }
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch != '"') continue;
                    inString = false;
                    if (readingKey)
                    {
                        readingKey = false;
                        name = keyOverflow ? null : JsonSerializer.Deserialize<string>(key.ToString());
                        selected = name is not null && properties.Contains(name);
                    }
                }
                else if (ch == '"') inString = true;
                else if (ch is '{' or '[')
                {
                    if (++depth > 64) throw new JsonException("Metadata nesting too deep.");
                }
                else if (ch is '}' or ']') depth--;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!ended || inString) throw new JsonException("Incomplete metadata object.");
        grammar.Finish();
        output.Append('}');
        return JsonSerializer.Deserialize<T>(output.ToString(), options);
    }
    // Incremental grammar validation is independent of projection: ignored tokens must be
    // valid too, but strings and numbers of arbitrary length never need buffering.
    private sealed class JsonGrammar
    {
        private enum State { KeyOrEnd, Key, Colon, Value, ObjectEnd, ArrayValueOrEnd, ArrayValue, ArrayEnd }
        private readonly State[] _stack = new State[64];
        private int _depth;
        private bool _started, _ended, _string, _key, _escape;
        private int _unicode;
        private string? _literal;
        private int _literalIndex;
        private int _number;

        public void Consume(char ch)
        {
            if (_string)
            {
                if (_unicode > 0)
                {
                    if (!char.IsAsciiHexDigit(ch)) Fail();
                    _unicode--;
                }
                else if (_escape)
                {
                    _escape = false;
                    if (ch == 'u') _unicode = 4;
                    else if (ch is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')) Fail();
                }
                else if (ch == '\\') _escape = true;
                else if (ch == '"')
                {
                    _string = false;
                    if (_key) _stack[_depth - 1] = State.Colon;
                    else CompleteValue();
                }
                else if (ch < 0x20) Fail();
                return;
            }
            if (_literal is not null)
            {
                if (ch != _literal[_literalIndex++]) Fail();
                if (_literalIndex == _literal.Length) { _literal = null; CompleteValue(); }
                return;
            }
            if (_number != 0)
            {
                var digit = ch is >= '0' and <= '9';
                switch (_number)
                {
                    case 1: if (!digit) Fail(); _number = ch == '0' ? 2 : 3; return;
                    case 2: if (digit) Fail(); break;
                    case 3: if (digit) return; break;
                    case 4: if (!digit) Fail(); _number = 5; return;
                    case 5: if (digit) return; break;
                    case 6:
                        if (ch is '+' or '-') { _number = 7; return; }
                        if (!digit) Fail(); _number = 8; return;
                    case 7: if (!digit) Fail(); _number = 8; return;
                    case 8: if (digit) return; break;
                }
                if (ch == '.' && _number is 2 or 3) { _number = 4; return; }
                if (ch is 'e' or 'E' && _number is 2 or 3 or 5) { _number = 6; return; }
                _number = 0;
                CompleteValue();
                // The current character terminates the number and still needs grammar validation.
            }
            if (ch is ' ' or '\t' or '\r' or '\n') return;
            if (_ended) Fail();
            if (!_started)
            {
                if (ch != '{') Fail();
                _started = true; Push(State.KeyOrEnd); return;
            }
            switch (_stack[_depth - 1])
            {
                case State.KeyOrEnd when ch == '}': Close(); return;
                case State.KeyOrEnd:
                case State.Key:
                    if (ch != '"') Fail();
                    _string = true; _key = true; return;
                case State.Colon:
                    if (ch != ':') Fail();
                    _stack[_depth - 1] = State.Value; return;
                case State.ObjectEnd:
                    if (ch == '}') { Close(); return; }
                    if (ch != ',') Fail();
                    _stack[_depth - 1] = State.Key; return;
                case State.ArrayEnd:
                    if (ch == ']') { Close(); return; }
                    if (ch != ',') Fail();
                    _stack[_depth - 1] = State.ArrayValue; return;
                case State.ArrayValueOrEnd when ch == ']': Close(); return;
            }
            if (ch == '{') Push(State.KeyOrEnd);
            else if (ch == '[') Push(State.ArrayValueOrEnd);
            else if (ch == '"') { _string = true; _key = false; }
            else if (ch == '-') _number = 1;
            else if (ch is >= '0' and <= '9') _number = ch == '0' ? 2 : 3;
            else if (ch is 't' or 'f' or 'n')
            {
                _literal = ch == 't' ? "true" : ch == 'f' ? "false" : "null";
                _literalIndex = 1;
            }
            else Fail();
        }

        private void Push(State state)
        {
            if (_depth == _stack.Length) throw new JsonException("Metadata nesting too deep.");
            _stack[_depth++] = state;
        }
        private void Close()
        {
            if (--_depth == 0) _ended = true;
            else CompleteValue();
        }
        private void CompleteValue()
        {
            _stack[_depth - 1] = _stack[_depth - 1] == State.Value ? State.ObjectEnd : State.ArrayEnd;
        }
        public void Finish()
        {
            if (!_ended || _string || _literal is not null || _number != 0) Fail();
        }
        private static void Fail() => throw new JsonException("Invalid metadata JSON grammar.");
    }
}
