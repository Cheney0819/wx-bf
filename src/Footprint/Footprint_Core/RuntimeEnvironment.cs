using System.Reflection;
using System.Security.Cryptography;

namespace Footprint.Core.Runtime
{
    public sealed record CaptureRuntimeEnvironment(string Root, string PythonExecutable, string FridaHostScript,
        string AgentScript, string ProfilePath, string SqlCipherExecutable)
    {
        public IReadOnlyList<string> ProfilePaths => Directory
            .EnumerateFiles(Root, "weixin-*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public sealed class CaptureRuntimeBootstrapper
    {
        private const string PythonArchive = "python-3.13.0-embed-amd64.zip";
        private const string FridaArchive = "frida-17.9.9-cp37-abi3-win_amd64.whl";
        private const string PythonPathConfiguration = "python313.zip\n.\nLib\\site-packages\nimport site\n";

        private readonly Assembly _assembly;
        private readonly string _localAppDataRoot;
        private readonly CaptureRuntimeManifest _manifest;
        private readonly Func<IDisposable>? _criticalSectionLeaseFactory;
        private readonly Func<string, Mutex> _mutexFactory;

        public CaptureRuntimeBootstrapper(Assembly assembly, string? localAppDataRoot = null,
            CaptureRuntimeManifest? manifest = null)
            : this(assembly, localAppDataRoot, manifest, null)
        {
        }

        internal CaptureRuntimeBootstrapper(Assembly assembly, string? localAppDataRoot,
            CaptureRuntimeManifest? manifest, Func<IDisposable>? criticalSectionLeaseFactory,
            Func<string, Mutex>? mutexFactory = null)
        {
            _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            _localAppDataRoot = localAppDataRoot ??
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _manifest = manifest ?? CaptureRuntimeManifest.LoadEmbedded(assembly);
            _criticalSectionLeaseFactory = criticalSectionLeaseFactory;
            _mutexFactory = mutexFactory ?? (static name => new Mutex(false, name));
        }

        public Task<CaptureRuntimeEnvironment> EnsureAsync(CancellationToken cancellationToken = default)
        {
            _manifest.Validate();
            return Task.Run(() => EnsureWithCrossProcessLock(cancellationToken), CancellationToken.None);
        }

        private CaptureRuntimeEnvironment EnsureWithCrossProcessLock(CancellationToken cancellationToken)
        {
            try
            {
                using var mutex = _mutexFactory($"Footprint_CaptureRuntime_{_manifest.BundleSha256}");
                var acquired = false;
                try
                {
                    try
                    {
                        while (!acquired)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(100));
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    return EnsureLockedAsync(cancellationToken).GetAwaiter().GetResult();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CaptureRuntimeException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new CaptureRuntimeException("capture_runtime_lock_failed", "采集运行时跨进程锁失败。", error);
            }
        }

        private async Task<CaptureRuntimeEnvironment> EnsureLockedAsync(CancellationToken cancellationToken)
        {
            using var criticalSectionLease = _criticalSectionLeaseFactory?.Invoke();
            var parent = Path.Combine(_localAppDataRoot, "Footprint", "Footprint_Runtime");
            var finalRoot = Path.Combine(parent, _manifest.BundleSha256);
            try
            {
                Directory.CreateDirectory(parent);
                CleanupAbandonedBundleDirectories(parent);
                if (await IsVerifiedRuntimeAsync(finalRoot, cancellationToken)) return CreateEnvironment(finalRoot);

                var temporaryRoot = Path.Combine(parent,
                    $".tmp-{_manifest.BundleSha256}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporaryRoot);
                MakePrivate(temporaryRoot);
                try
                {
                    await ExtractResourcesAsync(temporaryRoot, cancellationToken);
                    await ExpandArchivesAsync(temporaryRoot, cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(temporaryRoot, "python", "python313._pth"),
                        PythonPathConfiguration, cancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(temporaryRoot, ".complete"),
                        _manifest.BundleSha256 + "\n", cancellationToken);
                    await VerifyRuntimeAsync(temporaryRoot, cancellationToken);
                    await PublishAsync(temporaryRoot, finalRoot, cancellationToken);
                    await VerifyRuntimeAsync(finalRoot, cancellationToken);
                    return CreateEnvironment(finalRoot);
                }
                finally
                {
                    DeletePathNoThrow(temporaryRoot);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CaptureRuntimeException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new CaptureRuntimeException("capture_runtime_extract_failed", "采集运行时释放失败。", error);
            }
        }

        private async Task ExtractResourcesAsync(string root, CancellationToken cancellationToken)
        {
            foreach (var resource in _manifest.Resources)
            {
                var destination = CaptureRuntimePayloadValidator.SafeDestination(root, resource.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = _assembly.GetManifestResourceStream(resource.ResourceName)
                    ?? throw new CaptureRuntimeException("capture_runtime_resource_missing", "采集运行时内嵌资源缺失。");
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                long length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();

                if (length != resource.Length)
                    throw new CaptureRuntimeException("capture_runtime_length_mismatch", "采集运行时资源长度校验失败。");
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualHash, resource.Sha256, StringComparison.Ordinal))
                    throw new CaptureRuntimeException("capture_runtime_hash_mismatch", "采集运行时资源哈希校验失败。");
                if (CaptureRuntimePayloadValidator.RequiresPeX64Verification(destination))
                    await CaptureRuntimePayloadValidator.VerifyPeX64Async(destination, cancellationToken);
            }
        }

        private static async Task ExpandArchivesAsync(string root, CancellationToken cancellationToken)
        {
            var pythonRoot = Path.Combine(root, "python");
            await CaptureRuntimePayloadValidator.ExtractArchiveAsync(
                Path.Combine(root, PythonArchive), pythonRoot, cancellationToken);
            await CaptureRuntimePayloadValidator.ExtractArchiveAsync(
                Path.Combine(root, FridaArchive), Path.Combine(pythonRoot, "Lib", "site-packages"), cancellationToken);
        }

        private async Task PublishAsync(string temporaryRoot, string finalRoot, CancellationToken cancellationToken)
        {
            string? invalidRoot = null;
            try
            {
                if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
                {
                    if (Directory.Exists(finalRoot) && await IsVerifiedRuntimeAsync(finalRoot, cancellationToken)) return;
                    invalidRoot = finalRoot + $".invalid-{Guid.NewGuid():N}";
                    if (Directory.Exists(finalRoot)) Directory.Move(finalRoot, invalidRoot);
                    else File.Move(finalRoot, invalidRoot);
                }
                Directory.Move(temporaryRoot, finalRoot);
            }
            catch (CaptureRuntimeException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new CaptureRuntimeException("capture_runtime_publish_failed", "采集运行时原子发布失败。", error);
            }
            finally
            {
                if (invalidRoot is not null) DeletePathNoThrow(invalidRoot);
            }
        }

        private async Task<bool> IsVerifiedRuntimeAsync(string root, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(root)) return false;
            try
            {
                await VerifyRuntimeAsync(root, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private async Task VerifyRuntimeAsync(string root, CancellationToken cancellationToken)
        {
            var actualLayout = CaptureRuntimePayloadValidator.SnapshotLayout(root);
            var marker = Path.Combine(root, ".complete");
            if (!File.Exists(marker) ||
                !string.Equals(await File.ReadAllTextAsync(marker, cancellationToken),
                    _manifest.BundleSha256 + "\n", StringComparison.Ordinal))
                throw new CaptureRuntimeException("capture_runtime_incomplete", "采集运行时目录不完整。");

            foreach (var resource in _manifest.Resources)
            {
                var path = CaptureRuntimePayloadValidator.SafeDestination(root, resource.FileName);
                await VerifyFileAsync(path, resource.Length, resource.Sha256, cancellationToken);
                if (CaptureRuntimePayloadValidator.RequiresPeX64Verification(path))
                    await CaptureRuntimePayloadValidator.VerifyPeX64Async(path, cancellationToken);
            }

            var pythonRoot = Path.Combine(root, "python");
            var sitePackages = Path.Combine(pythonRoot, "Lib", "site-packages");
            VerifyExactLayout(root, actualLayout, pythonRoot, sitePackages);
            await CaptureRuntimePayloadValidator.VerifyArchiveExtractionAsync(
                Path.Combine(root, PythonArchive), pythonRoot, "python313._pth", cancellationToken);
            await CaptureRuntimePayloadValidator.VerifyArchiveExtractionAsync(
                Path.Combine(root, FridaArchive), sitePackages, null, cancellationToken);
            var pathConfiguration = Path.Combine(pythonRoot, "python313._pth");
            if (!File.Exists(pathConfiguration) ||
                !string.Equals(await File.ReadAllTextAsync(pathConfiguration, cancellationToken),
                    PythonPathConfiguration, StringComparison.Ordinal))
                throw new CaptureRuntimeException("capture_runtime_hash_mismatch", "采集运行时资源哈希校验失败。");

            var environment = CreateEnvironment(root);
            foreach (var required in new[]
                     {
                         environment.PythonExecutable, environment.FridaHostScript, environment.AgentScript,
                         environment.SqlCipherExecutable
                     }.Concat(environment.ProfilePaths))
            {
                if (!File.Exists(required))
                    throw new CaptureRuntimeException("capture_runtime_resource_missing", "采集运行时内嵌资源缺失。");
            }
        }

        private void VerifyExactLayout(string root, CaptureRuntimeLayout actualLayout, string pythonRoot,
            string sitePackages)
        {
            var expectedFiles = new HashSet<string>(CaptureRuntimePayloadValidator.PathComparer);
            var expectedDirectories = new HashSet<string>(CaptureRuntimePayloadValidator.PathComparer);
            AddExpectedFile(Path.Combine(root, ".complete"));
            foreach (var resource in _manifest.Resources)
                AddExpectedFile(CaptureRuntimePayloadValidator.SafeDestination(root, resource.FileName));
            foreach (var item in CaptureRuntimePayloadValidator.GetArchivePaths(
                         Path.Combine(root, PythonArchive), pythonRoot))
                AddExpected(item.FullPath, item.IsDirectory);
            foreach (var item in CaptureRuntimePayloadValidator.GetArchivePaths(
                         Path.Combine(root, FridaArchive), sitePackages))
                AddExpected(item.FullPath, item.IsDirectory);
            AddExpectedFile(Path.Combine(pythonRoot, "python313._pth"));

            if (!actualLayout.Files.SetEquals(expectedFiles) ||
                !actualLayout.Directories.SetEquals(expectedDirectories))
                throw new CaptureRuntimeException("capture_runtime_layout_mismatch", "采集运行时目录包含未固定的文件或目录。");
            return;

            void AddExpected(string path, bool isDirectory)
            {
                if (isDirectory) AddExpectedDirectory(path);
                else AddExpectedFile(path);
            }

            void AddExpectedFile(string path)
            {
                expectedFiles.Add(CaptureRuntimePayloadValidator.NormalizeRelative(root, path));
                var parent = Path.GetDirectoryName(path);
                while (parent is not null && !CaptureRuntimePayloadValidator.PathComparer.Equals(
                           Path.GetFullPath(parent), Path.GetFullPath(root)))
                {
                    expectedDirectories.Add(CaptureRuntimePayloadValidator.NormalizeRelative(root, parent));
                    parent = Path.GetDirectoryName(parent);
                }
            }

            void AddExpectedDirectory(string path)
            {
                var current = path;
                while (!CaptureRuntimePayloadValidator.PathComparer.Equals(
                           Path.GetFullPath(current), Path.GetFullPath(root)))
                {
                    expectedDirectories.Add(CaptureRuntimePayloadValidator.NormalizeRelative(root, current));
                    current = Path.GetDirectoryName(current)
                              ?? throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
                }
            }
        }

        private static async Task VerifyFileAsync(string path, long length, string sha256,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
                throw new CaptureRuntimeException("capture_runtime_resource_missing", "采集运行时内嵌资源缺失。");
            if (new FileInfo(path).Length != length)
                throw new CaptureRuntimeException("capture_runtime_length_mismatch", "采集运行时资源长度校验失败。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
                throw new CaptureRuntimeException("capture_runtime_hash_mismatch", "采集运行时资源哈希校验失败。");
        }

        private void CleanupAbandonedBundleDirectories(string parent)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(parent,
                         $".tmp-{_manifest.BundleSha256}-*", SearchOption.TopDirectoryOnly))
                DeletePathNoThrow(path);
            foreach (var path in Directory.EnumerateFileSystemEntries(parent,
                         $"{_manifest.BundleSha256}.invalid-*", SearchOption.TopDirectoryOnly))
                DeletePathNoThrow(path);
        }

        private CaptureRuntimeEnvironment CreateEnvironment(string root) => new(root,
            Path.Combine(root, "python", "python.exe"), Path.Combine(root, "frida_host.py"),
            Path.Combine(root, "weixin-agent.js"), Path.Combine(root, "weixin-4914.json"),
            Path.Combine(root, "sqlcipher.exe"));

        private static void MakePrivate(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static void DeletePathNoThrow(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}

namespace Footprint.Core
{
    public sealed record RuntimeEnvironment(string Root, string PythonExecutable, string FridaHostScript,
        string AgentScript, string ProfilePath, string SqlCipherExecutable)
    {
        public IReadOnlyList<string> ProfilePaths => Directory
            .EnumerateFiles(Root, "weixin-*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public sealed class RuntimeBootstrapper(Assembly assembly)
    {
        public async Task<RuntimeEnvironment> EnsureAsync(CancellationToken cancellationToken)
        {
            var runtime = await new Runtime.CaptureRuntimeBootstrapper(assembly).EnsureAsync(cancellationToken);
            return new RuntimeEnvironment(runtime.Root, runtime.PythonExecutable, runtime.FridaHostScript,
                runtime.AgentScript, runtime.ProfilePath, runtime.SqlCipherExecutable);
        }
    }
}
