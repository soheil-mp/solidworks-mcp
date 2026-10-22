using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SolidWorksMcp.Core;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>Contract tests that need the build (SOLIDWORKS installed) but not a running SOLIDWORKS.</summary>
public class ContractTests
{
    [Fact]
    public void Tool_catalog_follows_the_conventions()
    {
        var problems = CatalogCheck.Problems(out int count, out int bytes);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(count >= 20, $"only {count} tools registered");
        Assert.True(bytes / count <= CatalogCheck.AverageToolBudget, $"tools/list is {bytes} bytes for {count} tools");
    }

    [Fact]
    public void EntityRef_schemas_are_compacted_and_documented_once()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();
        int refs = 0;
        foreach (var tool in ServerSetup.ListedTools(host.Services))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(tool.InputSchema.GetRawText())!;
            foreach (var o in Objects(node).Where(SchemaCompactor.IsEntityRef))
            {
                refs++;
                Assert.All(o["properties"]!.AsObject(), p => Assert.False(p.Value!.AsObject().ContainsKey("description"), $"{tool.Name}: EntityRef.{p.Key} kept its description"));
            }
            Assert.DoesNotContain("\"default\":null", tool.InputSchema.GetRawText());
        }
        Assert.True(refs >= 10, $"only {refs} EntityRef schemas found; did EntityRef change shape?");
        // The instructions explain every field instead.
        Assert.All(SchemaCompactor.EntityRefFields, f => Assert.Contains(f, Instructions.Text));
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonObject> Objects(System.Text.Json.Nodes.JsonNode? n)
    {
        if (n is System.Text.Json.Nodes.JsonObject o)
        {
            yield return o;
            foreach (var p in o) foreach (var c in Objects(p.Value)) yield return c;
        }
        else if (n is System.Text.Json.Nodes.JsonArray a)
            foreach (var v in a) foreach (var c in Objects(v)) yield return c;
    }

    [Fact]
    public void Server_instructions_fit_the_client_limit() =>
        Assert.InRange(Instructions.Text.Length, 500, 4096);

    /// <summary>The SolidWorks interface members Core actually uses (NoPIA embeds exactly those).</summary>
    private static IEnumerable<(Type Type, MethodInfo Member)> EmbeddedInteropMembers() =>
        typeof(SwSession).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttribute<TypeIdentifierAttribute>() is not null)
            .SelectMany(t => t.GetMethods().Select(m => (t, m)))
            .Where(x => !x.m.Name.StartsWith("_VtblGap", StringComparison.Ordinal));

    [Fact]
    public void Core_never_uses_banned_document_killers()
    {
        string[] banned = ["CloseAllDocuments", "ExitApp", "QuitDoc", "ClearUndoList", "ResetUntitledCount"];
        var used = EmbeddedInteropMembers().Where(x => banned.Contains(x.Member.Name)).Select(x => $"{x.Type.Name}.{x.Member.Name}").ToList();
        Assert.True(used.Count == 0, "Banned members used: " + string.Join(", ", used));
    }

    [Fact]
    public void Core_never_calls_in_process_I_members()
    {
        // 'I'-prefixed SolidWorks members (IGetFaces, IGetPersistReference3, ...) return a single element out of process.
        var allowed = new HashSet<string>(StringComparer.Ordinal) { };
        var used = EmbeddedInteropMembers()
            .Where(x => x.Member.Name.Length > 2 && x.Member.Name[0] == 'I' && char.IsUpper(x.Member.Name[1]) && !x.Member.Name.StartsWith("Is", StringComparison.Ordinal)
                        && !x.Member.IsSpecialName && !allowed.Contains(x.Member.Name))
            .Select(x => $"{x.Type.Name}.{x.Member.Name}")
            .Distinct().ToList();
        Assert.True(used.Count == 0, "In-process members used: " + string.Join(", ", used));
    }

    [Fact]
    public void Core_has_no_dynamic_call_sites() =>
        Assert.DoesNotContain(typeof(SwSession).Assembly.GetReferencedAssemblies(), a => a.Name == "Microsoft.CSharp");

    [Fact]
    public void Core_public_api_exposes_no_com_types()
    {
        // Embedded interop types are exported too (NoPIA), so only our own namespaces are checked.
        var leaks = typeof(SwSession).Assembly.GetExportedTypes()
            .Where(t => t.Namespace?.StartsWith("SolidWorksMcp", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (t, m, type: m switch
                {
                    PropertyInfo p => p.PropertyType,
                    MethodInfo mi => mi.ReturnType,
                    FieldInfo f => f.FieldType,
                    _ => null,
                })))
            .Where(x => x.type is not null && x.type.GetCustomAttribute<ComImportAttribute>() is not null)
            .Select(x => $"{x.t.Name}.{x.m.Name}: {x.type!.Name}")
            .ToList();
        Assert.True(leaks.Count == 0, "COM types in the public API: " + string.Join(", ", leaks));
    }
}
