using IsSeattleFull.Parcels;

if (args.FirstOrDefault() == "export")
{
    Environment.ExitCode = await ParcelExportCommand.RunAsync(args[1..]);
    return;
}
if (args.FirstOrDefault() == "catalog")
{
    Environment.ExitCode = await AerialCatalogCommand.RunAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

var exportDirectory = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath,
    app.Configuration["ParcelMap:ExportDirectory"] ?? "exports"));

app.MapGet("/api/parcels", (HttpContext context) =>
{
    var path = Path.Combine(exportDirectory, "empty-parcels.geojson");
    if (!File.Exists(path))
    {
        app.Logger.LogWarning("Parcel map requested before the export exists at {ExportPath}", path);
        return Results.Problem(
            title: "Parcel export not found",
            detail: "Run 'dotnet run -- export' first, or configure ParcelMap:ExportDirectory to your existing export directory.",
            statusCode: StatusCodes.Status404NotFound);
    }

    context.Response.Headers.CacheControl = "no-cache";
    return Results.File(path, "application/geo+json", enableRangeProcessing: true);
})
.WithName("GetParcelBoundaries");

app.MapGet("/api/aerial-groups", (HttpContext context) =>
{
    var path = Path.Combine(exportDirectory, "aerial-catalog.json");
    if (!File.Exists(path))
    {
        app.Logger.LogWarning("Aerial catalog requested before it exists at {ExportPath}", path);
        return Results.Problem(title: "Aerial catalog not found",
            detail: "Run 'dotnet run -- catalog' after exporting parcels, or configure ParcelMap:ExportDirectory.",
            statusCode: StatusCodes.Status404NotFound);
    }
    context.Response.Headers.CacheControl = "no-cache";
    return Results.File(path, "application/json", enableRangeProcessing: true);
})
.WithName("GetAerialCatalog");

var aerialBoundaries = new AerialBoundaryStore(Path.Combine(exportDirectory, "aerial-boundaries.json"));
app.MapGet("/api/aerial-groups/{id}/parcels", (string id, HttpContext context) =>
{
    try
    {
        var (revision, geometry) = aerialBoundaries.Read(id);
        if (geometry is null) return Results.NotFound();
        if (context.Request.Query.TryGetValue("revision", out var requested) && requested != revision)
            return Results.Problem(title: "Aerial catalog changed",
                detail: "Reload the page to use the current parcel-group catalog.",
                statusCode: StatusCodes.Status409Conflict);
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Json(geometry.Value, contentType: "application/geo+json");
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
    {
        app.Logger.LogError(ex, "Unable to serve aerial boundaries for group {GroupId}", id);
        return Results.Problem(title: "Aerial boundaries unavailable",
            detail: "Run 'dotnet run -- catalog' to prepare a complete aerial export.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetAerialGroupParcels");

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
