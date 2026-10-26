using MSPChallenge_Client_Browser.Components;
using MSPChallenge_Client_Browser.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
if (builder.Environment.IsDevelopment())
{
    // Log levels are in appsettings.Development.json; this only makes each entry more informative.
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = false;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });

    // Allow plain HTTP and self-signed HTTPS certs when targeting a local game server.
    builder.Services.AddTransient<HttpTrafficLoggingHandler>();
    builder.Services.AddHttpClient(string.Empty)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
        .AddHttpMessageHandler<HttpTrafficLoggingHandler>();
}
else
{
    builder.Services.AddHttpClient();
}
builder.Services.AddScoped<UserSessionService>();
builder.Services.AddScoped<MspApiClient>();
builder.Services.AddScoped<WebSocketService>();
builder.Services.AddScoped<GameUIStateService>();
builder.Services.AddScoped<GameSessionService>();
builder.Services.AddBlazorBootstrap();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
        // Send full exception messages to the browser console instead of a generic error.
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
