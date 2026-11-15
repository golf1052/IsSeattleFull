using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IsSeattleEmpty.Parcels;

public static class AssessorBulkLoader
{
    private const int CacheVersion = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] ArchiveNames =
    [
        "Accessory", "Apartment Complex", "Commercial Building", "Condo Complex and Units",
        "Lookup", "Parcel", "Permit", "Real Property Account", "Real Property Appraisal History",
        "Residential Building", "Unit Breakdown", "Vacant Lot", "Value History"
    ];
    private sealed record CacheFile(int Version, string Fingerprint, AssessorBulkSnapshot Snapshot);
    private sealed record Candidate(
        int Year, DateTime Revision, int Priority, decimal? Land, decimal? Improvements,
        string Source, string Member, string Status, bool Nominal, bool Inactive, bool Destroyed);
    private sealed class Selection
    {
        public required Candidate Best { get; set; }
        public bool Conflict { get; set; }
    }

    public static string ResolveDirectory(string dataDirectory, string? explicitDirectory)
    {
        if (explicitDirectory is not null)
            return Path.GetFullPath(explicitDirectory);
        var root = Path.Combine(dataDirectory, "assessor-bulk");
        if (Directory.Exists(root))
        {
            var found = Directory.EnumerateDirectories(root)
                .Where(path => DateOnly.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) && File.Exists(Path.Combine(path, "manifest.json")))
                .OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            if (found is not null) return found;
        }
        throw new InvalidDataException(
            "No assessor bulk snapshot found. Download the approved bulk files and manifest into data\\assessor-bulk\\YYYY-MM-DD, or pass --bulk-dir PATH.");
    }

    public static async Task<AssessorBulkSnapshot> LoadAsync(
        string directory, IReadOnlySet<string> scope, TextWriter log, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = manifest.RootElement;
        var date = DateOnly.ParseExact(root.GetProperty("dataExtracted").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var declared = root.GetProperty("datasets").EnumerateArray().ToDictionary(
            dataset => dataset.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var sources = new List<BulkSource>();
        var signature = new StringBuilder($"bulk-v{CacheVersion}|{date:yyyy-MM-dd}|");
        signature.Append(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken))));
        foreach (var name in ArchiveNames)
        {
            if (!declared.TryGetValue(name, out var dataset))
                throw new InvalidDataException($"Bulk manifest is missing {name}.");
            var filename = name + ".zip";
            var path = Path.Combine(directory, filename);
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new InvalidDataException($"Missing bulk archive: {path}.");
            var archive = dataset.GetProperty("archive");
            var bytes = archive.GetProperty("bytes").GetInt64();
            if (info.Length != bytes)
                throw new InvalidDataException($"Bulk archive size changed: {filename}. Re-download the snapshot.");
            var hash = archive.GetProperty("sha256").GetString()!;
            sources.Add(new(filename, dataset.GetProperty("archiveUrl").GetString()!, hash, bytes));
            signature.Append($"|{filename}|{hash}|{bytes}|{info.LastWriteTimeUtc.Ticks}");
        }
        foreach (var pin in scope.Order(StringComparer.Ordinal)) signature.Append('|').Append(pin);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
        var cachePath = Path.Combine(directory, $"seattle-enrichment-v{CacheVersion}.json");
        if (File.Exists(cachePath))
        {
            await using var cached = File.OpenRead(cachePath);
            var cache = await JsonSerializer.DeserializeAsync<CacheFile>(cached, Json, cancellationToken)
                ?? throw new InvalidDataException("The bulk enrichment cache is empty.");
            if (cache.Version == CacheVersion && cache.Fingerprint == fingerprint)
            {
                if (cache.Snapshot is null || cache.Snapshot.Parcels is null)
                    throw new InvalidDataException("The bulk enrichment cache is incomplete.");
                log.WriteLine($"Using cached bulk enrichment: {cachePath}");
                return cache.Snapshot;
            }
            log.WriteLine("Bulk input files or parcel scope changed; rebuilding enrichment cache.");
        }
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(Path.Combine(directory, source.File));
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bulk archive hash mismatch: {source.File}. Re-download the snapshot.");
        }
        var builder = new Builder(directory, scope, date, log, cancellationToken);
        var snapshot = builder.Build(sources.ToArray(),
            root.TryGetProperty("generatedAtUtc", out var downloaded) ? downloaded.GetString() : null);
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stream, new CacheFile(CacheVersion, fingerprint, snapshot), Json, cancellationToken);
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        log.WriteLine($"Cached bulk enrichment for {snapshot.Parcels.Count:N0} parcel records.");
        return snapshot;
    }

    private sealed class Builder(
        string directory, IReadOnlySet<string> originalScope, DateOnly date, TextWriter log, CancellationToken cancellationToken)
    {
        private readonly HashSet<string> scope = originalScope.ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, BulkParcelData> data = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> warnings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Selection> selections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Selection> exemptSplitSelections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> lookups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> diagnostics = new(StringComparer.Ordinal);
        private int MaximumYear => date.Year + 1;
        private BulkParcelData Get(string pin)
        {
            if (!data.TryGetValue(pin, out var result)) data[pin] = result = new();
            return result;
        }
        private void Warn(string pin, string warning)
        {
            if (!warnings.TryGetValue(pin, out var items)) warnings[pin] = items = new(StringComparer.Ordinal);
            items.Add(warning);
        }
        private void Count(string key) => diagnostics[key] = diagnostics.GetValueOrDefault(key) + 1;
        private void Read(string archive, string member, string fields, Action<BulkCsvRow> action,
            bool filtered = true, bool majorOnly = false)
        {
            log.WriteLine($"Reading bulk {member}...");
            counts[member] = BulkCsvReader.Read(Path.Combine(directory, archive + ".zip"), member,
                fields.Split(','), filtered ? scope : null, action, cancellationToken, majorOnly);
            log.WriteLine($"  {counts[member]:N0} CSV records scanned.");
        }
        private bool Year(string pin, int? year)
        {
            if (year is >= 1900 && year <= MaximumYear) return true;
            Warn(pin, "invalid_bulk_tax_roll_year");
            Count("ignored_invalid_year");
            return false;
        }
        private bool Status(string pin, string? status)
        {
            if (status is "T" or "X" or "O") return true;
            Warn(pin, "unsupported_bulk_tax_status");
            Count("ignored_unsupported_tax_status");
            return false;
        }
        private static int Compare(Candidate a, Candidate b)
        {
            var comparison = a.Year.CompareTo(b.Year);
            if (comparison == 0) comparison = a.Revision.Date.CompareTo(b.Revision.Date);
            if (comparison == 0) comparison = a.Priority.CompareTo(b.Priority);
            if (comparison == 0) comparison = a.Revision.CompareTo(b.Revision);
            return comparison;
        }
        private void Add(string pin, Candidate candidate, Dictionary<string, Selection>? target = null)
        {
            target ??= selections;
            if (candidate.Improvements < 0)
            {
                Warn(pin, "negative_bulk_improvement_value");
                candidate = candidate with { Improvements = null };
            }
            if (!target.TryGetValue(pin, out var selection))
            {
                target[pin] = new() { Best = candidate };
                return;
            }
            var comparison = Compare(candidate, selection.Best);
            if (comparison > 0)
            {
                selection.Best = candidate;
                selection.Conflict = false;
            }
            else if (comparison == 0 &&
                (candidate.Improvements != selection.Best.Improvements || candidate.Land != selection.Best.Land ||
                 candidate.Status != selection.Best.Status || candidate.Inactive != selection.Best.Inactive))
            {
                selection.Conflict = true;
            }
        }
        private static decimal? Percent(BulkCsvRow row)
        {
            var value = row.Number("PcntComplete");
            if (value is < 0 or > 100)
                throw new InvalidDataException("Bulk building/permit percentage is outside 0-100.");
            return value;
        }

        public AssessorBulkSnapshot Build(BulkSource[] sources, string? downloadedAt)
        {
            Read("Lookup", "EXTR_LookUp.csv", "LUType,LUItem,LUDescription", row =>
            {
                var type = row.Text("LUType") ?? throw new InvalidDataException("Lookup row has no type.");
                var item = row.Text("LUItem") ?? throw new InvalidDataException("Lookup row has no item.");
                if (!lookups.TryGetValue(type, out var table)) lookups[type] = table = new(StringComparer.Ordinal);
                table[item] = row.Text("LUDescription") ?? "";
            }, filtered: false);
            Read("Parcel", "EXTR_Parcel.csv",
                "Major,Minor,PropName,PropType,PresentUse,SqFtLot,Topography,RestrictiveSzShape,TidelandShoreland,WaterProblems,Access,PcntUnusable,Unbuildable,Wetland,SteepSlopeHazard",
                row => Get(row.Pin).Parcel = new(row.Text("PropName"), row.Text("PropType"),
                    row.Integer("PresentUse"), row.Number("SqFtLot"), row.Integer("Topography"),
                    row.Integer("RestrictiveSzShape"), row.Integer("TidelandShoreland"), row.Text("WaterProblems"),
                    row.Integer("Access"), row.Integer("PcntUnusable"), row.Text("Unbuildable"),
                    row.Text("Wetland"), row.Text("SteepSlopeHazard")));
            Read("Condo Complex and Units", "EXTR_CondoComplex.csv",
                "Major,ComplexType,NbrBldgs,NbrUnits,AvgUnitSize,YrBuilt,PcntComplete", row =>
                Get(row.Major + "0000").Condo = new()
                {
                    ComplexType = row.Integer("ComplexType"), Buildings = row.Integer("NbrBldgs"),
                    ExpectedUnits = row.Integer("NbrUnits"), AverageUnitSize = row.Number("AvgUnitSize"),
                    YearBuilt = row.Integer("YrBuilt"), PercentComplete = Percent(row)
                }, majorOnly: true);
            var condoMajors = data.Where(entry => entry.Value.Condo is not null)
                .Select(entry => entry.Key[..6]).ToHashSet(StringComparer.Ordinal);
            var condoUnits = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Read("Condo Complex and Units", "EXTR_CondoUnit2.csv", "Major,Minor", row =>
            {
                if (!condoMajors.Contains(row.Major)) return;
                var parent = row.Major + "0000";
                if (row.Pin == parent) return;
                if (!condoUnits.TryGetValue(parent, out var members)) condoUnits[parent] = members = new(StringComparer.Ordinal);
                members.Add(row.Pin);
                scope.Add(row.Pin);
            }, filtered: false);
            ReadAccounts();
            ReadHistories();
            FinalizeAssessments(condoUnits);
            ReadBuildings();
            ReadAdditionalFacts();
            foreach (var (pin, item) in data)
            {
                if (!originalScope.Contains(pin)) continue;
                if (item.Parcel is null) Warn(pin, "missing_bulk_parcel_record");
                if (item.Assessment is null || !item.Assessment.HasValue) Warn(pin, "missing_or_unresolved_bulk_assessment");
                item.Warnings = warnings.GetValueOrDefault(pin)?.Order(StringComparer.Ordinal).ToArray() ?? [];
                item.Accounts = item.Accounts.Distinct().OrderBy(account => account.Number, StringComparer.Ordinal).ToList();
                item.Buildings = item.Buildings.Distinct().OrderBy(building => building.Kind, StringComparer.Ordinal)
                    .ThenBy(building => building.Number, StringComparer.Ordinal).ToList();
            }
            return new()
            {
                DataExtracted = date, AsOfTaxRollYear = MaximumYear, Sources = sources,
                DownloadedAtUtc = downloadedAt, RecordsRead = counts, Diagnostics = diagnostics, Lookups = lookups,
                Parcels = data.Where(entry => originalScope.Contains(entry.Key))
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            };
        }

        private void ReadAccounts()
        {
            Read("Real Property Account", "EXTR_RPAcct_NoName.csv",
                "Major,Minor,AcctNbr,BillYr,ApprLandVal,ApprImpsVal,TaxStat,TaxValReason", row =>
            {
                var year = row.Integer("BillYr");
                var status = row.Text("TaxStat");
                if (!Year(row.Pin, year) || !Status(row.Pin, status)) return;
                var account = row.Text("AcctNbr");
                if (account is null || account.Length != 12 || !account.All(char.IsAsciiDigit))
                    throw new InvalidDataException("Bulk account number is missing or invalid.");
                Get(row.Pin).Accounts.Add(new(account, year!.Value, row.Number("ApprLandVal"), row.Number("ApprImpsVal"), status!, row.Text("TaxValReason")));
            });
            foreach (var (pin, item) in data)
            {
                if (item.Accounts.Count == 0) continue;
                var value = item.CertifiedAssessment()!;
                if (!value.HasValue) Warn(pin, "conflicting_or_missing_bulk_account_values");
                Add(pin, new(value.SourceTaxRollYears.Max(), DateTime.MinValue, 0,
                    value.LandValue, value.ImprovementValue, value.Source, value.Member, value.TaxStatus!,
                    value.HasNominalImprovement, false, value.IsDestroyed));
            }
        }

        private void ReadHistories()
        {
            Read("Value History", "EXTR_ValueHistory_V.csv",
                "Major,Minor,TaxYr,OmitYr,ApprLandVal,ApprImpsVal,TaxStatus,TaxValReason,ChangeDate,Reason,SplitCode", row =>
            {
                var year = row.Integer("TaxYr");
                if (!Year(row.Pin, year)) return;
                if (row.Integer("OmitYr") != 0) { Count("ignored_omitted_assessment"); return; }
                var split = row.Text("SplitCode");
                var exemptSplit = split == "9" && row.Text("TaxStatus") == "X" && row.Text("TaxValReason") == "EX";
                if (split is not (null or "0") && !exemptSplit)
                {
                    Warn(row.Pin, "unresolved_split_value_history");
                    Count("ignored_split_history");
                    return;
                }
                var status = row.Text("TaxStatus");
                if (!Status(row.Pin, status)) return;
                var revision = row.Date("ChangeDate");
                if (revision is null || DateOnly.FromDateTime(revision.Value) > date)
                {
                    Warn(row.Pin, "invalid_bulk_revision_date");
                    return;
                }
                var reason = row.Text("Reason") ?? "";
                var inactive = reason.Contains("KILL", StringComparison.OrdinalIgnoreCase);
                var destroyed = row.Text("TaxValReason") == "DP" ||
                    reason.Contains("DESTROYED PROPERTY", StringComparison.OrdinalIgnoreCase);
                var value = row.Number("ApprImpsVal");
                Add(row.Pin, new(year!.Value, revision.Value, 2, row.Number("ApprLandVal"), value,
                    "published_value_history", "EXTR_ValueHistory_V.csv", status!, value == 1000, inactive, destroyed),
                    exemptSplit ? exemptSplitSelections : selections);
            });
            Read("Real Property Appraisal History", "EXTR_RealPropApplHist_V.csv",
                "Major,Minor,RollYr,LandVal,ImpsVal,PostStatus,PostDate", row =>
            {
                var year = row.Integer("RollYr");
                if (!Year(row.Pin, year)) return;
                if (row.Integer("PostStatus") is not (2 or 5)) { Count("ignored_unposted_appraisal"); return; }
                var revision = row.Date("PostDate");
                if (revision is null || DateOnly.FromDateTime(revision.Value) > date)
                {
                    Warn(row.Pin, "invalid_bulk_revision_date");
                    return;
                }
                var value = row.Number("ImpsVal");
                Add(row.Pin, new(year!.Value, revision.Value, 1, row.Number("LandVal"), value,
                    "posted_appraisal_history", "EXTR_RealPropApplHist_V.csv", "POSTED", value == 1000, false, false));
            });
        }

        private void FinalizeAssessments(Dictionary<string, HashSet<string>> condoUnits)
        {
            foreach (var pin in selections.Keys.Union(exemptSplitSelections.Keys))
            {
                var selection = selections.GetValueOrDefault(pin);
                var split = exemptSplitSelections.GetValueOrDefault(pin);
                var chosen = selection?.Best ?? split!.Best;
                var conflict = selection?.Conflict == true;
                var source = chosen.Source;
                var land = chosen.Land;
                var improvements = selection is null || conflict ? null : chosen.Improvements;
                var nominal = chosen.Nominal;
                decimal? splitLowerBound = null;
                var years = new[] { chosen.Year };
                var status = chosen.Status;
                if (split is not null && !chosen.Inactive && !split.Best.Inactive)
                {
                    var component = split.Best;
                    var paired = selection is not null && chosen.Source == "published_value_history" &&
                        chosen.Year == component.Year && chosen.Revision.Date == component.Revision.Date;
                    if (paired && !conflict && !split.Conflict && chosen.Improvements is >= 0 && component.Improvements is >= 0)
                    {
                        improvements = chosen.Improvements + component.Improvements;
                        land = chosen.Land is not null && component.Land is not null ? chosen.Land + component.Land : null;
                        nominal |= component.Nominal;
                        source = "published_exempt_split_aggregation";
                        status = chosen.Status == component.Status ? chosen.Status : "MIXED";
                        Count("resolved_exempt_split_assessments");
                    }
                    else if ((selection is null || chosen.Source != "certified_account_snapshot") &&
                        (split.Conflict || component.Improvements is null or > 0))
                    {
                        conflict |= split.Conflict;
                        improvements = null;
                        var knownOrdinary = selection?.Conflict == false && chosen.Improvements is >= 0 ? chosen.Improvements : null;
                        var knownExempt = !split.Conflict && component.Improvements is >= 0 ? component.Improvements : null;
                        splitLowerBound = knownOrdinary is not null || knownExempt is not null
                            ? Math.Max(knownOrdinary ?? 0, knownExempt ?? 0) : null;
                        source = "unresolved_exempt_split_assessment";
                        years = new[] { chosen.Year, component.Year }.Distinct().Order().ToArray();
                        nominal |= component.Nominal;
                        Warn(pin, "unresolved_exempt_split_assessment");
                        Count("unresolved_exempt_split_assessments");
                    }
                }
                if (conflict) Warn(pin, "conflicting_bulk_valuation_revision");
                Get(pin).Assessment = new()
                {
                    Source = source, Member = chosen.Member, SourceTaxRollYears = years,
                    LandValue = land, ImprovementValue = improvements,
                    RevisionDate = chosen.Revision == DateTime.MinValue ? null : chosen.Revision,
                    TaxStatus = status, HasNominalImprovement = nominal,
                    IsInactive = chosen.Inactive, IsConflicting = conflict,
                    IsDestroyed = chosen.Destroyed, KnownSplitImprovementLowerBound = splitLowerBound
                };
            }
            foreach (var (parent, item) in data.Where(entry => entry.Value.Condo is not null).ToArray())
            {
                var condo = item.Condo!;
                var members = condoUnits.GetValueOrDefault(parent, []);
                condo.UnitPins = members.Order(StringComparer.Ordinal).ToArray();
                var known = members.Select(pin => (Pin: pin, Value: Get(pin).Assessment))
                    .Where(unit => unit.Value?.HasValue == true && unit.Value.IsInactive == false).ToArray();
                condo.KnownUnitAssessments = known.Length;
                condo.CompleteValuationCoverage = members.Count > 0 && condo.ExpectedUnits == members.Count && known.Length == members.Count;
                if (!condo.CompleteValuationCoverage) Warn(parent, "incomplete_condo_unit_assessments");
                var sum = known.Sum(unit => unit.Value!.ImprovementValue!.Value);
                item.Assessment = new()
                {
                    Source = "condo_unit_aggregation", Member = "EXTR_CondoUnit2.csv",
                    SourceTaxRollYears = known.SelectMany(unit => unit.Value!.SourceTaxRollYears).Distinct().Order().ToArray(),
                    ImprovementValue = condo.CompleteValuationCoverage ? sum : null,
                    LandValue = condo.CompleteValuationCoverage && known.All(unit => unit.Value!.LandValue is not null)
                        ? known.Sum(unit => unit.Value!.LandValue!.Value) : null,
                    HasNominalImprovement = known.Any(unit => unit.Value!.HasNominalImprovement),
                    KnownCondoImprovementLowerBound = sum, ComponentPins = condo.UnitPins
                };
            }
        }

        private void ReadBuildings()
        {
            Read("Residential Building", "EXTR_ResBldg.csv",
                "Major,Minor,BldgNbr,NbrLivingUnits,SqFtTotLiving,YrBuilt,PcntComplete,Address", row =>
                Get(row.Pin).Buildings.Add(new("residential", row.Text("BldgNbr") ?? "",
                    1, row.Integer("NbrLivingUnits"), row.Number("SqFtTotLiving"),
                    row.Integer("YrBuilt"), Percent(row), row.Text("Address"), null)));
            Read("Commercial Building", "EXTR_CommBldg.csv",
                "Major,Minor,BldgNbr,NbrBldgs,BldgGrossSqFt,YrBuilt,PcntComplete,Address,PredominantUse", row =>
                Get(row.Pin).Buildings.Add(new("commercial", row.Text("BldgNbr") ?? "",
                    row.Integer("NbrBldgs"), null, row.Number("BldgGrossSqFt"), row.Integer("YrBuilt"),
                    Percent(row), row.Text("Address"), row.Integer("PredominantUse"))));
            Read("Commercial Building", "EXTR_CommBldgSection.csv",
                "Major,Minor,BldgNbr,SectionNbr,SectionUse,GrossSqFt", row =>
                Get(row.Pin).CommercialSections.Add(new(row.Text("BldgNbr") ?? "", row.Text("SectionNbr") ?? "",
                    row.Integer("SectionUse"), row.Number("GrossSqFt"))));
            Read("Commercial Building", "EXTR_CommBldgFeature.csv",
                "Major,Minor,BldgNbr,FeatureType,GrossSqFt,NetSqFt", row =>
                Get(row.Pin).CommercialFeatures.Add(new(row.Text("BldgNbr") ?? "", row.Text("FeatureType"),
                    row.Number("GrossSqFt"), row.Number("NetSqFt"))));
            Read("Apartment Complex", "EXTR_AptComplex.csv",
                "Major,Minor,NbrBldgs,NbrUnits,AvgUnitSize,YrBuilt,PcntComplete,Address", row =>
                Get(row.Pin).ApartmentComplex = new("apartment_complex", "",
                    row.Integer("NbrBldgs"), row.Integer("NbrUnits"), row.Number("AvgUnitSize"),
                    row.Integer("YrBuilt"), Percent(row), row.Text("Address"), null));
        }

        private void ReadAdditionalFacts()
        {
            Read("Accessory", "EXTR_Accessory_V.csv", "Major,Minor,AccyType,AccyDescr,Quantity,Size,AccyValue", row =>
                Get(row.Pin).Accessories.Add(new(row.Text("AccyType"), row.Text("AccyDescr"),
                    row.Number("Quantity"), row.Number("Size"), row.Number("AccyValue"))));
            Read("Unit Breakdown", "EXTR_UnitBreakdown.csv", "Major,Minor,UnitTypeItemId,NbrThisType,SqFt", row =>
                Get(row.Pin).UnitBreakdown.Add(new(row.Text("UnitTypeItemId"), row.Integer("NbrThisType"), row.Number("SqFt"))));
            Read("Vacant Lot", "EXTR_VacantLot.csv", "Major,Minor,PropName,Address", row =>
            {
                var item = Get(row.Pin);
                item.HasVacantLotRecord = true;
                item.VacantLotName = row.Text("PropName");
                item.VacantLotAddress = row.Text("Address");
            });
            Read("Permit", "EXTR_PermitHistory_V.csv", "Major,Minor,PermitNbr,PermitType,PermitStatus,IssueDate,UpdateDate,PcntComplete", row =>
            {
                var item = Get(row.Pin);
                item.Permits.Add(new(row.Text("PermitNbr") ?? "", row.Text("PermitType"), row.Text("PermitStatus"),
                    row.Date("IssueDate"), row.Date("UpdateDate"), Percent(row), new(StringComparer.Ordinal)));
                if (item.Permits.Count > 5)
                    item.Permits = item.Permits.OrderByDescending(permit => permit.Issued)
                        .ThenByDescending(permit => permit.Updated).ThenBy(permit => permit.Number, StringComparer.Ordinal).Take(5).ToList();
            });
            var permits = data.Values.SelectMany(item => item.Permits).GroupBy(permit => permit.Number)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var allowed = new HashSet<string>(["42", "51", "52", "53", "55", "56", "57"], StringComparer.Ordinal);
            Read("Permit", "EXTR_PermitDetailHistory_V.csv", "PermitNbr,PermitItem,ItemValue", row =>
            {
                var item = row.Text("PermitItem");
                if (item is null || !allowed.Contains(item) || !permits.TryGetValue(row.Text("PermitNbr") ?? "", out var matches)) return;
                var value = row.Text("ItemValue");
                if (value is null) return;
                var label = lookups.GetValueOrDefault("161")?.GetValueOrDefault(item) ?? item;
                foreach (var permit in matches) permit.Details[label] = value;
            }, filtered: false);
        }
    }
}
