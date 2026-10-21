using System.Net;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>In-memory device: GET returns a copy of the stored document, PUT stores it (unless told to ignore it).</summary>
sealed class FakeTransport : IConfigTransport
{
    public Dictionary<string, XDocument> Docs { get; } = [];
    public List<string> Calls { get; } = [];
    public Dictionary<string, XDocument> Sent { get; } = [];

    /// <summary>ResponseStatus per path for PUT; default OK.</summary>
    public Dictionary<string, IsapiResponseStatus> PutStatus { get; } = [];

    /// <summary>Paths whose PUT answers OK but changes nothing (the device "ignores" the value).</summary>
    public HashSet<string> IgnorePut { get; } = [];

    public Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default)
    {
        Calls.Add("GET " + path);
        return Docs.TryGetValue(path, out var doc)
            ? Task.FromResult(new XDocument(doc))
            : throw new IsapiException("not found", HttpStatusCode.NotFound);
    }

    public Task<IsapiResponseStatus> PutConfigAsync(string path, XDocument body, CancellationToken ct = default)
    {
        IsapiWritePolicy.EnsureAllowed(path);
        Calls.Add("PUT " + path);
        Sent[path] = new XDocument(body);
        var status = PutStatus.GetValueOrDefault(path) ?? new IsapiResponseStatus(1, "OK", "ok");
        if (status.IsOk && !IgnorePut.Contains(path))
        {
            Docs[path] = new XDocument(body);
        }
        return Task.FromResult(status);
    }
}
