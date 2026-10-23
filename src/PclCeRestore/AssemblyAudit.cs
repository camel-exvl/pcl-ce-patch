using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace PclCeRestore;

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

    public static Dictionary<string, string> GuardedMethods(ModuleDefinition module)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in Methods(module).Values)
        {
            var type = method.DeclaringType?.FullName;
            var guarded = new[] { "PCL.RestrictedFeaturePolicy", "PCL.UpdateManager", "PCL.UpdatesMinioModel",
                "PCL.UpdatesWrapperModel" }.Any(root => type == root ||
                    (type?.StartsWith(root + "+", StringComparison.Ordinal) ?? false));
            guarded |= method.CilMethodBody?.Instructions.Any(instruction => instruction.Operand switch
            {
                IMemberDescriptor member => IsRegionReference(member.FullName),
                _ => false
            }) ?? false;
            if (guarded) result.Add(method.FullName, Fingerprint(method));
        }
        return result.ToDictionary();
    }

    private static bool IsRegionReference(string name) =>
        name.Contains("PCL.RestrictedFeaturePolicy", StringComparison.Ordinal) ||
        name.Contains("PCL.Core.App.Region.", StringComparison.Ordinal);

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
