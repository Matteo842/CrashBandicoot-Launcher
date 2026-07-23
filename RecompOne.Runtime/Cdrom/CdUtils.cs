namespace RecompOne.Runtime.Cdrom;

public static class CdUtils
{
    public static string ExtractFileName(string rawPath)
    {
        int colon = rawPath.IndexOf(':');
        string path = colon >= 0 ? rawPath[(colon + 1)..] : rawPath;
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        path = path.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
    
    
    
    
    public static string OverlayName(string fileName) => Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
}
