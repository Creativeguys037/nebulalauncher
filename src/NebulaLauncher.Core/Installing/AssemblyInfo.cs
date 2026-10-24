using System.Runtime.CompilerServices;

// RepairAsync 的判据（「sha1 不符才重下」「0 字节才重下」）需要可控网络才能稳定复现，
// 而 DownloadHttpClientFactory 在构造时就绑定了真实的 MirrorRewritingHandler。
// 只给测试程序集开放那个「直接注入 HttpClient」的内部构造，不扩大公开 API。
[assembly: InternalsVisibleTo("NebulaLauncher.Tests")]
