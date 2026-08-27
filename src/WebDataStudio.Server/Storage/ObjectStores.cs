namespace WebDataStudio.Server.Storage;

/// Which store a URL means. The one place that knows all four providers by name; everything above
/// it holds an `IObjectStore` and cannot tell them apart.
public static class ObjectStores
{
    public static IObjectStore For(StorageTarget target) => target.Provider switch
    {
        StorageProvider.S3 => new S3ObjectStore(target),
        StorageProvider.AzureBlob => new AzureBlobObjectStore(target),
        StorageProvider.GoogleCloud => new GcsObjectStore(target),
        StorageProvider.Local => new LocalObjectStore(target),
        _ => throw new NotSupportedException($"no object store for {target.Provider}"),
    };

    public static IObjectStore For(string url) => For(StorageUrl.Parse(url));
}
