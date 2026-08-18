var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version }));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapMethods("/api/{**rest}", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "PATCH" }, () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { } // exposed for WebApplicationFactory
