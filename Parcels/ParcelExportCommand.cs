using System.Text.Json;
using NetTopologySuite.Geometries;

namespace IsSeattleFull.Parcels;

public static class ParcelExportCommand
{
    private static readonly HashSet<string> PublicOwnerTypes =
    [
        "SCHOOL", "CITY", "COUNTY", "STATE", "FEDERAL", "PORT",
        "REGIONAL TRANSIT AUTHORITY", "VASHON PARK DIST"
    ];
    private sealed record PublicOwner(string OwnerType, string? OwnerName);
    private sealed record NeighborException(string OriginalExclusion, string[] OverriddenExclusions, string[] NeighborPins);

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var dataDirectory = Path.GetFullPath("data");
            var outputDirectory = Path.GetFullPath("exports");
            string? bulkDirectory = null;
            var refresh = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        Console.WriteLine("dotnet run -- export [--data-dir PATH] [--output-dir PATH] [--bulk-dir PATH] [--refresh]");
                        Console.WriteLine("Use the latest local assessor bulk snapshot; reuse downloaded GIS masks and processed bulk data.");
                        return 0;
                    case "--refresh":
                        refresh = true;
                        break;
                    case "--data-dir":
                    case "--output-dir":
                    case "--bulk-dir":
                        var option = args[i];
                        if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"{option} requires a path.");
                        }
                        if (option == "--data-dir")
                        {
                            dataDirectory = Path.GetFullPath(args[i]);
                        }
                        else if (option == "--output-dir")
                        {
                            outputDirectory = Path.GetFullPath(args[i]);
                        }
                        else
                        {
                            bulkDirectory = Path.GetFullPath(args[i]);
                        }
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'. Use export --help.");
                }
            }

            var reviewedExclusions = await ReviewedParcelExclusion.LoadAsync(
                Path.GetFullPath("reviewed-exclusions.json"), cancellation.Token);
            bulkDirectory = AssessorBulkLoader.ResolveDirectory(dataDirectory, bulkDirectory);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await ExportAsync(new DatasetCache(http, Console.Out), dataDirectory, outputDirectory,
                refresh, cancellation.Token, reviewedExclusions, bulkDirectory);
            await AerialCatalog.BuildAsync(new DatasetCache(http, Console.Out), dataDirectory, outputDirectory,
                refresh, Console.Out, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Export canceled. Completed snapshots remain cached; unfinished downloads were removed.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or HttpRequestException or
            JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException or TopologyException)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    public static async Task ExportAsync(
        DatasetCache cache, string dataDirectory, string outputDirectory, bool refresh,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ReviewedParcelExclusion>? reviewedExclusions = null,
        string? bulkDirectory = null)
    {
        using var parcels = await cache.LoadAsync(DatasetCache.Parcels, dataDirectory, refresh, cancellationToken);
        using var propertyNames = await cache.LoadAsync(DatasetCache.PropertyNames, dataDirectory, refresh, cancellationToken);
        using var fullPropertyNames = await cache.LoadAsync(DatasetCache.FullPropertyNames, dataDirectory, refresh, cancellationToken);
        using var outlines = await cache.LoadAsync(DatasetCache.ParkOutlines, dataDirectory, refresh, cancellationToken);
        using var parks = await cache.LoadAsync(DatasetCache.Parks, dataDirectory, refresh, cancellationToken);
        using var otherParks = await cache.LoadAsync(DatasetCache.OtherParks, dataDirectory, refresh, cancellationToken);
        using var common = await cache.LoadAsync(DatasetCache.CommonProperty, dataDirectory, refresh, cancellationToken);
        using var topography = await cache.LoadAsync(DatasetCache.Topography, dataDirectory, refresh, cancellationToken);
        using var waterProblems = await cache.LoadAsync(DatasetCache.WaterProblems, dataDirectory, refresh, cancellationToken);
        using var restrictiveSizeShape = await cache.LoadAsync(DatasetCache.RestrictiveSizeShape, dataDirectory, refresh, cancellationToken);
        using var tidelandShoreland = await cache.LoadAsync(DatasetCache.TidelandShorelandOnly, dataDirectory, refresh, cancellationToken);
        using var publicOwnership = await cache.LoadAsync(DatasetCache.PublicOwned, dataDirectory, refresh, cancellationToken);
        var publicOwners = new Dictionary<string, HashSet<PublicOwner>>(StringComparer.Ordinal);
        foreach (var feature in publicOwnership.Features.EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            var ownerType = ParcelRecord.Text(properties, "TYPE_")?.ToUpperInvariant();
            if (ownerType is null || !PublicOwnerTypes.Contains(ownerType))
            {
                throw new InvalidDataException($"Unknown public ownership classification: {ownerType ?? "(missing)"}.");
            }
            var pin = ParcelRecord.Text(properties, "PIN")
                ?? throw new InvalidDataException("Public-ownership record has no PIN.");
            if (!publicOwners.TryGetValue(pin, out var owners))
            {
                publicOwners[pin] = owners = [];
            }
            owners.Add(new(ownerType, ParcelRecord.Text(properties, "KCTP_NAME")));
        }
        var topographyPins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in topography?.Features.EnumerateArray().AsEnumerable() ?? [])
        {
            var properties = feature.GetProperty("properties");
            if (properties.GetProperty("TOPOGRAPHY").GetInt32() != 1)
            {
                throw new InvalidDataException("The topography supplement must contain only TOPOGRAPHY = 1 records.");
            }
            topographyPins.Add(ParcelRecord.Text(properties, "PIN")
                ?? throw new InvalidDataException("Topography record has no PIN."));
        }
        var waterProblemPins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in waterProblems?.Features.EnumerateArray().AsEnumerable() ?? [])
        {
            var properties = feature.GetProperty("properties");
            if (ParcelRecord.Text(properties, "WATERPROBLEMS") != "Y")
            {
                throw new InvalidDataException("The water-problems supplement must contain only WATERPROBLEMS = Y records.");
            }
            waterProblemPins.Add(ParcelRecord.Text(properties, "PIN")
                ?? throw new InvalidDataException("Water-problems record has no PIN."));
        }
        var restrictiveSizeShapePins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in restrictiveSizeShape?.Features.EnumerateArray().AsEnumerable() ?? [])
        {
            var properties = feature.GetProperty("properties");
            if (properties.GetProperty("RESTRICTIVESZSHAPE").GetInt32() != 1)
            {
                throw new InvalidDataException("The size/shape supplement must contain only RESTRICTIVESZSHAPE = 1 records.");
            }
            restrictiveSizeShapePins.Add(ParcelRecord.Text(properties, "PIN")
                ?? throw new InvalidDataException("Size/shape record has no PIN."));
        }
        var tidelandShorelandPins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in tidelandShoreland?.Features.EnumerateArray().AsEnumerable() ?? [])
        {
            var properties = feature.GetProperty("properties");
            if (properties.GetProperty("TIDELANDSHORELAND").GetInt32() != 3)
            {
                throw new InvalidDataException("The tideland/shoreland supplement must contain only TIDELANDSHORELAND = 3 records.");
            }
            tidelandShorelandPins.Add(ParcelRecord.Text(properties, "PIN")
                ?? throw new InvalidDataException("Tideland/shoreland record has no PIN."));
        }
        var parkIndex = new ParkIndex(outlines, parks, otherParks);
        foreach (var repair in parkIndex.Repairs)
        {
            Console.Error.WriteLine(
                $"WARNING: Resolved park boundary issue in memory: {repair.ParkName} ({repair.SourceFile}, OBJECTID {repair.ObjectId}): {repair.ValidationError}; {repair.Resolution}");
        }
        var commonPins = common.Features.EnumerateArray()
            .Select(feature => ParcelRecord.Text(feature.GetProperty("properties"), "PIN"))
            .Where(pin => pin is not null).ToHashSet(StringComparer.Ordinal);
        var groups = parcels.Features.EnumerateArray().Select(ParcelRecord.Read)
            .GroupBy(row => row.Pin).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var rowsByPin = groups.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var originalRowsByPin = rowsByPin;
        var bulk = bulkDirectory is not null
            ? await AssessorBulkLoader.LoadAsync(bulkDirectory, rowsByPin.Keys.ToHashSet(StringComparer.Ordinal),
                Console.Out, cancellationToken)
            : null;
        var assessmentsByPin = bulk is not null
            ? originalRowsByPin.ToDictionary(entry => entry.Key,
                entry => bulk.SelectAssessment(entry.Key, entry.Value), StringComparer.Ordinal)
            : null;
        if (bulk is not null)
        {
            rowsByPin = rowsByPin.ToDictionary(entry => entry.Key,
                entry => entry.Value.Select(row => bulk.Enrich(row, assessmentsByPin![entry.Key])).ToArray(), StringComparer.Ordinal);
            foreach (var (pin, data) in bulk.Parcels)
            {
                if (data.Parcel?.Topography is not null) topographyPins.Remove(pin);
                if (data.Parcel?.WaterProblems is not null) waterProblemPins.Remove(pin);
                if (data.Parcel?.RestrictiveSizeShape is not null) restrictiveSizeShapePins.Remove(pin);
                if (data.Parcel?.TidelandShoreland is not null) tidelandShorelandPins.Remove(pin);
                if (data.Parcel?.Topography == 1) topographyPins.Add(pin);
                if (data.Parcel?.WaterProblems == "Y") waterProblemPins.Add(pin);
                if (data.Parcel?.RestrictiveSizeShape == 1) restrictiveSizeShapePins.Add(pin);
                if (data.Parcel?.TidelandShoreland == 3) tidelandShorelandPins.Add(pin);
            }
        }
        if (reviewedExclusions is not null)
        {
            foreach (var pin in reviewedExclusions.Keys.Where(pin => !rowsByPin.ContainsKey(pin)))
            {
                Console.Error.WriteLine($"WARNING: Reviewed exclusion {pin} is not present in the parcel snapshot.");
            }
        }
        var appliedReviewedExclusions = new List<ReviewedParcelExclusion>();
        var namesByPin = propertyNames.Features.EnumerateArray()
            .Select(feature => feature.GetProperty("properties"))
            .GroupBy(p => ParcelRecord.Text(p, "PIN")
                ?? throw new InvalidDataException("Property-name record has no PIN."))
            .ToDictionary(group => group.Key,
                group => group.Select(p => ParcelRecord.Text(p, "PROP_NAME"))
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var missingNameRecord = groups.FirstOrDefault(group => !namesByPin.ContainsKey(group.Key));
        if (bulk is null && missingNameRecord is not null)
        {
            throw new InvalidDataException(
                $"No property-name record for {missingNameRecord.Key}. Refresh the datasets to align their parcel inventories.");
        }
        foreach (var group in (fullPropertyNames?.Features.EnumerateArray().AsEnumerable() ?? [])
            .Select(feature => feature.GetProperty("properties"))
            .GroupBy(p => ParcelRecord.Text(p, "PIN")
                ?? throw new InvalidDataException("Full property-name record has no PIN.")))
        {
            var names = group.Select(p => ParcelRecord.Text(p, "PROPNAME"))
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
            {
                namesByPin[group.Key] = names;
            }
        }
        if (bulk is not null)
        {
            foreach (var pin in rowsByPin.Keys)
            {
                var names = bulk.Parcels.GetValueOrDefault(pin)?.PropertyNames ?? [];
                if (names.Length > 0) namesByPin[pin] = names;
                else namesByPin.TryAdd(pin, []);
            }
        }
        ParcelDecision ClassifyBase(string pin, IReadOnlyList<ImprovementReference>? references = null, string? neighborOverride = null) =>
            ParcelFilter.Classify(rowsByPin[pin], commonPins.Contains(pin), references,
                topographyPins.Contains(pin), waterProblemPins.Contains(pin),
                restrictiveSizeShapePins.Contains(pin) && neighborOverride != "restrictive_size_shape_flagged",
                AssessmentReferences.IsConstructionMarked(namesByPin[pin]),
                tidelandShorelandPins.Contains(pin), AssessmentReferences.IsTidelandNamed(namesByPin[pin]),
                AssessmentReferences.IsBnsfNamed(namesByPin[pin]),
                schoolOwned: publicOwners.TryGetValue(pin, out var owners) && owners.Any(owner => owner.OwnerType == "SCHOOL"),
                publicOwned: publicOwners.ContainsKey(pin),
                valuation: assessmentsByPin?.GetValueOrDefault(pin),
                allowAutoShowroomNeighborException: neighborOverride == "auto_showroom_and_lot_use");
        int?[] TaxRollYears(string pin)
        {
            if (bulk is null) return rowsByPin[pin].Select(row => row.TaxYear).Distinct().Order().ToArray();
            var years = assessmentsByPin?.GetValueOrDefault(pin)?.SourceTaxRollYears;
            return years is { Length: > 0 } ? years.Select(year => (int?)year).ToArray() : [null];
        }

        bool CanOverride(string reason) => reason is "restrictive_size_shape_flagged" or "auto_showroom_and_lot_use";
        var potentialCandidatePins = rowsByPin.Keys.Where(pin =>
        {
            if (reviewedExclusions?.ContainsKey(pin) == true) return false;
            var decision = ClassifyBase(pin);
            return decision.Status != "excluded" || CanOverride(decision.Reasons[0]) &&
                ClassifyBase(pin, neighborOverride: decision.Reasons[0]).Status != "excluded";
        })
            .ToHashSet(StringComparer.Ordinal);
        var associationRowsByPin = assessmentsByPin is not null
            ? rowsByPin.ToDictionary(entry => entry.Key, entry =>
            {
                var value = assessmentsByPin[entry.Key];
                var evidence = value?.ImprovementValue ?? value?.KnownSplitImprovementLowerBound ?? value?.KnownCondoImprovementLowerBound;
                return entry.Value.Select(row => row with { ImprovementValue = evidence }).ToArray();
            }, StringComparer.Ordinal)
            : rowsByPin;
        var sharedNameSites = new SharedNameSiteIndex(associationRowsByPin, namesByPin, potentialCandidatePins, cancellationToken);
        var dealershipParking = new DealershipParkingIndex(rowsByPin, namesByPin);
        var referenceCache = new Dictionary<string, ImprovementReference[]>(StringComparer.Ordinal);
        ImprovementReference[] ReferencesFor(string pin)
        {
            if (referenceCache.TryGetValue(pin, out var cached)) return cached;
            var references = AssessmentReferences.Parse(pin, namesByPin[pin]);
            var rows = rowsByPin[pin];
            var year = rows[0].TaxYear;
            if (references.Length == 0 && year is not null && rows.All(row => row.TaxYear == year))
            {
                references = AssessmentReferences.ParseStructured(pin, namesByPin[pin], targetPin =>
                    associationRowsByPin.TryGetValue(targetPin, out var target) &&
                    target.Any(row => row.TaxYear == year && row.ImprovementValue > ParcelFilter.ImprovementThreshold));
            }
            referenceCache[pin] = references;
            return references;
        }
        var decisionCache = new Dictionary<(string Pin, string? Override), ParcelDecision>();
        ParcelDecision ClassifyFinal(string pin, string? neighborOverride = null)
        {
            if (decisionCache.TryGetValue((pin, neighborOverride), out var cached)) return cached;
            var decision = ClassifyBase(pin, ReferencesFor(pin), neighborOverride);
            var data = bulk?.Parcels.GetValueOrDefault(pin);
            string? reason = decision.Status == "excluded" ? null
                : data?.IncompleteConstruction == true ? "assessor_incomplete_construction"
                : data?.HasParkingGarage == true ? "assessor_parking_garage"
                : data?.Condo?.ComplexType == 7 ? "assessor_floating_home_condo"
                : rowsByPin[pin].Any(row => row.UseCode == 180) && dealershipParking.Find(pin, namesByPin[pin]).Length > 0
                    ? "dealership_parking_name_match"
                : sharedNameSites.Find(pin).Count > 0 ? "shared_name_site_improvements" : null;
            if (reason is not null) decision = new("excluded", null, decision.ImprovementValue, [reason], []);
            decisionCache[(pin, neighborOverride)] = decision;
            return decision;
        }
        var geometryCache = new Dictionary<string, Geometry?>(StringComparer.Ordinal);
        Geometry? CompleteGeometry(string pin)
        {
            if (geometryCache.TryGetValue(pin, out var cached)) return cached;
            var parts = new List<Geometry>();
            foreach (var raw in rowsByPin[pin].Select(row => row.Geometry).DistinctBy(raw => raw.GetRawText()))
            {
                var geometry = ParkIndex.ReadGeometry(raw);
                if (geometry is null || geometry.IsEmpty || !geometry.IsValid)
                {
                    geometryCache[pin] = null;
                    return null;
                }
                parts.Add(geometry);
            }
            var merged = ParkIndex.Merge(parts);
            geometryCache[pin] = merged is not null && !merged.IsEmpty && merged.IsValid ? merged : null;
            return geometryCache[pin];
        }
        var acceptedNeighbors = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        var neighborCandidates = new List<(string Pin, string Exclusion, Geometry Geometry)>();
        foreach (var pin in potentialCandidatePins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = ClassifyFinal(pin);
            var exclusion = original.Status == "excluded" ? original.Reasons[0] : null;
            if (exclusion is not null && !CanOverride(exclusion)) continue;
            var decision = exclusion is null ? original : ClassifyFinal(pin, exclusion);
            if (decision.Status == "excluded" || decision.ImprovementValue is not (>= 0 and <= ParcelFilter.ImprovementThreshold))
                continue;
            var geometry = CompleteGeometry(pin);
            if (geometry is null || parkIndex.Matches(pin, [geometry]).Length > 0) continue;
            if (exclusion is null) acceptedNeighbors[pin] = geometry;
            else neighborCandidates.Add((pin, exclusion, geometry));
        }
        var emptyNeighbors = new EmptyNeighborIndex(acceptedNeighbors);
        var neighborExceptions = new Dictionary<string, NeighborException>(StringComparer.Ordinal);
        foreach (var candidate in neighborCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var neighbors = emptyNeighbors.Find(candidate.Pin, candidate.Geometry);
            if (neighbors.Length == 0) continue;
            var nominal = assessmentsByPin?.GetValueOrDefault(candidate.Pin)?.HasNominalImprovement ??
                rowsByPin[candidate.Pin].Any(row => row.ImprovementValue == ParcelFilter.ImprovementThreshold);
            var overridden = candidate.Exclusion == "auto_showroom_and_lot_use" && nominal
                ? new[] { candidate.Exclusion, "nominal_1000_improvements" } : [candidate.Exclusion];
            neighborExceptions[candidate.Pin] = new(candidate.Exclusion, overridden, neighbors);
        }
        foreach (var pin in sharedNameSites.UnresolvedGeometryPins)
        {
            Console.Error.WriteLine(
                $"WARNING: Cannot evaluate shared-name site links for {pin}: missing or invalid parcel geometry.");
        }
        var sourceList = new List<JsonElement>
        {
            parcels.Source, outlines.Source, parks.Source, otherParks.Source, common.Source, publicOwnership.Source
        };
        foreach (var snapshot in new[] { propertyNames, fullPropertyNames, topography, waterProblems, restrictiveSizeShape, tidelandShoreland })
            if (snapshot is not null) sourceList.Add(snapshot.Source);
        if (bulk is not null) sourceList.Add(JsonSerializer.SerializeToElement(new
        {
            file = "assessor-bulk", url = "https://info.kingcounty.gov/assessor/DataDownload/default.aspx",
            dataExtracted = bulk.DataExtracted, downloadedAtUtc = bulk.DownloadedAtUtc, archives = bulk.Sources
        }));
        var sources = sourceList.ToArray();
        var assessmentExclusions = new List<object>();
        var assessmentDataBasis = new
        {
            source = bulk is null ? "Most recently certified tax roll" : "Bulk-base assessment cross-checked against GIS; positive improvements preferred over zero/missing values",
            yearField = bulk is null ? "KCTP_TAXYR" : "TaxYr / RollYr / BillYr / KCTP_TAXYR",
            yearMeaning = bulk is null ? "Tax roll year, not valuation year" : "Published source tax years are retained; comparison values carry forward through the snapshot's next tax year",
            asOfTaxRollYear = (int?)bulk?.AsOfTaxRollYear,
            dataExtracted = (DateOnly?)bulk?.DataExtracted,
            mayLagLatestAppraisal = true
        };
        var exported = 0;
        var aerialMembers = new List<AerialParcelMember>();
        AerialParcelGroup[] aerialGroups = [];
        var review = 0;
        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var warningCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "empty-parcels.geojson");
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("type", "FeatureCollection");
                writer.WritePropertyName("sources");
                JsonSerializer.Serialize(writer, sources);
                writer.WritePropertyName("assessmentDataBasis");
                JsonSerializer.Serialize(writer, assessmentDataBasis);
                writer.WritePropertyName("parkBoundaryRepairs");
                JsonSerializer.Serialize(writer, parkIndex.Repairs);
                writer.WritePropertyName("reviewedExclusions");
                JsonSerializer.Serialize(writer, reviewedExclusions?.Values.ToArray() ?? []);
                writer.WriteNumber("improvementThreshold", ParcelFilter.ImprovementThreshold);
                writer.WriteStartArray("features");
                foreach (var group in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rows = rowsByPin[group.Key];
                    var bulkData = bulk?.Parcels.GetValueOrDefault(group.Key);
                    if (reviewedExclusions?.TryGetValue(group.Key, out var reviewed) == true)
                    {
                        Increment(excluded, reviewed.Reason);
                        appliedReviewedExclusions.Add(reviewed);
                        continue;
                    }
                    var names = namesByPin[group.Key];
                    var references = ReferencesFor(group.Key);
                    var neighborException = neighborExceptions.GetValueOrDefault(group.Key);
                    var decision = ClassifyFinal(group.Key, neighborException?.OriginalExclusion);
                    var dealershipMatches = rows.Any(row => row.UseCode == 180)
                        ? dealershipParking.Find(group.Key, names) : [];
                    var siteAssociations = sharedNameSites.Find(group.Key);
                    if (decision.Status == "excluded")
                    {
                        Increment(excluded, decision.Reasons[0]);
                        if (references.Length > 0 || decision.Reasons[0] is "cross_parcel_improvements" or "nominal_1000_improvements" or "construction_marked_name" or "tideland_property_name" or "shared_name_site_improvements" or "bnsf_property_name" or "school_owned_land" or "government_owned_land" or "dealership_parking_name_match" or "assessor_incomplete_construction" or "assessor_parking_garage" or "assessor_floating_home_condo" or "known_condo_unit_improvements_above_threshold" or "inactive_assessor_parcel" or "vacant_use_with_valuable_improvements" or "known_exempt_split_improvements_above_threshold")
                        {
                            assessmentExclusions.Add(new
                            {
                                pin = group.Key,
                                reasons = decision.Reasons,
                                propertyNames = names,
                                appraisedImprovementValues = rows.Select(row => row.ImprovementValue).Distinct().ToArray(),
                                taxRollYears = TaxRollYears(group.Key),
                                assessment = assessmentsByPin?.GetValueOrDefault(group.Key),
                                assessor = bulkData,
                                references,
                                sharedNameAssociations = siteAssociations,
                                dealershipParkingMatches = dealershipMatches,
                                publicOwnership = publicOwners.TryGetValue(group.Key, out var owners)
                                    ? new
                                    {
                                        owners = owners.OrderBy(owner => owner.OwnerType, StringComparer.Ordinal)
                                            .ThenBy(owner => owner.OwnerName, StringComparer.Ordinal).ToArray(),
                                        source = publicOwnership.Source
                                    } : null,
                                referencedParcels = references.SelectMany(reference => reference.ParcelPins)
                                    .Concat(siteAssociations.SelectMany(association => association.ImprovementBearingPins))
                                    .Concat(dealershipMatches.Select(match => match.DealershipPin))
                                    .Distinct(StringComparer.Ordinal).Select(pin =>
                                    {
                                        var found = rowsByPin.TryGetValue(pin, out var targetRows);
                                        return new
                                        {
                                            pin,
                                            found,
                                            propertyNames = namesByPin.GetValueOrDefault(pin, []),
                                            appraisedImprovementValues = targetRows?.Select(row => row.ImprovementValue).Distinct().ToArray(),
                                            taxRollYears = found ? TaxRollYears(pin) : null,
                                            assessment = assessmentsByPin?.GetValueOrDefault(pin)
                                        };
                                    }).ToArray()
                            });
                        }
                        continue;
                    }

                    var warnings = decision.Warnings.ToHashSet(StringComparer.Ordinal);
                    if (neighborException is not null) warnings.Add("neighbor_based_exclusion_override_requires_review");
                    if (bulk is not null)
                    {
                        if (bulkData is null) warnings.Add("missing_bulk_parcel_and_assessment");
                        else
                        {
                            warnings.UnionWith(bulkData.Warnings);
                            if (assessmentsByPin?.GetValueOrDefault(group.Key)?.HasValue == true)
                                warnings.Remove("missing_or_unresolved_bulk_assessment");
                            if (bulkData.HasBuildingEvidence && (decision.ImprovementValue is null or <= 1000))
                                warnings.Add("recorded_building_with_low_or_unresolved_value");
                        }
                    }
                    var geometries = new List<Geometry>();
                    foreach (var element in rows.Select(row => row.Geometry)
                        .DistinctBy(element => element.GetRawText()))
                    {
                        var geometry = ParkIndex.ReadGeometry(element);
                        if (geometry is null || geometry.IsEmpty)
                        {
                            warnings.Add("missing_geometry");
                        }
                        else if (!geometry.IsValid)
                        {
                            warnings.Add("invalid_geometry");
                        }
                        else
                        {
                            geometries.Add(geometry);
                        }
                    }
                    var parkNames = parkIndex.Matches(group.Key, geometries);
                    if (parkNames.Length > 0)
                    {
                        Increment(excluded, "park_or_greenbelt_overlap");
                        continue;
                    }
                    var incompleteGeometry = warnings.Contains("missing_geometry") || warnings.Contains("invalid_geometry");
                    if (incompleteGeometry)
                    {
                        warnings.Add("park_overlap_unverified");
                    }
                    if (sources.Any(source => !source.TryGetProperty("downloadedAtUtc", out var date) ||
                        date.ValueKind == JsonValueKind.Null))
                    {
                        warnings.Add("source_provenance_unverified");
                    }
                    var needsReview = warnings.Count > 0;
                    var mergedGeometry = incompleteGeometry ? null : ParkIndex.Merge(geometries);
                    writer.WriteStartObject();
                    writer.WriteString("type", "Feature");
                    writer.WriteString("id", group.Key);
                    writer.WritePropertyName("geometry");
                    // Do not display a partial footprint as the complete parcel.
                    JsonSerializer.Serialize(writer, mergedGeometry, ParkIndex.GeometryJson);
                    writer.WritePropertyName("properties");
                    JsonSerializer.Serialize(writer, new
                    {
                        pin = group.Key,
                        propertyNames = names,
                        category = decision.Category,
                        status = needsReview ? "needs_review" : "candidate",
                        inclusionReasons = neighborException is null ? decision.Reasons
                            : decision.Reasons.Append("neighbor_based_exclusion_override").ToArray(),
                        neighborException,
                        warnings = warnings.Order(StringComparer.Ordinal).ToArray(),
                        appraisedImprovementValue = decision.ImprovementValue,
                        taxRollYears = TaxRollYears(group.Key),
                        asOfTaxRollYear = (int?)bulk?.AsOfTaxRollYear,
                        assessment = assessmentsByPin?.GetValueOrDefault(group.Key),
                        assessor = bulkData,
                        accountNumbers = rows.Select(row => row.Account).OfType<string>()
                            .Concat(bulkData?.AccountNumbers ?? []).Distinct().Order(StringComparer.Ordinal).ToArray(),
                        addresses = rows.Select(row => row.Address).Where(address => address is not null)
                            .Distinct().Order(StringComparer.Ordinal).ToArray(),
                        presentUses = rows.Select(row => new { code = row.UseCode, description = row.UseDescription }).Distinct().ToArray(),
                        propertyTypes = rows.Select(row => row.PropertyType).Distinct().ToArray(),
                        lotSquareFeet = rows.Select(row => row.LotSquareFeet).Distinct().ToArray(),
                        sourceRecordCount = rows.Length
                    });
                    writer.WriteEndObject();
                    aerialMembers.Add(new(group.Key, mergedGeometry, decision.Category!, needsReview));
                    exported++;
                    review += needsReview ? 1 : 0;
                    Increment(categories, decision.Category!);
                    foreach (var warning in warnings)
                    {
                        Increment(warningCounts, warning);
                    }
                    if (exported % 500 == 0)
                    {
                        await writer.FlushAsync(cancellationToken);
                    }
                }
                writer.WriteEndArray();
                aerialGroups = AerialParcelGrouping.Build(aerialMembers, cancellationToken);
                writer.WritePropertyName("aerialGroups");
                JsonSerializer.Serialize(writer, aerialGroups);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
            }

            var summary = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                improvementThreshold = ParcelFilter.ImprovementThreshold,
                sourceRecords = parcels.Features.GetArrayLength(),
                uniqueParcels = groups.Length,
                exportedParcels = exported,
                needsReview = review,
                aerialGrouping = new
                {
                    groupCount = aerialGroups.Length,
                    groupedParcels = aerialGroups.Sum(group => group.ParcelCount),
                    ungroupedGeometryPins = aerialMembers.Where(parcel => parcel.Geometry is null).Select(parcel => parcel.Pin).ToArray(),
                    boundaryToleranceMetres = ParcelBoundaryGeometry.ToleranceMetres,
                    minimumSharedBoundaryMetres = ParcelBoundaryGeometry.MinimumSharedBoundaryMetres,
                    overlappingFootprintsGrouped = true
                },
                categories,
                exclusions = excluded,
                warnings = warningCounts,
                sources,
                assessmentDataBasis,
                bulkDiagnostics = bulk?.Diagnostics,
                bulkRecordsRead = bulk?.RecordsRead,
                parkBoundaryRepairs = parkIndex.Repairs,
                reviewedExclusionsApplied = appliedReviewedExclusions,
                neighborExceptionsApplied = neighborExceptions.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new { pin = entry.Key, evidence = entry.Value }).ToArray(),
                sharedNameSiteMatching = new
                {
                    boundaryToleranceMetres = SharedNameSiteIndex.BoundaryToleranceMetres,
                    minimumSharedBoundaryMetres = SharedNameSiteIndex.MinimumSharedBoundaryMetres,
                    unresolvedGeometryPins = sharedNameSites.UnresolvedGeometryPins
                },
                limitations = new[]
                {
                    "Candidate inventory, not proof of physical vacancy or development suitability.",
                    "Aerial-photo groups are connected components of eligible parcel footprints using shared boundaries or overlapping area, not ownership or development-site determinations. Group unions preserve excluded gaps and holes; crop bounds can include surrounding context.",
                    "Otherwise eligible low-value parcels excluded only by Restrictive Size Shape or Auto Showroom and Lot may be included for review when they share a boundary with an originally accepted low-value parcel. The showroom-plus-nominal-$1,000 combination is permitted; all other exclusions remain active. Rescued parcels do not seed further rescues.",
                    "Vacant-classified parcels with selected appraised improvements above $1,000 are excluded by user-reviewed policy; this class can contain associated occupied sites or construction/redevelopment parcels. Unresolved values remain review cases.",
                    "Bulk files are the base for values, parcel facts and building/account metadata. GIS fills null attributes and zero/missing improvements when a positive GIS appraisal exists; actual source tax years remain separate from the comparison horizon.",
                    "Value history selects ordinary and split-9 exempt components independently, combining matching year/revision pairs without adding already-combined account totals. Other split codes remain unresolved; omitted rows are ignored.",
                    "Condo-complex values aggregate current unit assessments when coverage is complete; incomplete coverage remains unknown, but a known improvement lower bound above the threshold can exclude the site.",
                    "Building completion percentages, garage sections and floating-home condo types provide additional evidence for existing construction/garage/houseboat exclusions; completed building records with low values are flagged, not automatically removed.",
                    "All Townhouse Plat records (29), including potentially completed developments, are excluded by user-selected policy; missing assessments are not evidence of empty land.",
                    "Explicit construction-marked property names are excluded conservatively; names may be stale, and a completed building is also not empty land.",
                    "Topography YES (assessor TOPOGRAPHY = 1) is excluded by user-selected policy; this is a constraint, not proof of occupancy or unusability.",
                    "Water Problems YES (assessor WATERPROBLEMS = Y) is excluded by user-selected policy; this is a constraint, not proof of submersion.",
                    "Restrictive Size Shape YES (assessor RESTRICTIVESZSHAPE = 1) is excluded by user-selected policy; this is a constraint, not proof of occupancy or unusability.",
                    "Reviewed exclusions override assessor classification and retain user-observed reasons; no general spatial water/rail occupancy detection is performed.",
                    "Seattle scope uses CTYNAME (jurisdiction), not the postal city.",
                    "HYDR water placeholders, rivers/creeks/streams (333), freshwater bodies (337), and tidelands (334/335) are excluded; other shoreline parcels are not spatially tested for complete submersion.",
                    "Assessor TIDELANDSHORELAND = 3 (tidelands/shorelands only) is excluded even when present use says vacant; mixed upland/tideland records (2) and historical tide-plat names alone are not excluded by this rule.",
                    "An exact TIDELAND or TIDELANDS property name also excludes a parcel; longer historical plat or building names are not treated as tidal solely by containing that word.",
                    "BNSF/Burlington Northern-named parcels are excluded; this is name-based association, not complete ownership matching, because the public parcel sources omit full owner names.",
                    "All public/government ownership identified in the county's public-ownership layer is excluded, including genuinely vacant public land; ownership data can lag changes.",
                    "Auto-showroom/display lots are excluded except for explicit neighbor-based review exceptions; terminal classes remain excluded. Commercial parking names matching classified dealerships remain excluded using meaningful business-name words, not a brand blacklist.",
                    "Tract/unknown/public placeholders, religious/welfare uses, cemeteries/mortuaries/crematories, hospitals, schools, utilities, golf courses, sport facilities, marinas, houseboats, rail/marine terminals and rights-of-way are excluded.",
                    "All associated/accessory parking (159) is excluded; standalone commercial parking (180) remains eligible subject to other exclusions.",
                    "Condominium and common-property candidates are flagged, not resolved to physical sites.",
                    "All undivided-interest records (property type U) are excluded by user-selected policy; underlying GIS records can lack the related share-account assessments, and genuinely vacant co-owned land may also be excluded.",
                    "Nominal $1,000 valuations are excluded except for the approved showroom-neighbor review combination. Cross-parcel improvement references remain excluded pending independent physical verification.",
                    "Bulk names, use descriptions and constraints take precedence when non-null; GIS fills missing fields. Positive bulk improvements retain precedence when both sources are positive. Partial condo totals are not replaced with zero-valued GIS master records.",
                    "Improvement, DATA ON, and explicit associated-parcel references are heuristic; truncated or unresolved references are excluded conservatively, and unmentioned relationships may be missed.",
                    "Other structured PIN references are excluded only when they resolve to another parcel with same-year improvements above $1,000; bare four-digit numbers do not trigger this fallback.",
                    "Same-name boundary-connected sites are excluded when a distinct parcel carries improvements above $1,000 in the same tax roll year; matching names or touching corners alone do not qualify.",
                    "Shared-name site matching is conservative association evidence, not proof of building coverage or common ownership; unrelated or genuinely open parcels within a named site may be excluded.",
                    "Recent permit records and non-contact details are included as evidence, but an issued permit alone is not treated as proof that construction started.",
                    "Parking classifications are not independently checked against building footprints or imagery."
                }
            };
            var summaryPath = Path.Combine(outputDirectory, "summary.json");
            var summaryTemporary = summaryPath + "." + Guid.NewGuid().ToString("N") + ".part";
            var assessmentPath = Path.Combine(outputDirectory, "assessment-exclusions.json");
            var assessmentTemporary = assessmentPath + "." + Guid.NewGuid().ToString("N") + ".part";
            var bulkAuditPath = Path.Combine(outputDirectory, "bulk-assessment-audit.json");
            var bulkAuditTemporary = bulk is not null ? bulkAuditPath + "." + Guid.NewGuid().ToString("N") + ".part" : null;
            var groupPath = Path.Combine(outputDirectory, "empty-parcel-groups.geojson");
            var groupTemporary = groupPath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await File.WriteAllTextAsync(summaryTemporary,
                    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                await using (var assessmentStream = new FileStream(assessmentTemporary, FileMode.CreateNew, FileAccess.Write))
                {
                    await JsonSerializer.SerializeAsync(assessmentStream, assessmentExclusions, cancellationToken: cancellationToken);
                }
                await using (var groupStream = new FileStream(groupTemporary, FileMode.CreateNew, FileAccess.Write))
                    await AerialParcelGrouping.WriteGeoJsonAsync(groupStream, aerialGroups, sources, cancellationToken);
                if (bulkAuditTemporary is not null)
                {
                    await using var auditStream = new FileStream(bulkAuditTemporary, FileMode.CreateNew, FileAccess.Write);
                    using var auditWriter = new Utf8JsonWriter(auditStream);
                    auditWriter.WriteStartArray();
                    foreach (var pin in originalRowsByPin.Keys.Order(StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var data = bulk!.Parcels.GetValueOrDefault(pin);
                        JsonSerializer.Serialize(auditWriter, new
                        {
                            pin,
                            originalGisImprovementValues = originalRowsByPin[pin].Select(row => row.ImprovementValue).Distinct().ToArray(),
                            originalGisTaxRollYears = originalRowsByPin[pin].Select(row => row.TaxYear).Distinct().ToArray(),
                            asOfTaxRollYear = bulk.AsOfTaxRollYear,
                            bulkAssessment = data?.Assessment,
                            selectedAssessment = assessmentsByPin![pin],
                            buildingRecords = data?.Buildings ?? [],
                            condominium = data?.Condo,
                            warnings = data?.Warnings ?? ["missing_bulk_parcel_and_assessment"]
                        });
                        if (auditWriter.BytesPending > 65536) await auditWriter.FlushAsync(cancellationToken);
                    }
                    auditWriter.WriteEndArray();
                    await auditWriter.FlushAsync(cancellationToken);
                }
                File.Move(temporary, outputPath, overwrite: true);
                File.Move(assessmentTemporary, assessmentPath, overwrite: true);
                if (bulkAuditTemporary is not null) File.Move(bulkAuditTemporary, bulkAuditPath, overwrite: true);
                File.Move(groupTemporary, groupPath, overwrite: true);
                File.Move(summaryTemporary, summaryPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(summaryTemporary))
                {
                    File.Delete(summaryTemporary);
                }
                if (File.Exists(assessmentTemporary))
                {
                    File.Delete(assessmentTemporary);
                }
                if (bulkAuditTemporary is not null && File.Exists(bulkAuditTemporary)) File.Delete(bulkAuditTemporary);
                if (File.Exists(groupTemporary)) File.Delete(groupTemporary);
            }
            Console.WriteLine($"Exported {exported:N0} candidate parcels ({review:N0} need review) to {outputPath}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
