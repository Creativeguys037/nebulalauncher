namespace NebulaLauncher.DocsCheck;

/// <summary>命令行入口：只做委托，逻辑都在纯类里。</summary>
internal static class Program
{
    private static int Main(string[] args)
        => DocCheckCommand.Execute(args, Directory.GetCurrentDirectory(), Console.Out);
}
