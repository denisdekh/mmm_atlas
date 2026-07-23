using Microsoft.AspNetCore.DataProtection;
using MMMAtlas.Api;
using MMMAtlas.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();

var connectionString =
    builder.Configuration.GetConnectionString("MMMAtlas")
    ?? throw new InvalidOperationException(
        "Connection string 'MMMAtlas' is not configured.");

var database = DatabaseConnection.Create(
    connectionString,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(database);

var dataProtectionKeyDirectory = Path.Combine(
    builder.Environment.ContentRootPath,
    "App_Data",
    "DataProtection-Keys");

Directory.CreateDirectory(dataProtectionKeyDirectory);

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(
        new DirectoryInfo(dataProtectionKeyDirectory));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    await DevelopmentDatabase.InitializeAsync(database);
}

app.MapStaticAssets();
app.MapGameEndpoints();
app.MapControllers();
app.MapRazorPages()
    .WithStaticAssets();

app.Run();

public partial class Program;
