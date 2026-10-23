using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.PortableExecutable;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Bundles;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.Builder;
using static PclCeRestore.AssemblyAudit;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PclCeRestore;

public static class Program
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var options = ParseOptions(args.Skip(1).ToArray());
            string Get(string key) => options.TryGetValue(key, out var value)
                ? value : throw new ArgumentException($"Missing --{key}.");
            object result = args[0] switch
            {
                "patch" => Patcher.Patch(Get("input"), Get("output"), Get("feed-base"),
                    int.Parse(Get("revision"))),
                "verify" => Patcher.Verify(Get("original"), Get("patched"), Get("feed-base"),
                    int.Parse(Get("revision"))),
                _ => throw new ArgumentException(Usage)
            };
            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (options.TryGetValue("report", out var report))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, json + "\n");
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception error)
        {
            // Do not dump IL, string heaps, configuration, or arbitrary exception messages from parsers.
            Console.Error.WriteLine(error is PatchException or ArgumentException or FormatException
                ? error.Message : $"Operation failed ({error.GetType().Name}). No output is approved for publication.");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException(Usage);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Invalid or repeated option.");
        }
        return result;
    }

    private const string Usage = "Commands: patch --input EXE --output EXE --feed-base URL --revision N --report JSON; " +
        "verify --original EXE --patched EXE --feed-base URL --revision N";
}

public sealed class PatchException(string message) : Exception(message);

public sealed record PatchReport(string OriginalSha256, string PatchedSha256, string Version, int VersionCode,
    string FeedBase, int Revision, string[] ChangedMethods, Dictionary<string, string> PreservedFiles);

public static class Patcher
{
    public const string MainAssembly = "Plain Craft Launcher 2.dll";
    public const string MetadataResource = "PCL.metadata.json";
    private static readonly string[] PolicyMethods = ["get_IsRestricted", "Check", "ThrowIfRestricted"];

    private sealed class Input
    {
        public byte[] Bytes { get; }
        public BundleManifest Bundle { get; }
        public BundleFile Main { get; }
        public ModuleDefinition Module { get; }

        public Input(string path)
        {
            Bytes = File.ReadAllBytes(path);
            using var host = new PEReader(new MemoryStream(Bytes));
            Require(host.PEHeaders.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.Amd64,
                "Only official x64 Windows executables are supported.");
            Bundle = BundleManifest.FromBytes(Bytes);
            Require(Bundle.MajorVersion == 6 && Bundle.MinorVersion == 0, "Unsupported bundle format; adaptation required.");
            Require(Bundle.Files.Select(file => file.RelativePath).Distinct().Count() == Bundle.Files.Count,
                "Duplicate bundle entries.");
            Main = Bundle.Files.Single(file => file.RelativePath == MainAssembly);
            Require(Bundle.Files.Count(file => file.RelativePath == "PCL.Core.dll") == 1, "Core library missing.");
            Module = ModuleDefinition.FromBytes(Main.GetData());
            Require(Module.IsILOnly && !Module.IsStrongNameSigned, "Unsupported signed or native managed assembly.");
            using var pe = new PEReader(new MemoryStream(Main.GetData()));
            Require(pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size == 0,
                "ReadyToRun assemblies require a separate compatibility review.");
            // Materialize lazy metadata before rewriting so output does not depend on access order.
            foreach (var method in Methods(Module).Values) _ = Fingerprint(method);
        }
    }

    public static string ValidateFeedBase(string feedBase)
    {
        Require(Uri.TryCreate(feedBase, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
            "Feed base must be an absolute HTTPS URL without credentials, query, or fragment.");
        return feedBase.TrimEnd('/') + "/";
    }

    private static Dictionary<string, MethodDefinition> TargetMethods(ModuleDefinition module)
    {
        var type = module.GetAllTypes().Single(type => type.FullName == "PCL.RestrictedFeaturePolicy");
        var result = PolicyMethods.ToDictionary(name => name, name => type.Methods.Single(method => method.Name == name));
        bool Signature(string name, string returnType, params string[] parameters) =>
            result[name].Signature?.ReturnType.FullName == returnType &&
            result[name].Signature!.ParameterTypes.Select(type => type.FullName).SequenceEqual(parameters);
        Require(Signature("get_IsRestricted", "System.Boolean") &&
            Signature("Check", "System.Boolean", "System.String", "System.Boolean") &&
            Signature("ThrowIfRestricted", "System.Void", "System.String"),
            "Policy signatures changed; adaptation required.");
        result.Add(".cctor", module.GetAllTypes().Single(type => type.FullName == "PCL.UpdateManager")
            .Methods.Single(method => method.Name == ".cctor"));
        Require(result.Values.All(method => method.IsStatic && method.CilMethodBody is not null), "Invalid patch targets.");
        return result;
    }

    private static void ApplyMethods(ModuleDefinition module, string feedBase)
    {
        var methods = TargetMethods(module);
        foreach (var (name, method) in methods.Where(pair => pair.Key != ".cctor"))
        {
            method.CilMethodBody = new CilMethodBody();
            if (name != "ThrowIfRestricted")
                method.CilMethodBody.Instructions.Add(name == "Check" ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
            method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        }

        var initializer = methods[".cctor"];
        var original = initializer.CilMethodBody!.Instructions;
        IMethodDescriptor FindMethod(string type, string name) => original
            .Select(instruction => instruction.Operand).OfType<IMethodDescriptor>()
            .First(method => method.DeclaringType?.FullName == type && method.Name == name);
        var listCtor = original.Select(instruction => instruction.Operand).OfType<IMethodDescriptor>()
            .First(method => method.Name == ".ctor" && method.DeclaringType!.FullName.StartsWith(
                "System.Collections.Generic.List`1<PCL.IUpdateSource>", StringComparison.Ordinal));
        var add = original.Select(instruction => instruction.Operand).OfType<IMethodDescriptor>()
            .First(method => method.Name == "Add" && method.DeclaringType!.FullName == listCtor.DeclaringType!.FullName);
        var sourceCtor = FindMethod("PCL.UpdatesMinioModel", ".ctor");
        var wrapperCtor = FindMethod("PCL.UpdatesWrapperModel", ".ctor");
        var field = original.Select(instruction => instruction.Operand).OfType<IFieldDescriptor>()
            .Single(field => field.FullName == "PCL.UpdatesWrapperModel PCL.UpdateManager::remoteServer");
        var storeIndex = original.ToList().FindIndex(instruction => ReferenceEquals(instruction.Operand, field));
        Require(storeIndex >= 0 && original.Take(storeIndex).All(instruction =>
                instruction.OpCode.Code != CilCode.Stsfld && instruction.Operand is not ICilLabel) &&
                initializer.CilMethodBody.ExceptionHandlers.Count == 0,
            "Updater initialization layout changed; adaptation required.");
        // Preserve all subsequent initialization, including the official serverLoader.
        var tail = original.Skip(storeIndex + 1).ToArray();
        var il = initializer.CilMethodBody.Instructions;
        il.Clear();
        il.Add(CilOpCodes.Newobj, listCtor);
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Ldstr, feedBase);
        il.Add(CilOpCodes.Ldstr, "PCL CE Restore (GitHub)");
        il.Add(CilOpCodes.Newobj, sourceCtor);
        il.Add(CilOpCodes.Callvirt, add);
        il.Add(CilOpCodes.Newobj, wrapperCtor);
        il.Add(CilOpCodes.Stsfld, field);
        il.AddRange(tail);
    }

    private static string ApplyMetadata(ModuleDefinition module, int revision)
    {
        var resource = module.Resources.Single(resource => resource.Name == MetadataResource);
        var metadata = JsonNode.Parse(resource.GetData()!)!.AsObject();
        var version = metadata["version"]!.AsObject();
        Require(string.IsNullOrEmpty(version["suffix"]!.GetValue<string>()), "Only official stable binaries are supported.");
        Require(revision > 0, "Revision must be a positive integer.");
        version["suffix"] = $"restored.{revision}";
        metadata["name"] = "Plain Craft Launcher (PCL) CE Restore / 第三方补丁版";
        resource.EmbeddedDataSegment = new DataSegment(JsonSerializer.SerializeToUtf8Bytes(metadata, Program.JsonOptions));
        return version["base"]!.GetValue<string>() + $"-restored.{revision}";
    }

    public static PatchReport Patch(string input, string output, string feedBase, int revision)
    {
        Require(!string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase),
            "Input and output must be different files.");
        feedBase = ValidateFeedBase(feedBase);
        var original = new Input(input);
        ApplyMethods(original.Module, feedBase);
        ApplyMetadata(original.Module, revision);
        var candidateBytes = SerializeBundle(original);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var candidate = output + ".candidate";
        try
        {
            File.WriteAllBytes(candidate, candidateBytes);
            var report = Verify(input, candidate, feedBase, revision);
            File.Move(candidate, output, true);
            return report;
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static byte[] SerializeBundle(Input original)
    {
        var diagnostics = new DiagnosticBag();
        var builder = new ManagedPEImageBuilder(MetadataBuilderFlags.PreserveAll | MetadataBuilderFlags.NoResourceDataDeduplication)
        {
            ErrorListener = diagnostics
        };
        var image = builder.CreateImage(original.Module);
        Require(!image.HasFailed && diagnostics.Exceptions.Count == 0, "Assembly writer reported errors; output rejected.");
        var pe = new ManagedPEFileBuilder().CreateFile(image.ConstructedImage!);
        using var stream = new MemoryStream();
        pe.Write(stream);
        original.Main.Contents = new DataSegment(stream.ToArray());
        original.Main.IsCompressed = false;
        original.Bundle.BundleID = original.Bundle.GenerateDeterministicBundleID();
        using var bundle = new MemoryStream();
        original.Bundle.WriteUsingTemplate(bundle, BundlerParameters.FromExistingBundle(original.Bytes, MainAssembly));
        return bundle.ToArray();
    }

    public static PatchReport Verify(string originalPath, string patchedPath, string feedBase, int revision)
    {
        feedBase = ValidateFeedBase(feedBase);
        var original = new Input(originalPath);
        var patched = new Input(patchedPath);
        Require(original.Bundle.BundleID != patched.Bundle.BundleID, "Bundle identity was not regenerated.");
        Require(original.Bundle.Flags == patched.Bundle.Flags && original.Bundle.Files.Count == patched.Bundle.Files.Count,
            "Bundle layout changed unexpectedly.");
        var preserved = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in original.Bundle.Files)
        {
            var other = patched.Bundle.Files.SingleOrDefault(item => item.RelativePath == file.RelativePath);
            Require(other is not null && other.Type == file.Type, "Bundle entry changed unexpectedly.");
            if (file.RelativePath == MainAssembly) continue;
            var hash = Sha256(file.GetData());
            Require(hash == Sha256(other!.GetData()), $"Bundled dependency changed: {file.RelativePath}");
            preserved.Add(file.RelativePath, hash);
        }

        // Independently reconstruct the only approved changes from the original.
        ApplyMethods(original.Module, feedBase);
        var version = ApplyMetadata(original.Module, revision);
        var expectedMethods = Methods(original.Module);
        var actualMethods = Methods(patched.Module);
        Require(expectedMethods.Count == actualMethods.Count, "Method inventory changed.");
        foreach (var (name, method) in expectedMethods)
            Require(actualMethods.TryGetValue(name, out var actual) && Fingerprint(method) == Fingerprint(actual),
                $"Unexpected method change: {name}. " + (actual is null ? "missing" : DescribeDifference(method, actual)));
        Require(original.Module.Resources.Count == patched.Module.Resources.Count, "Resource inventory changed.");
        foreach (var resource in original.Module.Resources)
        {
            var other = patched.Module.Resources.SingleOrDefault(item => item.Name == resource.Name);
            Require(other is not null && resource.Attributes == other.Attributes &&
                (resource.GetData() ?? []).SequenceEqual(other.GetData() ?? []), "Unexpected resource change.");
        }
        Require(original.Module.AssemblyReferences.Select(reference => reference.FullName).SequenceEqual(
            patched.Module.AssemblyReferences.Select(reference => reference.FullName)), "Assembly references changed.");
        Require(original.Module.Name == patched.Module.Name && original.Module.Assembly?.FullName == patched.Module.Assembly?.FullName,
            "Assembly identity changed.");
        // Also cover the native apphost, headers, field constants, and metadata not represented
        // by normalized CIL. Rebuilding from the official input is deterministic.
        Require(SerializeBundle(original).SequenceEqual(patched.Bytes),
            "Binary differs from the reproducible approved patch (including native host or metadata).");
        var versionCode = JsonNode.Parse(patched.Module.Resources.Single(resource => resource.Name == MetadataResource)
            .GetData()!)!["version"]!["code"]!.GetValue<int>();
        return new PatchReport(Sha256(original.Bytes), Sha256(patched.Bytes), version, versionCode, feedBase, revision,
            TargetMethods(original.Module).Values.Select(method => method.FullName).ToArray(), preserved.ToDictionary());
    }
}

public static class AssemblyAudit
{
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static Dictionary<string, MethodDefinition> Methods(ModuleDefinition module) =>
        module.GetAllTypes().SelectMany(type => type.Methods)
            .ToDictionary(method => method.FullName, StringComparer.Ordinal);

    public static string Fingerprint(MethodDefinition method) => Sha256(Encoding.UTF8.GetBytes(Normalize(method)));

    private static string Normalize(MethodDefinition method)
    {
        var body = method.CilMethodBody;
        if (body is null) return $"no-cil:{method.ImplAttributes}";
        body.Instructions.CalculateOffsets();
        var positions = body.Instructions.Select((instruction, index) => (instruction.Offset, index))
            .ToDictionary(item => item.Offset, item => item.index);
        string Label(ICilLabel? label) => label is null ? "null" :
            label.Offset == body.Instructions.Size ? "end" :
            positions.TryGetValue(label.Offset, out var index) ? index.ToString(CultureInfo.InvariantCulture) : "invalid";
        string Operand(object? operand) => operand switch
        {
            null => "",
            ICilLabel label => "label:" + Label(label),
            IEnumerable<ICilLabel> labels => "labels:" + string.Join(',', labels.Select(Label)),
            IMemberDescriptor member => "member:" + member.FullName,
            CilLocalVariable local => "local:" + body.LocalVariables.IndexOf(local),
            string text => "string:" + text.Length + ":" + text,
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => operand.ToString() ?? ""
        };
        var value = new StringBuilder();
        value.AppendLine($"{method.Attributes}:{method.ImplAttributes}:{body.InitializeLocals}");
        foreach (var local in body.LocalVariables) value.AppendLine("local:" + local.VariableType.FullName);
        foreach (var instruction in body.Instructions)
            value.AppendLine($"{instruction.OpCode.Code}:{Operand(instruction.Operand)}");
        // Assemblers may reorder independent protected regions. Catch precedence within
        // the same try region stays significant, so preserve that relative order.
        foreach (var handler in body.ExceptionHandlers.OrderBy(handler => handler.TryStart?.Offset)
                     .ThenBy(handler => handler.TryEnd?.Offset))
            value.AppendLine($"eh:{handler.HandlerType}:{handler.ExceptionType?.FullName}:" +
                $"{Label(handler.TryStart)}:{Label(handler.TryEnd)}:{Label(handler.HandlerStart)}:" +
                $"{Label(handler.HandlerEnd)}:{Label(handler.FilterStart)}");
        return value.ToString().Replace("\r\n", "\n");
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new PatchException(message);
    }

    public static string DescribeDifference(MethodDefinition expected, MethodDefinition actual)
    {
        var left = expected.CilMethodBody;
        var right = actual.CilMethodBody;
        if (left is null || right is null) return "CIL availability changed";
        var at = Normalize(expected).Split('\n').Zip(Normalize(actual).Split('\n'))
            .Select((pair, index) => (pair, index)).FirstOrDefault(item => item.pair.First != item.pair.Second);
        return $"locals-init {left.InitializeLocals}/{right.InitializeLocals}, " +
            $"locals {left.LocalVariables.Count}/{right.LocalVariables.Count}, " +
            $"instructions {left.Instructions.Count}/{right.Instructions.Count}, " +
            $"handlers {left.ExceptionHandlers.Count}/{right.ExceptionHandlers.Count}, " +
            $"normalized difference at line {at.index}: " +
            (at.pair.First?.Contains(":string:") == true || at.pair.Second?.Contains(":string:") == true
                ? "literal changed" : $"{at.pair.First}/{at.pair.Second}");
    }
}
