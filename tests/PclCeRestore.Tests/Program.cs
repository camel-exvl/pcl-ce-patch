using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Text.Json;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Bundles;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.Builder;
using AsmResolver.PE.DotNet.Cil;
using PclCeRestore;

// Integration tests use real, separately verified official assets. Nothing starts the launcher,
// signs in, modifies Windows region settings, or reads the service-configuration library as IL.
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: Tests ORIGINAL.exe PROFILE.json WORKSPACE_TEMP FEED_BASE");
    return 2;
}
var originalPath = Path.GetFullPath(args[0]);
var profile = Path.GetFullPath(args[1]);
var root = Path.Combine(Path.GetFullPath(args[2]), "integration-" + Guid.NewGuid().ToString("N"));
var feedBase = args[3];
Directory.CreateDirectory(root);
var output = Path.Combine(root, "patched.exe");
var passed = 0;
void Test(string name, Action action)
{
    action();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Reject(Action action)
{
    try { action(); }
    catch (PatchException) { return; }
    throw new Exception("Expected a rejection.");
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }

try
{
    Test("integration host is native x64", () =>
    {
        Assert(RuntimeInformation.ProcessArchitecture == Architecture.X64);
    });
    Test("patch official binary and verify every untouched dependency and method", () =>
    {
        var report = Patcher.Patch(originalPath, output, feedBase, 1, profile);
        Assert(report.ChangedMethods.Length == 4 && report.PreservedFiles.ContainsKey("PCL.Core.dll"));
        Patcher.Verify(originalPath, output, feedBase, 1, profile);
    });
    Test("execute restored policy without region reads, UI hints, or cancellation", () =>
    {
        var bundle = BundleManifest.FromFile(output);
        var context = new AssemblyLoadContext("restored-policy-tests", isCollectible: true);
        context.Resolving += (_, name) =>
        {
            var file = bundle.Files.SingleOrDefault(file => file.RelativePath == name.Name + ".dll");
            return file is null ? null : context.LoadFromStream(new MemoryStream(file.GetData()));
        };
        var assembly = context.LoadFromStream(new MemoryStream(bundle.Files.Single(
            file => file.RelativePath == Patcher.MainAssembly).GetData()));
        var type = assembly.GetType("PCL.RestrictedFeaturePolicy", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Assert((bool)type.GetProperty("IsRestricted", flags)!.GetValue(null)! == false);
        foreach (var feature in new[] { "minecraft-install", "minecraft-repair", "minecraft-reset",
                     "modpack-install", "online-modpack-install", "minecraft-client-save", "minecraft-loader-install",
                     "auto-modpack-install", "minecraft-launch-missing-files", "future-feature" })
        {
            foreach (var showHint in new[] { false, true })
                Assert((bool)type.GetMethod("Check", flags)!.Invoke(null, [feature, showHint])!);
            type.GetMethod("ThrowIfRestricted", flags)!.Invoke(null, [feature]);
        }
        context.Unload();
    });
    Test("repeated patching produces identical bytes", () =>
    {
        var second = Path.Combine(root, "second.exe");
        Patcher.Patch(originalPath, second, feedBase, 1, profile);
        Assert(File.ReadAllBytes(second).SequenceEqual(File.ReadAllBytes(output)));
    });
    Test("different feed, revision, and original output are rejected", () =>
    {
        Reject(() => Patcher.Verify(originalPath, output, "https://example.invalid/", 1, profile));
        Reject(() => Patcher.Verify(originalPath, output, feedBase, 2, profile));
        Reject(() => Patcher.Patch(originalPath, originalPath, feedBase, 1, profile));
        Reject(() => Patcher.Patch(originalPath, output, "http://example.invalid/", 1, profile));
        Reject(() => Patcher.Patch(originalPath, output, feedBase, 0, profile));
    });
    Test("unknown upstream method fingerprint is rejected", () =>
    {
        var data = JsonSerializer.Deserialize<CompatibilityProfile>(File.ReadAllText(profile), PclCeRestore.Program.JsonOptions)!;
        data.GuardedMethods[data.GuardedMethods.Keys.First()] = new string('0', 64);
        var changed = Path.Combine(root, "unknown-profile.json");
        File.WriteAllText(changed, JsonSerializer.Serialize(data, PclCeRestore.Program.JsonOptions));
        Reject(() => Patcher.Patch(originalPath, output, feedBase, 1, changed));
    });
    void Repackage(string path, Action<BundleManifest> change)
    {
        var bytes = File.ReadAllBytes(output);
        var bundle = BundleManifest.FromBytes(bytes);
        change(bundle);
        bundle.BundleID = bundle.GenerateDeterministicBundleID();
        bundle.WriteUsingTemplate(path, BundlerParameters.FromExistingBundle(bytes, Patcher.MainAssembly));
    }
    Test("tampered opaque core library is rejected", () =>
    {
        var changed = Path.Combine(root, "tampered-core.exe");
        Repackage(changed, bundle =>
        {
            var core = bundle.Files.Single(file => file.RelativePath == "PCL.Core.dll");
            var bytes = core.GetData();
            bytes[0] ^= 1;
            core.Contents = new DataSegment(bytes);
        });
        Reject(() => Patcher.Verify(originalPath, changed, feedBase, 1, profile));
    });
    Test("unapproved method change is rejected", () =>
    {
        var changed = Path.Combine(root, "tampered-method.exe");
        Repackage(changed, bundle =>
        {
            var main = bundle.Files.Single(file => file.RelativePath == Patcher.MainAssembly);
            var module = ModuleDefinition.FromBytes(main.GetData());
            var method = module.GetAllTypes().Single(type => type.FullName == "PCL.RestrictedFeaturePolicy")
                .Methods.Single(method => method.Name == "Assess");
            method.CilMethodBody = new CilMethodBody();
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ldnull);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            var image = new ManagedPEImageBuilder(MetadataBuilderFlags.PreserveAll).CreateImage(module);
            using var stream = new MemoryStream();
            new ManagedPEFileBuilder().CreateFile(image.ConstructedImage!).Write(stream);
            main.Contents = new DataSegment(stream.ToArray());
        });
        Reject(() => Patcher.Verify(originalPath, changed, feedBase, 1, profile));
    });
    Test("native apphost header tampering is rejected", () =>
    {
        var bytes = File.ReadAllBytes(output);
        var offset = BitConverter.ToInt32(bytes, 0x3c);
        bytes[offset + 8] ^= 1; // Native COFF timestamp, leaving all bundled assemblies intact.
        var changed = Path.Combine(root, "tampered-host.exe");
        File.WriteAllBytes(changed, bytes);
        Reject(() => Patcher.Verify(originalPath, changed, feedBase, 1, profile));
    });
    Console.WriteLine($"Passed {passed} integration scenarios.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL after {passed} scenarios: {error.GetType().Name}: {error.Message}");
    return 1;
}
finally
{
    // Only remove the directory created above, contained in the caller-supplied workspace temp.
    Assert(Path.GetFullPath(root).StartsWith(Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
