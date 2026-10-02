namespace MarketplaceHub.Workflows;
public sealed class WorkflowTestRunner
{
    public int Checks { get; private set; }
    public int Failures { get; private set; }
    public async Task CheckAsync(string name, Func<Task> test)
    {
        Checks++;
        try { await test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { Failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
    }
    public static void Expect(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected rejection: " + typeof(T).Name);
    }
}
