using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace MishaWeb;

internal static class WebView2LoaderBootstrap
{
    private const string ResourceName = "MishaWeb.WebView2Loader.dll";
    private static readonly object LoaderSync = new();
    private static readonly TimeSpan RuntimeProbeTimeout = TimeSpan.FromSeconds(20);
    private static nint loaderHandle;
    private static int cleanupStarted;
    private static (byte[] Hash, string VersionKey)? cachedEmbeddedDigest;

    public static void EnsureLoaded()
    {
        lock (LoaderSync)
        {
            if (loaderHandle != 0) return;

            var assembly = Assembly.GetExecutingAssembly();
            byte[]? rentedLoaderBuffer = null;
            byte[]? fallbackLoaderBytes = null;
            var loaderByteLength = 0;

            try
            {
                byte[] expectedHash;
                string versionKey;
                if (cachedEmbeddedDigest is { } cached)
                {
                    expectedHash = cached.Hash;
                    versionKey = cached.VersionKey;
                }
                else
                {
                    using var stream = assembly.GetManifestResourceStream(ResourceName)
                        ?? throw new InvalidOperationException("The embedded WebView2 loader is missing.");
                    if (stream.CanSeek && stream.Length is > 0 and <= int.MaxValue)
                    {
                        loaderByteLength = (int)stream.Length;
                        rentedLoaderBuffer = ArrayPool<byte>.Shared.Rent(loaderByteLength);
                        stream.ReadExactly(rentedLoaderBuffer.AsSpan(0, loaderByteLength));
                        expectedHash = SHA256.HashData(rentedLoaderBuffer.AsSpan(0, loaderByteLength));
                    }
                    else
                    {
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        fallbackLoaderBytes = buffer.ToArray();
                        loaderByteLength = fallbackLoaderBytes.Length;
                        expectedHash = SHA256.HashData(fallbackLoaderBytes);
                    }

                    versionKey = Convert.ToHexString(expectedHash).ToLowerInvariant();
                    cachedEmbeddedDigest = (expectedHash, versionKey);
                }

                var runtimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MishaWeb",
                    "Runtime");
                var folder = Path.Combine(runtimeRoot, versionKey);
                var loaderPath = Path.Combine(folder, "WebView2Loader.dll");

                Directory.CreateDirectory(folder);
                EnsureNoReparsePoints(runtimeRoot, folder, loaderPath);

                FileStream? verifiedLoader = TryOpenVerifiedLoader(loaderPath, expectedHash);
                if (verifiedLoader is null)
                {
                    ReadOnlySpan<byte> loaderBytes;
                    if (rentedLoaderBuffer is not null)
                    {
                        loaderBytes = rentedLoaderBuffer.AsSpan(0, loaderByteLength);
                    }
                    else if (fallbackLoaderBytes is not null)
                    {
                        loaderBytes = fallbackLoaderBytes;
                    }
                    else
                    {
                        fallbackLoaderBytes = ReadEmbeddedLoaderBytes(assembly);
                        loaderBytes = fallbackLoaderBytes;
                    }

                    WriteLoaderAtomically(loaderPath, loaderBytes);
                    EnsureNoReparsePoints(runtimeRoot, folder, loaderPath);
                    verifiedLoader = OpenVerifiedLoader(loaderPath, expectedHash);
                }

                try
                {
                    // Keep the non-sharing verification handle open through Load so the
                    // verified bytes cannot be replaced between hashing and mapping.
                    loaderHandle = NativeLibrary.Load(loaderPath);
                }
                finally
                {
                    verifiedLoader.Dispose();
                }

                ScheduleObsoleteRuntimeCleanup(runtimeRoot, versionKey);
            }
            finally
            {
                if (rentedLoaderBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedLoaderBuffer);
                }
            }
        }
    }

    internal static async Task<string> ProbeRuntimeAsync(string userDataFolder)
    {
        EnsureLoaded();
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder).WaitAsync(RuntimeProbeTimeout).ConfigureAwait(false);
        return environment.BrowserVersionString;
    }

    private static byte[] ReadEmbeddedLoaderBytes(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded WebView2 loader is missing.");
        if (stream.CanSeek && stream.Length is > 0 and <= int.MaxValue)
        {
            var exactBytes = new byte[stream.Length];
            stream.ReadExactly(exactBytes);
            return exactBytes;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static FileStream? TryOpenVerifiedLoader(string loaderPath, ReadOnlySpan<byte> expectedHash)
    {
        if (!File.Exists(loaderPath)) return null;
        try
        {
            return OpenVerifiedLoader(loaderPath, expectedHash);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteLoaderAtomically(string destination, ReadOnlySpan<byte> bytes)
    {
        var temporaryPath = destination
            + "."
            + Environment.ProcessId
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";
        var moved = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporaryPath, destination, true);
                moved = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A concurrent MishaWeb process may already have extracted and mapped
                // the identical content-addressed loader; OpenVerifiedLoader verifies
                // the destination bytes afterward.
                if (!File.Exists(destination)) throw;
            }
        }
        finally
        {
            if (!moved)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch
                {
                    // A leftover temporary file is harmless and can be replaced later.
                }
            }
        }
    }

    private static void ScheduleObsoleteRuntimeCleanup(string runtimeRoot, string activeVersionKey)
    {
        if (Interlocked.Exchange(ref cleanupStarted, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
                {
                    var name = Path.GetFileName(directory);
                    if (string.Equals(name, activeVersionKey, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                        Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                        // Still mapped by another running instance or access-denied; skip.
                    }
                }
            }
            catch
            {
                // Best-effort cache cleanup.
            }
        });
    }

    private static FileStream OpenVerifiedLoader(string path, ReadOnlySpan<byte> expectedHash)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
            {
                throw new InvalidDataException("The extracted WebView2 loader failed integrity verification.");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureNoReparsePoints(string runtimeRoot, string folder, string loaderPath)
    {
        if (File.GetAttributes(runtimeRoot).HasFlag(FileAttributes.ReparsePoint)
            || File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The WebView2 runtime cache cannot use a linked directory.");
        }
        if (File.Exists(loaderPath)
            && File.GetAttributes(loaderPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The WebView2 loader cache cannot use a linked file.");
        }
    }
}
