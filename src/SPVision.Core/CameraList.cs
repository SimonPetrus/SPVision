namespace SPVision.Core;

/// <summary>Combines a freshly fetched camera list (ISAPI) with what the user already set per camera.</summary>
public static class CameraList
{
    /// <summary>
    /// Returns the fetched cameras in channel order. A camera that already existed keeps its object
    /// (so open tiles keep pointing at it), its stream key and its "hidden from the list" flag; only
    /// its name and online state are taken from the device. Cameras the device no longer reports drop out.
    /// </summary>
    public static List<Camera> Merge(IReadOnlyList<Camera> existing, IEnumerable<Camera> fetched)
    {
        var known = existing.GroupBy(c => c.Channel).ToDictionary(g => g.Key, g => g.First());
        var result = new List<Camera>();
        foreach (var camera in fetched.GroupBy(c => c.Channel).Select(g => g.First()).OrderBy(c => c.Channel))
        {
            if (known.TryGetValue(camera.Channel, out var old))
            {
                old.Name = camera.Name;
                old.Online = camera.Online;
                result.Add(old);
            }
            else
            {
                result.Add(camera);
            }
        }
        return result;
    }
}
