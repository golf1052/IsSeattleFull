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

var exportDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["ParcelMap:ExportDirectory"] ?? "exports"));
if (builder.Configuration.GetValue<bool>("ParcelReview:Enabled"))
{
    builder.Services.AddSingleton(new LocalParcelReview(Path.Combine(builder.Environment.ContentRootPath, "reviewed-exclusions.json")));
    builder.Services.AddSingleton(services => new ParcelReviewExport(
        services.GetRequiredService<LocalParcelReview>(), exportDirectory,
        (staging, progress, token) => ParcelReviewExport.GenerateAsync(builder.Environment.ContentRootPath, staging, progress, token),
        services.GetRequiredService<ILogger<ParcelReviewExport>>()));
    builder.Services.AddHostedService(services => services.GetRequiredService<ParcelReviewExport>());
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

var reviewExports = app.Services.GetService<ParcelReviewExport>();
if (reviewExports is not null)
{
    app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        if (context.Request.Path == "/api/parcels" || context.Request.Path.StartsWithSegments("/api/aerial-groups"))
            await reviewExports.ServeAsync(context, next);
        else await next(context);
    });
}

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

if (app.Configuration.GetValue<bool>("ParcelReview:Enabled"))
{
    var review = app.Services.GetRequiredService<LocalParcelReview>();
    var exports = app.Services.GetRequiredService<ParcelReviewExport>();
    app.MapGet("/api/reviewed-exclusions", async (HttpContext context) =>
    {
        if (!LocalParcelReview.IsLocalRequest(context)) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var entries = await review.ReadAsync(context.RequestAborted);
            return Results.Json(entries.Values.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            app.Logger.LogError(ex, "Unable to read local reviewed exclusions");
            return Results.Problem(title: "Reviewed exclusions unavailable",
                detail: "Check reviewed-exclusions.json and reload the page. No exclusions were changed.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).WithName("GetReviewedExclusions");

    app.MapPost("/api/reviewed-exclusions", async (ParcelReviewRequest request, HttpContext context) =>
    {
        if (!LocalParcelReview.IsLocalRequest(context)) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        if (!LocalParcelReview.HasSameOrigin(context))
        {
            app.Logger.LogWarning("Rejected a local parcel review request with a mismatched origin");
            return Results.Problem(title: "Review request rejected",
                detail: "Save exclusions from this app's local map page.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            var status = await exports.SaveAsync(request, context.RequestAborted);
            app.Logger.LogInformation("Saved {Count} local parcel exclusions and queued an export", status.Saved);
            return Results.Accepted("/api/reviewed-exclusions/export", status);
        }
        catch (ArgumentException ex)
        {
            app.Logger.LogWarning(ex, "Invalid local parcel review request");
            return Results.Problem(title: "Invalid exclusions", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            app.Logger.LogWarning(ex, "Local parcel review conflicts with existing exclusions");
            return Results.Problem(title: "Exclusions not saved", detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            app.Logger.LogError(ex, "Unable to save local reviewed exclusions");
            return Results.Problem(title: "Exclusions not saved",
                detail: "Could not update reviewed-exclusions.json. Check the file and local server logs before retrying.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }).WithName("SaveReviewedExclusions");

    app.MapGet("/api/reviewed-exclusions/export", (HttpContext context) =>
    {
        if (!LocalParcelReview.IsLocalRequest(context)) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(exports.Status);
    }).WithName("GetReviewExportStatus");

    app.MapPost("/api/reviewed-exclusions/export", async (HttpContext context) =>
    {
        if (!LocalParcelReview.IsLocalRequest(context)) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        if (!LocalParcelReview.HasSameOrigin(context))
        {
            app.Logger.LogWarning("Rejected a local export request with a mismatched origin");
            return Results.Problem(title: "Export request rejected", detail: "Retry from this app's local map page.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            return Results.Accepted("/api/reviewed-exclusions/export", await exports.RetryAsync(context.RequestAborted));
        }
        catch (InvalidOperationException ex)
        {
            app.Logger.LogWarning(ex, "Local export request conflicts with the current job");
            return Results.Problem(title: "Export not started", detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }).WithName("RetryReviewExport");
}

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
