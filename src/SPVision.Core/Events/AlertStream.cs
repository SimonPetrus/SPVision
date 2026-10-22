using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>
/// One <c>EventNotificationAlert</c> of the recorder's alert stream (<c>GET ISAPI/Event/notification/alertStream</c>), as sent:
/// event type (VMD, linedetection, videoloss, diskfull…), state (active / inactive), channel and the device's own wall-clock
/// time (an offset is dropped, like every ISAPI time). Read only; nothing here is ever sent to the device.
/// </summary>
public sealed record AlertEvent(
    string EventType, string EventState, int? Channel, int? DynChannel, DateTime? Time, string? Description, string? ChannelName)
{
    /// <summary>"active" (or no state at all). "inactive" is the stream's heartbeat (videoloss without a channel, every ~10 s).</summary>
    public bool IsActive => EventState.Length == 0 || EventState.Equals("active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the document is not an EventNotificationAlert or has no event type.</summary>
    public static AlertEvent? Parse(XDocument doc)
    {
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "EventNotificationAlert")
        {
            return null;
        }
        var type = IsapiXml.Get(root, "eventType");
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }
        var time = IsapiClient.ParseTime(IsapiXml.Get(root, "dateTime"));
        return new AlertEvent(
            type,
            IsapiXml.Get(root, "eventState") ?? "",
            ChannelOf(root, "channelID"),
            ChannelOf(root, "dynChannelID"),
            time == default ? null : time,
            Limit(IsapiXml.Get(root, "eventDescription")),
            Limit(IsapiXml.Get(root, "channelName")));
    }

    static int? ChannelOf(XElement root, string name) => IsapiXml.GetInt(root, name) is > 0 and < 10000 and var n ? n : null;

    static string? Limit(string? text) => string.IsNullOrEmpty(text) ? null : text.Length > 128 ? text[..128] : text;
}

/// <summary>
/// Incremental reader of the alert stream body. The recorder keeps the response open and sends one part per event plus a
/// heartbeat (DS-8632NI-I8: <c>videoloss</c> / <c>inactive</c> without a channel, every ~10 s): <c>multipart/mixed;
/// boundary=boundary</c>, each part <c>--boundary</c>, its headers (<c>Content-Type: application/xml</c>, <c>Content-Length</c>),
/// a blank line and one XML <see cref="AlertEvent"/>; smart events may add picture parts. An alert is returned as soon as
/// its <c>Content-Length</c> bytes (or, without a length, its closing tag) are in — not when the next part starts, which can
/// be 10 s later. Without a boundary the documents are taken one by one up to their closing tag. Defensive: at most
/// <see cref="MaxPartLength"/> bytes of a part are ever buffered — a bigger or non-XML part (a picture) is skipped by its
/// length or up to the next boundary, never held — and every XML goes through <see cref="IsapiXml.Parse"/> (no DTD, size
/// cap). A bad part is counted, never thrown. Unit tested (EventTests).
/// </summary>
public sealed class AlertStreamParser
{
    /// <summary>Largest part kept (an alert is about 0.5 KB). Pictures and anything bigger are skipped.</summary>
    public const int MaxPartLength = 64 * 1024;

    /// <summary>Largest header block of a part.</summary>
    const int MaxHeaderLength = 8 * 1024;

    /// <summary>Bytes appended at a time: the buffer never holds much more than one part.</summary>
    const int Chunk = 16 * 1024;

    static readonly byte[] EndTag = "</EventNotificationAlert>"u8.ToArray();

    enum Stage
    {
        Seeking,      // up to the next delimiter (preamble, the CRLF after a body, a skipped part)
        Headers,      // right after a delimiter
        Body,         // Content-Length known: waiting for _bodyLength bytes
        BodyToEnd,    // no length: up to the alert's closing tag (or the next delimiter)
    }

    readonly byte[]? _delimiter;     // "--" + boundary; null: documents back to back
    byte[] _buffer = new byte[4096];
    int _length;
    Stage _stage;
    int _bodyLength;
    long _skipBytes;                 // bytes of a skipped part (known length) still to drop, never buffered
    bool _skippingDocument;          // no-boundary mode: the rest of an oversized document

    public AlertStreamParser(string? boundary)
    {
        var name = boundary?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(name))
        {
            _delimiter = Encoding.ASCII.GetBytes(name.StartsWith("--", StringComparison.Ordinal) ? name : "--" + name);
        }
    }

    /// <summary>Parts that were dropped: too big, not XML, unreadable XML or not an alert.</summary>
    public int Skipped { get; private set; }

    /// <summary>Alerts read so far (heartbeats included).</summary>
    public int Alerts { get; private set; }

    /// <summary>Bytes held right now (tests: stays near one part whatever arrives).</summary>
    internal int Buffered => _length;

    /// <summary>Feeds the next bytes of the body; returns the alerts completed by them, in order.</summary>
    public IReadOnlyList<AlertEvent> Feed(ReadOnlySpan<byte> data)
    {
        var found = new List<AlertEvent>();
        while (!data.IsEmpty)
        {
            if (_skipBytes > 0)
            {
                var drop = (int)Math.Min(_skipBytes, data.Length);
                _skipBytes -= drop;
                data = data[drop..];
                continue;
            }
            var take = Math.Min(data.Length, Chunk);
            Append(data[..take]);
            data = data[take..];
            if (_delimiter is null)
            {
                ReadDocuments(found);
            }
            else
            {
                ReadParts(_delimiter, found);
            }
        }
        return found;
    }

    void ReadParts(byte[] delimiter, List<AlertEvent> found)
    {
        while (_skipBytes == 0 && Step(delimiter, found))
        {
        }
    }

    /// <summary>One step of the part reader; false when it needs more bytes.</summary>
    bool Step(byte[] delimiter, List<AlertEvent> found)
    {
        var span = _buffer.AsSpan(0, _length);
        switch (_stage)
        {
            case Stage.Seeking:
            {
                var at = span.IndexOf(delimiter);
                if (at < 0)
                {
                    Keep(delimiter.Length - 1); // a delimiter may be split between two reads
                    return false;
                }
                Drop(at + delimiter.Length);
                _stage = Stage.Headers;
                return true;
            }
            case Stage.Headers:
                return Headers(span);
            case Stage.Body:
                if (span.Length < _bodyLength)
                {
                    return false;
                }
                Body(span[.._bodyLength], found);
                Drop(_bodyLength);
                _stage = Stage.Seeking;
                return true;
            default:
            {
                var end = span.IndexOf(EndTag);
                var next = span.IndexOf(delimiter);
                if (end >= 0 && (next < 0 || end < next))
                {
                    Body(span[..(end + EndTag.Length)], found);
                    Drop(end + EndTag.Length);
                    _stage = Stage.Seeking;
                    return true;
                }
                if (next >= 0)
                {
                    Body(span[..next], found); // not an alert, or cut short: counted as skipped
                    Drop(next);
                    _stage = Stage.Seeking;
                    return true;
                }
                if (span.Length > MaxPartLength)
                {
                    Skipped++;
                    _stage = Stage.Seeking;
                    return true;
                }
                return false;
            }
        }
    }

    /// <summary>After a delimiter: "--" (closing), a body without headers, or headers + blank line.</summary>
    bool Headers(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && span[start] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            start++;
        }
        if (span.Length - start < 2)
        {
            return false;
        }
        if (span[start] == (byte)'-' && span[start + 1] == (byte)'-')
        {
            Drop(start + 2); // closing delimiter "--boundary--"
            _stage = Stage.Seeking;
            return true;
        }
        if (span[start] == (byte)'<')
        {
            Drop(start);
            _stage = Stage.BodyToEnd;
            return true;
        }
        var rest = span[start..];
        var split = rest.IndexOf("\r\n\r\n"u8);
        var gap = 4;
        var lf = rest.IndexOf("\n\n"u8);
        if (lf >= 0 && (split < 0 || lf < split))
        {
            split = lf;
            gap = 2;
        }
        if (split < 0)
        {
            if (rest.Length <= MaxHeaderLength)
            {
                return false;
            }
            Skipped++;
            _stage = Stage.Seeking;
            return true;
        }
        var (contentType, contentLength) = ReadHeaders(rest[..split]);
        Drop(start + split + gap);
        var xml = contentType is null || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
        if (!xml || contentLength > MaxPartLength)
        {
            Skipped++; // a picture of a smart event, or too big: dropped by its length, else up to the next delimiter
            _stage = Stage.Seeking;
            var drop = (int)Math.Min(contentLength ?? 0, _length);
            Drop(drop);
            _skipBytes = (contentLength ?? 0) - drop;
            return true;
        }
        if (contentLength is { } length)
        {
            _bodyLength = (int)length;
            _stage = Stage.Body;
        }
        else
        {
            _stage = Stage.BodyToEnd;
        }
        return true;
    }

    static (string? ContentType, long? ContentLength) ReadHeaders(ReadOnlySpan<byte> block)
    {
        string? contentType = null;
        long? contentLength = null;
        foreach (var line in Encoding.ASCII.GetString(block).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                contentLength = n;
            }
        }
        return (contentType, contentLength);
    }

    void ReadDocuments(List<AlertEvent> found)
    {
        while (true)
        {
            var at = _buffer.AsSpan(0, _length).IndexOf(EndTag);
            if (at < 0)
            {
                if (_length > MaxPartLength)
                {
                    if (!_skippingDocument)
                    {
                        Skipped++;
                    }
                    _skippingDocument = true;
                    Keep(EndTag.Length - 1);
                }
                return;
            }
            var end = at + EndTag.Length;
            if (_skippingDocument)
            {
                _skippingDocument = false; // the rest of an oversized document
            }
            else
            {
                Body(_buffer.AsSpan(0, end), found);
            }
            Drop(end);
        }
    }

    void Body(ReadOnlySpan<byte> body, List<AlertEvent> found)
    {
        while (!body.IsEmpty && body[0] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            body = body[1..];
        }
        if (body.IsEmpty || body[0] != (byte)'<')
        {
            Skipped++;
            return;
        }
        try
        {
            if (AlertEvent.Parse(IsapiXml.Parse(Encoding.UTF8.GetString(body).TrimEnd())) is { } alert)
            {
                Alerts++;
                found.Add(alert);
                return;
            }
        }
        catch (System.Xml.XmlException)
        {
        }
        Skipped++;
    }

    void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    void Drop(int count)
    {
        _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
        _length -= count;
    }

    /// <summary>Drops everything but the last <paramref name="tail"/> bytes; a grown buffer shrinks back.</summary>
    void Keep(int tail)
    {
        if (_length > tail)
        {
            Drop(_length - tail);
        }
        if (_buffer.Length > 4 * MaxPartLength)
        {
            Array.Resize(ref _buffer, Math.Max(4096, _length));
        }
    }
}
