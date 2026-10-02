namespace MarketplaceHub.Infrastructure;
public sealed class DatabaseInstanceLock : IDisposable
{
    private readonly FileStream stream;
    private DatabaseInstanceLock(FileStream stream) => this.stream = stream;
    public static bool TryAcquire(string dbPath, out DatabaseInstanceLock? heldLock)
    {
        heldLock = null;
        var path = Path.GetFullPath(dbPath) + ".instance.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { heldLock = new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); return true; }
        catch (IOException) { return false; }
    }
    public void Dispose() => stream.Dispose();
}
