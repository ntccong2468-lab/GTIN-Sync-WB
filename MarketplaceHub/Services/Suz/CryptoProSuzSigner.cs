using System.Diagnostics;
namespace MarketplaceHub.Services.Suz;
public sealed class CryptoProSuzSigner : ISuzSigner
{
    public async Task<byte[]> SignAsync(byte[] payload, string certificateThumbprint, bool detached, CancellationToken ct)
    {
        var exe = FindExecutable() ?? throw new InvalidOperationException("cryptopro_not_installed");
        var dir = Path.Combine(Path.GetTempPath(), "MarketplaceHub-sign-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "payload.bin"); var output = Path.Combine(dir, "signature.p7s");
        try
        {
            await File.WriteAllBytesAsync(input, payload, ct);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-sign", "-uMy", "-thumbprint", certificateThumbprint.Replace(" ", ""), "-der", detached ? "-detached" : "-attached", input, output }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("cryptopro_start_failed");
            // Drain concurrently before waiting: CSP diagnostics can fill either pipe.
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; throw new TimeoutException("cryptopro_timeout"); }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("cryptopro_sign_failed");
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidOperationException("cryptopro_signature_missing");
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
    private static string? FindExecutable()
    {
        var paths = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Crypto Pro", "CSP"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Crypto Pro", "CSP") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in paths) { try { var path = Path.Combine(dir.Trim().Trim('"'), "cryptcp.exe"); if (File.Exists(path)) return path; } catch (ArgumentException) { } } return null;
    }
}
