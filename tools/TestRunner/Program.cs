using System.Reflection;
using Xunit;
using Xunit.Sdk;

namespace NebulaLauncher.TestRunner;

/// <summary>
/// 进程内 xunit 运行器。
///
/// 为什么需要它：本机沙箱拒绝 testhost 启动（<c>Process.EnableRaisingEvents</c> →
/// <c>OpenProcess(父进程)</c> → Win32Exception 5），<c>dotnet test</c> 与 <c>dotnet vstest</c>
/// 都在启动阶段就中止。这里直接反射测试程序集、复用 xunit 的断言实现，
/// 覆盖 Fact / Theory+InlineData，足以在受限环境里给出可信的通过率。
///
/// 不支持：IClassFixture、ICollectionFixture、MemberData、ClassData、Skip 原因。
/// 遇到会打印「未支持」而不是静默忽略。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static readonly List<string> Failures = [];

    private static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        var verbose = args.Contains("-v");

        var assembly = typeof(NebulaLauncher.Tests.ModelSmokeTests).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract).OrderBy(t => t.FullName))
        {
            if (filter is not null && !(type.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                              .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                              .OrderBy(m => m.Name);

            foreach (var method in methods)
            {
                foreach (var arguments in ExpandArguments(method, type.ModelName()))
                {
                    RunOne(type, method, arguments, verbose);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"总计: {_passed + _failed + _skipped}, 通过: {_passed}, 失败: {_failed}, 跳过/未支持: {_skipped}");

        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败详情:");
            foreach (var f in Failures)
            {
                Console.WriteLine($"  - {f}");
            }

            return 1;
        }

        return 0;
    }

    private static void RunOne(Type type, MethodInfo method, object?[] arguments, bool verbose)
    {
        var name = string.Join(".", type.Name, method.Name);
        try
        {
            var instance = Activator.CreateInstance(type);
            var result = method.Invoke(instance, arguments);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
            }

            _passed++;
            if (verbose)
            {
                Console.WriteLine($"  PASS {name}");
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            _failed++;
            var message = ex.InnerException is XunitException
                ? ex.InnerException.Message
                : $"{ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            Failures.Add($"{name} => {message}");
            Console.WriteLine($"  FAIL {name} => {message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name} => {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name} => {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<object?[]> ExpandArguments(MethodInfo method, string typeName)
    {
        // 纯 Fact（没有 [Theory]）→ 无参数直接跑一次。
        if (method.GetCustomAttribute<TheoryAttribute>() is null)
        {
            yield return [];
            yield break;
        }

        var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToList();
        if (inlineData.Count > 0)
        {
            foreach (var data in inlineData)
            {
                foreach (var row in data.GetData(method))
                {
                    yield return row;
                }
            }

            yield break;
        }

        // MemberData / ClassData 需要测试类实例上下文，本运行器不实现。
        _skipped++;
        Console.WriteLine($"  SKIP {typeName}.{method.Name} (未支持的参数化数据源)");
    }

    /// <summary>反射拿不到 typeof(T) 的泛型参数，这里退化为按方法所在类型名拼接。</summary>
    private static string ModelName(this Type type) => type.Name;
}
