using Microsoft.EntityFrameworkCore;
using NewsBriefingAssistant;
using NewsBriefingAssistant.Accessors;
using NewsBriefingAssistant.DatabaseContracts;
using NewsBriefingAssistant.Engines;
using NewsBriefingAssistant.Managers;
using NewsBriefingAssistant.Utilities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// WebPageAccessor 注册为 Singleton（Playwright 浏览器实例复用）
builder.Services.AddSingleton<WebPageAccessor>(sp =>
{
    var handler = new HttpClientHandler
    {
        // 不走系统代理，避免本地代理（如 Clash）拦截直连请求
        UseProxy = false,
        // 允许所有 TLS 版本，兼容老旧服务器
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | 
                       System.Security.Authentication.SslProtocols.Tls13,
        // 跳过证书验证（某些网站自签或过期证书）
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };
    var httpClient = new HttpClient(handler);
    httpClient.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
    httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
    httpClient.Timeout = TimeSpan.FromSeconds(20);
    var logger = sp.GetRequiredService<ILogger<WebPageAccessor>>();
    return new WebPageAccessor(httpClient, logger);
});

builder.Services.Configure<DeepSeekOptions>(
    builder.Configuration.GetSection("DeepSeek"));
builder.Services.Configure<SupportedSourcesOptions>(
    builder.Configuration.GetSection("SupportedNewsSources"));

builder.Services.AddHttpClient<DeepSeekAccessor>().ConfigurePrimaryHttpMessageHandler(() =>
    new SocketsHttpHandler
    {
        UseProxy = false,   // 不走系统代理，避免本地代理（如 Clash）拦截直连请求
        // 固定 IP 解决 DeepSeek CDN 节点不稳定问题
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            await socket.ConnectAsync(
                new System.Net.IPEndPoint(System.Net.IPAddress.Parse("117.163.60.130"), 443),
                cancellationToken);
            socket.NoDelay = true;
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
    });

builder.Services.AddSingleton<NewsSourceEngine>();
builder.Services.AddSingleton<AgentEngine>();
builder.Services.AddSingleton<NewsBriefingManager>();
builder.Services.AddScoped<UserContextService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.Run();
