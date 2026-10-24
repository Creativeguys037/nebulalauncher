using System.Reflection;
using System.Text;

var assemblyNames = new[]
{
    "CmlLib.Core",
    "CmlLib.Core.Commons",
    "CmlLib.Core.Auth.Microsoft",
    "CmlLib.Core.Installer.Forge",
    "CmlLib.Core.Installer.NeoForge",
    "XboxAuthNet.Game",
};

var sb = new StringBuilder();
foreach (var name in assemblyNames)
{
    Assembly asm;
    try { asm = Assembly.Load(name); }
    catch (Exception ex) { sb.AppendLine($"### {name} LOAD FAILED: {ex.Message}"); continue; }

    sb.AppendLine($"### {name} {asm.GetName().Version}");

    foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
    {
        if (t.IsEnum)
        {
            sb.AppendLine($"enum {t.FullName} : {string.Join(",", Enum.GetNames(t))}");
            continue;
        }

        sb.AppendLine($"type {t.FullName} : {t.BaseType?.Name}");

        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            sb.AppendLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.HasDefaultValue ? "=" + (p.DefaultValue ?? "null") : "")}"))})");

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            sb.AppendLine($"  prop {p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get;" : "")}{(p.CanWrite ? "set;" : "")} }}");

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            sb.AppendLine($"  method {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }
}

File.WriteAllText(args.Length > 0 ? args[0] : "api.txt", sb.ToString());
Console.WriteLine($"written {sb.Length} chars");
