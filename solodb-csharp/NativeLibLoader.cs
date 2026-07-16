using System.Reflection;
using System.Runtime.InteropServices;

namespace SoloDbManager;

/// <summary>
/// Extracts the embedded SQLite native library to a temp directory and
/// pre-loads it, so the published AOT binary is a SINGLE self-contained file
/// with no companion native lib.
///
/// The trick: the SQLitePCLRaw P/Invoke imports "e_sqlite3" (no prefix, no
/// extension). If we dlopen the library by full path FIRST, the dynamic
/// loader caches it under that soname, and the subsequent P/Invoke-triggered
/// load by name "e_sqlite3" hits the cache. We name the extracted file
/// "e_sqlite3.so" / "e_sqlite3.dll" so the lookup also works directly.
/// </summary>
public static class NativeLibLoader
{
    public static void InitSQLiteNative()
    {
        var asm = typeof(NativeLibLoader).Assembly;
        const string resourceName = "sqlite_native_lib";
        Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return; // dev build: fall back to default loader
        using (stream)
        {
            string ext = OperatingSystem.IsWindows() ? ".dll"
                       : OperatingSystem.IsMacOS() ? ".dylib"
                       : ".so";
            // SQLitePCLRaw imports "e_sqlite3", so the file must be named
            // e_sqlite3.<ext> for the loader to find it by that name.
            const string fileBase = "e_sqlite3";

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            var hashSuffix = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

            // Per-version cache dir to avoid collisions / stale files.
            var cacheDir = Path.Combine(Path.GetTempPath(), "solodb-manager-native", hashSuffix);
            Directory.CreateDirectory(cacheDir);
            var libPath = Path.Combine(cacheDir, $"{fileBase}{ext}");

            if (!File.Exists(libPath))
            {
                // Clean older versions.
                var parent = Path.GetDirectoryName(cacheDir)!;
                if (Directory.Exists(parent))
                {
                    foreach (var d in Directory.EnumerateDirectories(parent))
                    {
                        if (d != cacheDir)
                        {
                            try { Directory.Delete(d, recursive: true); } catch { }
                        }
                    }
                }
                File.WriteAllBytes(libPath, bytes);
            }

            // Pre-load by full path. On Unix this makes dlopen cache the library
            // so a later dlopen("e_sqlite3") hits the cache; on Windows the
            // SetDllImportResolver below handles the name resolution.
            try
            {
                NativeLibrary.Load(libPath);
            }
            catch
            {
                // Pre-load failed; fall back to resolver below.
            }

            // Belt-and-suspenders: register a DllImport resolver on every loaded
            // assembly so the name "e_sqlite3" always resolves to our full path,
            // regardless of which assembly declares the P/Invoke.
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        NativeLibrary.SetDllImportResolver(a, (name, _, _) =>
                        {
                            if (name == "e_sqlite3" || name == "libe_sqlite3")
                                return NativeLibrary.Load(libPath);
                            return IntPtr.Zero;
                        });
                    }
                    catch { /* some assemblies don't allow setting a resolver */ }
                }
            }
            catch { }

            // On Unix also set LD_LIBRARY_PATH / DYLD_LIBRARY_PATH for child
            // processes (harmless, and helps if any subprocess needs SQLite).
            if (OperatingSystem.IsLinux())
            {
                var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH",
                    cacheDir + (string.IsNullOrEmpty(existing) ? "" : ":" + existing));
            }
            else if (OperatingSystem.IsMacOS())
            {
                var existing = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
                Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH",
                    cacheDir + (string.IsNullOrEmpty(existing) ? "" : ":" + existing));
            }
        }
    }
}
