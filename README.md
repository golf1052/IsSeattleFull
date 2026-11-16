# IsSeattleFull

Build an explainable inventory of Seattle parcels that may be colloquially
"empty": vacant/cleared lots, surface parking, and parcels with at most $1,000
in **appraised** improvements. This is not proof of current physical vacancy.
Nominal $1,000 valuations and improvements assessed on other parcels are excluded
pending independent evidence of physical vacancy or surface parking.
The downloaded assessor bulk files are the base for values, parcel attributes,
building details and account metadata. GIS supplies geometry and exclusion masks,
fills null attributes, and cross-checks improvements: a positive appraised value
is preferred over zero or null, with bulk retaining precedence when both are positive.

## Export parcels

Requires the .NET 11 SDK and a local bulk snapshot. Python 3.11+ is needed only
for downloading a snapshot with the helper.

Open the [county download portal](https://info.kingcounty.gov/assessor/DataDownload/default.aspx),
review its data-use acknowledgment, and note its **Date Data Extracted**. Download
the required files and descriptions with explicit acknowledgment:

```powershell
python tools\download-assessor-bulk.py --accept-data-use --extracted-date 2026-11-13
```

Use the date actually shown on the portal, not today's date. This creates a
dated folder containing 13 ZIP archives, descriptions and a source/hash manifest.
The helper reuses existing files; `--refresh` explicitly redownloads them.

```powershell
dotnet run -- export
dotnet run -- export --data-dir "D:\SeattleGIS" --output-dir "D:\SeattleInventory"
dotnet run -- export --bulk-dir "D:\AssessorData\2026-11-13"
dotnet run -- export --refresh
```

The exporter automatically selects the newest dated manifest under
`data\assessor-bulk`, or the folder specified by `--bulk-dir`. Missing bulk inputs
are an error; GIS cross-checking does not replace the required bulk snapshot.

The first export streams the scoped data from compressed CSVs and writes
`seattle-enrichment-v4.json` beside the archives. Subsequent exports reuse this
processed cache. Archive hashes, lengths/timestamps, the manifest and parcel-ID
scope guard against stale or changed inputs. The archives are never unpacked.

Missing GIS geometry/attribute/mask datasets are downloaded automatically. Subsequent runs
reuse them without contacting their APIs. Exporter `--refresh` replaces the GIS
snapshots, not the bulk archives; use the download helper for a new bulk snapshot.
there is no automatic expiration. Interrupted or failed downloads never replace
completed files. Corrupt/incompatible cached files produce an error rather than
silently being redownloaded. Cached source data and diagnostic exports are ignored
by Git; the three runtime exports below are included in version control.

Normal `export` CLI runs also prepare the aerial landing catalog. To prepare or
refresh that catalog from an existing parcel export without rerunning filters:

```powershell
dotnet run -- catalog
dotnet run -- catalog --data-dir "D:\SeattleGIS" --output-dir "D:\SeattleInventory"
dotnet run -- catalog --refresh
```

The first catalog preparation downloads a public Seattle county address-point
snapshot; later runs reuse it. `catalog --refresh` refreshes only that address
snapshot. Normal `export --refresh` refreshes its GIS inputs and the address points.

The default `data` directory contains WGS84 GeoJSON FeatureCollections:

| File | Source and downloaded scope |
| --- | --- |
| `parcels.geojson` | [King County parcel geometry and attributes](https://gis-kingcounty.opendata.arcgis.com/datasets/parcels-with-address-property-and-ownership-information-public/explore), `UPPER(CTYNAME) = 'SEATTLE'`. Positive appraised improvements replace zero/missing bulk values; non-null attributes fill missing bulk fields. |
| `park-outlines.geojson` | [Seattle park outlines](https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/1) |
| `parks.geojson` | [Seattle Parks properties](https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/2), including greenbelts |
| `other-parks.geojson` | [Parks not managed by SPR](https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/3) |
| `seattle-address-points.geojson` | [King County public address points](https://gismaps.kingcounty.gov/arcgis/rest/services/Address/KingCo_AddressPoints/MapServer/0), Seattle jurisdiction, with only PIN, street address, object ID, and point geometry. Used for offline reverse geocoding. |
| `common-property.geojson` | [Assessor parcel extract](https://gis-kingcounty.opendata.arcgis.com/datasets/parcel-record-assessor-extract-table/explore), `COMMONPROPERTY <> 0` (may be empty) |
| `public-owned-parcels.geojson` | All county public-ownership layer records, including owner categories and agency names. Downloaded once and reused. Supersedes the earlier school-only supplement. |

Downloads use a complete object-ID list and verify every returned batch, rather
than assuming a single API response contains every record. Only relevant fields
are downloaded, not taxpayer names or mailing addresses. Each generated snapshot
includes its query, source URL, retrieval date and expected record count.
GIS name/constraint supplements are also cross-checked. Non-null bulk fields retain
precedence, including explicit zero/negative flags; GIS fills only missing fields.

Existing external GeoJSON files can be placed at these same paths. They must
contain the fields listed in `DatasetCache.cs`, retain string parcel/account
identifiers (including leading zeros), and use longitude/latitude coordinates.
Missing `snapshot` metadata is allowed but flagged as unverified provenance.
For `common-property.geojson`, include only records with nonzero
`COMMONPROPERTY`. Shapefiles, standalone CSV imports and file geodatabases are
not supported; the manifest-based assessor ZIP/CSV snapshot is supported.
For `public-owned-parcels.geojson`, retain the public owner categories published
by the county (city, county, state, federal, school, port, regional transit authority,
and Vashon park district). Unknown categories are rejected rather than silently accepted.

The CLI also reads the repository's `reviewed-exclusions.json` on every run.
Each entry contains a numeric `pin`, an exclusion `reason`, and review `evidence`.
These explicit observations override misleading assessor classifications without
inventing a broader class rule, including reviewed construction sites without
an explicit construction marker and reviewed apartment buildings whose entire
same-name group has misleading zero improvement valuations, as well as reviewed
completed condominium buildings with zero-valued complex records. The initial entry records a user-observed rail
parcel whose assessor use is marked vacant. Applied entries are preserved in `summary.json`, and
the configured entries appear in the GeoJSON metadata. The file is required
(use `[]` for none); missing, malformed or duplicate entries stop the export.
Custom `--data-dir` / `--output-dir` paths do not change this review-file location.

## Filtering and output

`exports\empty-parcels.geojson` contains one feature per PIN, including ambiguous
candidates with `status: "needs_review"` and explicit warnings.
It also includes `aerialGroups` metadata for one future photo view per connected
piece of eligible land. `exports\empty-parcel-groups.geojson` supplies those groups
as unioned polygons, deterministic IDs, member PINs, review status, category counts,
and WGS84 crop bounds (`west, south, east, north`). Parcel eligibility is unchanged.

Grouping follows chains of shared boundaries (at least 1 metre within the existing
0.25-metre rounding tolerance) and groups overlapping eligible footprints. Names,
ownership and tax-account links are not required. Corner-only contact or separated
parcels do not connect, and excluded parcels cannot bridge groups. Polygon unions
retain real gaps and holes; crop padding and imagery selection are left to the
aerial viewer. Missing/invalid parcel footprints are not given a photo group.
`exports\summary.json` records source dates, counts, exclusions and limitations.
`exports\assessment-exclusions.json` records the parcels excluded by the new
assessment rules, their property names, parsed references, target values/years,
and missing/truncated-reference evidence.
It also includes property names excluded for explicit construction markers or
exact tidal labels, BNSF/Burlington Northern name matches, and Seattle Housing
Authority name matches.
Association references are retained even when another exclusion, such as
restrictive size/shape, takes precedence.
`exports\bulk-assessment-audit.json` records the original GIS and bulk values alongside
the selected merged assessment, source years, revision date, building evidence
and condo coverage, including parcels removed by other/manual exclusions.

### Bulk selection and evidence

- Select eligible ordinary Value History records by tax year and revision date,
  bounded through the extraction year's next tax year. Far-future years are ignored.
  Omitted assessments and unsupported split histories are not summed into ordinary values.
- Split `9` records with tax status `X` and exemption reason `EX` are distinct
  tax-exempt account components, not duplicate revisions of the ordinary account.
  Select their revisions independently and combine appraised values with ordinary
  history only when tax year and revision date match. Taxable improvements can be
  zero while the combined appraised improvements are substantial. Certified account
  totals already include their distinct accounts and are never added again.
  Unmatched/conflicting components remain unresolved; a known exempt improvement
  lower bound above $1,000 can exclude an improved site without claiming an exact total.
  Zero-valued older exempt components do not erase a known ordinary appraisal.
  A positive, consistent certified total across distinct accounts also takes
  precedence over zero/missing history, retaining its actual certified tax year.
  Known positive partial totals remain association evidence without being exported
  as exact appraised values.
- Posted Appraisal History (status `2` or `5`) is the next evidence source; unposted
  values are ignored. The certified Account snapshot is a fallback. For the same
  year/day, Value History takes precedence, then posted appraisal, then accounts.
  Same-key conflicting values remain unresolved rather than selecting the largest.
- Cross-check that bulk assessment against GIS. A positive improvement value wins
  over zero or null even when it comes from an older tax year. When both sources
  are positive, retain bulk; otherwise retain a known bulk value or fill it from
  non-null GIS data. Deduplicate GIS accounts and reject inconsistent amounts/years
  rather than summing repeated parcel rows. The exported `assessment` identifies
  the selected source and selection reason; `assessor.Assessment` retains raw bulk
  evidence. Source tax years describe the chosen value, not an inferred newer appraisal.
- Published source years are retained in `taxRollYears`; `asOfTaxRollYear` identifies
  the comparison horizon. Unchanged values can carry forward through that horizon.
  This does not claim every parcel received a new appraisal in that year.
- Condo complexes match units by Major. Current distinct units are aggregated only
  when valuation coverage and unit counts are complete. Partial totals are not
  presented as exact values; a known improvement lower bound above $1,000 can
  still exclude the site. Unit values are not added again to a complex assessment.
  A known positive partial-unit total is not replaced with a zero-valued GIS master.
- Residential/commercial/condo/apartment records provide size, building counts,
  year and completion evidence. Percent complete `1` through `99` supplies an
  incomplete-construction exclusion unless the latest assessment indicates
  destroyed property. Garage building/section uses and floating-home condo
  types support the existing garage/houseboat exclusions.
- Completed building records with low/unknown values are flagged for review,
  not blanket-excluded: the small/low-value-structure category remains in scope.
  Records can lag actual construction or demolition.
- Accessory values, commercial sections/features, unit breakdowns and permit
  records are evidence, not extra appraised value to sum. Only the five latest
  assessor permit records are retained per parcel, with non-contact detail fields.
  An issued permit alone is not proof that work began. Owner/contact data is
  not imported from account or permit files.

- Vacant use codes: `299`, `300`, `301`, `309`, `316`.
  These parcels are excluded when selected appraised improvements exceed $1,000,
  recorded as `vacant_use_with_valuable_improvements` in the exclusion audit.
  User review found associated occupied sites and construction/redevelopment
  parcels in this class; a vacant use label does not override valuable improvements.
  Low/non-nominal and unresolved values retain their existing eligibility/review rules.
- Post Office/Post Service (`189`) parcels are excluded even when their assessed
  improvements are zero or missing. Reviewed auto-dealership sites that are
  misclassified as vacant are excluded through persistent reviewed evidence.
- Standalone commercial parking use code: `180`, independent of improvement value.
  Associated/accessory parking (`159`) is excluded.
- All `Townhouse Plat` (`29`) records are excluded, including those with
  missing assessments. This is a user-selected class exclusion, not a claim
  that every townhouse plat is currently under construction.
- Explicit construction labels in property names (`under const`, `underconst`,
  `under constr`, `under construction`, `U/C`, or `construction in progress`)
  trigger `construction_marked_name` exclusions. The full matched property
  names are preserved in the assessment-exclusion audit. These labels can
  persist after completion, so they are not a real-time construction status.
  `NEW_CONSTR` is not used as proof of current activity, and ordinary mentions
  such as a construction-company name do not trigger this rule.
- Low-value improvements: the selected appraised parcel/site improvement value is
  **$0 through $1,000 inclusive**, excluding nominal values. Certified-account
  fallbacks deduplicate account numbers before summing. Taxable improvements
  after exemptions are deliberately not used.
- Tax-exempt bulk records can contain zero appraised values while GIS and KCA retain
  positive appraisals. Those zeros do not prove vacancy. Government ownership is
  excluded separately: the public-ownership GIS layer is incomplete, so verified
  government parcels missing from it are retained as reviewed exclusions.
- An account assessed at exactly **$1,000** is treated as nominal, not evidence
  of insignificant structures, and excluded even if its present-use code says
  vacant or parking, except for the explicitly approved showroom-neighbor
  review exception described below. King County's 2026 commercial appraisal reports describe
  this nominal allocation when property is not at its highest and best use or
  income-based property value falls below land value.
- Property names such as `imps on -0068`, `improvements carried on 0060`, and
  `Imp Data on Parcel #952810-4735`, as well as `OFFICE DATA ON 0410` and
  `ASSOC W-246840-0005, 0025, 0035`, trigger a cross-parcel exclusion across
  **all categories**. Four-digit references inherit the source's six-digit
  major, or the most recent full PIN's major within a reference list. Full PINs
  can refer to another major. Association wording must be followed by a numeric
  parcel reference; generic phrases such as "land associated with office" alone
  do not imply an assessment link. Self-references alone do not
  trigger exclusion. Missing targets and truncated references remain excluded
  conservatively rather than being mistaken for empty land.
- Explicit labels such as `ASSOCIATE PARCEL`, `ASSOCIATED PARCEL` and
  `ASSOCIATED LAND` also trigger exclusion when no target PIN is provided.
  Notes such as `Assoc Land - See Minor 3850` resolve the unqualified minor
  within the source's major. These are recorded in the assessment audit as
  `associated_parcel_label`, with missing targets marked unparsed.
- Names such as `UNDERLYING LAND PARCEL OF SELLEN BLDG (0070)` identify land
  underlying another building parcel. The parenthesized reference is resolved
  using the source Major and recorded as `underlying_building_reference`.
  These parcels are excluded as cross-parcel associations; unrelated years or
  bare parenthesized numbers without the underlying-building wording do not qualify.
- Typed cross-references such as `SEE MI 1035`, `SEE MINOR #1035`, and
  `SEE PARCEL 859090-1035` resolve other parcels and are recorded as
  `see_parcel_reference`. `MI` is recognized as the county's minor abbreviation;
  interest fractions outside the reference parentheses are not parcel numbers.
  Unqualified `see` notes and legitimate self-references do not trigger this rule.
- Short `w/` parcel lists such as `Skyline First Hill w/0825, 0826, ...`
  are resolved using the source Major, or an explicit full PIN's Major.
  They trigger `validated_with_parcel_list` only when a listed parcel is a
  known improvement-bearing target in the same comparison year. Single
  references such as `PRECISION SHEET METAL (w/0565)` also resolve when the PIN
  ends the note or precedes a closing parenthesis, using the same target-value
  validation; these are recorded as `validated_with_parcel_reference`.
  An optional `#` prefix is supported in both single references and lists,
  such as `J Pan Apartments (w/ #0820)` or `w/#0820, #0825`.
  The slash-free `w #0195` variant is supported under the same validation rules;
  without a slash, the `#` marker is required.
  Unit counts, square-footage descriptions, unvalidated numbers, and self-references
  do not qualify.
- Explicit `Together w/ #0740 & #0745` or `together with #0740` parcel notes
  identify a shared site and are recorded as `together_parcel_reference`.
  Unlike a bare `w/` reference, these exclude the source across all categories
  even when the referenced parcels have zero or unresolved improvements.
  The source can be the improvement-bearing parcel mislabeled as parking.
  Ordinary phrases without numeric parcel references and self-references alone
  do not trigger this rule.
- Explicit `SAME OWNERSHIP W/ACCTS #817010-0120 & #817010-0135` notes resolve
  listed parcels and exclude the source as `same_ownership_parcel_reference`,
  including when the referenced values are nominal or unknown. `with`, account,
  parcel and PIN labels are supported. This is a conservative property-name
  association rule, not a general owner-name lookup or proof of building coverage.
  Generic same-ownership wording without parcel references and self-references
  alone do not trigger it. Twelve-digit tax-account IDs are not guessed into PINs.
- `Econ unit` / `Econ. Unit` / `Economic unit` notes resolve economic-unit
  associations without requiring specific wording such as `to Minor`.
  `includes` lists, `with` references, and `(P)`/`(S)` role markers are supported;
  notes without a resolvable target remain conservatively excluded. These are
  recorded as `economic_unit_reference`. Explicit `- SUBSIDIARY` role suffixes
  resolve any listed parcels, or remain conservatively excluded if no target is
  named (`subsidiary_parcel_reference`). Explicit `SUBSIDIARY W/0145` and
  `subsidiary with P 0600, 0605` references also resolve targets, without requiring
  a preceding hyphen; `P` and `(P)` primary-role markers are supported.
  Ordinary subsidiary-company names do not trigger this rule. These associations
  remain excluded even when a neighbor could otherwise override size/shape.
- User-corrected associations and manually inspected associated sites without
  reliable name evidence remain persistent reviewed exclusions. In particular,
  a mistaken self-reference is corrected through reviewed evidence, not by
  treating all legitimate self-references as cross-parcel allocations.
- Explicit primary-parcel notes such as `primary parcel: -1725` and
  `Secondary w/Primary on 9115` are resolved
  and excluded even when the referenced parcel has only a nominal $1,000 value
  or its value is unknown. The audit identifies these as
  `primary_parcel_reference`. A reference to the source PIN itself is ignored;
  the word "primary" without a numeric parcel reference does not trigger this rule.
- A second, wording-independent pass scans names for full PINs, `major-minor`
  PINs, and explicit `-minor` references. It catches notes such as `SEE
  684070-1735 FOR IMPS`, `Assoc Land for 005300-0005`, or `Primary -4280`
  without adding exceptions for individual parcels. This pass requires the
  referenced PIN to exist in the parcel snapshot and carry improvements above
  $1,000 in the same as-of comparison tax year. Published source years remain
  visible separately. It ignores unqualified four-digit numbers,
  unresolved targets and self-references. The audit records its detection
  method as `validated_structured_pin`.
- Same-name, same-as-of-tax-year, boundary-connected parcels are treated as a site when
  another parcel in the connected group carries more than $1,000 of improvements.
  Names must match exactly after case/whitespace normalization; punctuation is
  retained. The rule follows chains of same-name parcels, so a candidate need
  not directly touch the improvement-bearing parcel. It never uses the candidate
  itself as evidence of improvements elsewhere.
  Boundary contact requires at least 1 metre, with a 0.25 metre tolerance for
  GIS rounding, using a local Seattle metre approximation. Corner-only contact,
  distant same-name parcels, missing comparison years, and nominal
  $1,000 targets do not qualify. Missing/invalid geometries are not bridged;
  unresolved PINs are logged and recorded in the summary.
  The assessment audit records `shared_name_site_improvements`, the normalized
  name, year, connected PINs and distinct improvement-bearing PINs, along with
  the target values. This is a conservative user-selected exclusion: shared
  site association does not prove every parcel is physically occupied.
- These two exclusions cannot currently be overridden by a present-use code;
  the exporter does not yet ingest independent evidence of physical vacancy
  or surface parking. Other ambiguous cases still remain flagged in the export.
- Parking garages (`182`), park/greenbelt uses (`149`, `150`, `328`, `331`),
  mapped Parks properties, and mineral/mining-rights records are excluded.
- Assessor highest and best use **as if vacant** `PARK/RECREATION`
  (`HBUAsIfVacant = 9`, lookup type `103`) also excludes a parcel, even if its
  present-use code incorrectly says vacant residential land. Both highest-and-best-use
  codes are retained in `assessor.Parcel`, and exclusions appear as
  `park_recreation_highest_best_use` in the assessment audit. This is a specific
  park/recreation policy, not a general claim that highest and best use proves
  current activity. Highest and best use **as improved** `PRESENT USE`
  (`HBUAsImproved = 1`, lookup type `104`) does not trigger exclusion on its own.
- Water polygons with a `HYDR` PIN suffix are excluded before evaluating missing
  valuations. The county's [parcel metadata](https://www5.kingcounty.gov/sdc/?Layer=parcel_area)
  identifies these as water placeholders, not tax parcels. Parcels explicitly
  classified as `Water Body, Fresh` (`337`) or `River/Creek/Stream` (`333`) are
  also excluded. Other shoreline parcels are not removed solely because they touch water;
  this version does not perform a spatial test for complete submersion.
  Both tideland classes (`334`, `335`) are excluded by their present-use codes.
- The assessor's separate `TIDELANDSHORELAND = 3` flag (tidelands/shorelands
  only) also excludes parcels, even if their present-use code says vacant.
  Code `2` (uplands with tidelands/shorelands), unknown flag values, and a
  historical tide-land plat name alone do not trigger this rule. This avoids
  treating every filled/developed parcel in the Seattle Tide Lands plat as tidal.
- An exact property name `TIDELAND` or `TIDELANDS` (case-insensitive, ignoring
  surrounding whitespace) also excludes a parcel, including mixed-code records
  whose present use says vacant. Longer names mentioning historic tide lands
  do not trigger this name-only rule. Matched names are preserved in the audit.
- Names identifying `BNSF` or `Burlington Northern` (including hyphenated and
  full Burlington Northern Santa Fe variants) are excluded across all categories.
  Generic "Burlington" or "Santa Fe" names alone are not matched. The audit
  retains the matched name under `bnsf_property_name`. This is name-based
  association, not comprehensive ownership matching: the public parcel sources
  omit full owner names, and differently named BNSF-owned parcels can be missed.
- Property names containing `SEATTLE HOUSING AUTHORITY` or the standalone
  abbreviation `SHA` (including `MF- SHA- EXEMPT` and `Olive Ridge-SHA`)
  are excluded case-insensitively across all categories. The assessment audit
  records `seattle_housing_authority_property_name` and the source property names.
  Neighbor exceptions cannot override this exclusion. `SHA` inside unrelated
  words such as `SHARED` or `SHADOW` does not match. Other abbreviations such as
  `SEATTLE HOUSING AUTH` are not inferred; this is not an ownership lookup.
- All public/government-owned parcels are excluded using the county's
  [public-ownership layer](https://gismaps.kingcounty.gov/arcgis/rest/services/Property/KingCo_PropertyInfo/MapServer/7)
  even when the assessor present-use code says vacant or commercial parking.
  This user-selected policy can exclude genuinely vacant public land. The audit
  records `government_owned_land` (or `school_owned_land` for school ownership),
  owner classifications and agency names, and the source snapshot. The generic
  tax-exempt property type alone is not used as ownership evidence. This derived
  ownership dataset may lag ownership changes.
- Tract placeholder minors beginning with `T` (including `TRCT` and `TR6A`) and
  unknown placeholder minors `UNKN`/`UNK` are excluded. A real numeric PIN with
  missing valuation data still remains a review candidate. Public ownership
  placeholders (`PUBL`, which have no tax parcel number) are now also excluded;
  the placeholder rule itself does not use numeric-PIN ownership. The separate
  public-ownership rule above excludes identified government-owned parcels.
- Religious/welfare use (`165`), hospital use (`173`), cemetery/mortuary/crematory
  use (`179`), public/private schools (`184`, `185`), air terminals (`247`),
  rail terminals (`261`), auto/bus/other terminals (`264`),
  marine terminals (`262`, `271`), public utilities (`266`), private radio/TV
  utilities (`267`), and all rights-of-way/utility-road parcels (`332`) are
  excluded. A rail-related property name is no longer required for code `332`.
  Golf course (`143`) and sport facility (`156`) uses are also excluded.
  Marina (`146`) and houseboat (`7`) present-use classes are excluded regardless
  of their improvement values.
- All associated/accessory parking (`159`) is excluded regardless of property
  name or appraisal value. This replaces the earlier medical/school-specific
  parking rules. Standalone commercial parking (`180`) remains eligible subject
  to the other exclusions.
- Auto showroom/display lots (`161`) are excluded. Standalone commercial parking
  (`180`) is also excluded when its business-name words match a parcel actually
  classified as an auto showroom/lot. For example, `Toyota of Lake City (Parking)`
  and `Lake City Toyota Service Garage` match `Toyota of Lake City`. The matcher
  ignores punctuation, word order, common business/parking/service descriptors
  and parenthetical notes, and requires at least two remaining distinct words.
  Generic names such as "Vacant" and unrelated standalone parking do not match.
  This is not a vehicle-brand blacklist or an ownership lookup; differently
  named dealership lots can still be missed. The audit retains the matched
  names and dealership PINs under `dealership_parking_name_match`.
- All parcels with assessor `TOPOGRAPHY = 1` (Topography: YES) are excluded by
  user-selected policy. This narrows the inventory by a development constraint:
  the flag alone does **not** prove a building exists or that the lot is unusable.
  Other topography values are not interpreted as YES.
- All parcels with assessor `WATERPROBLEMS = 'Y'` (Water Problems: YES) are also
  excluded by user-selected policy. This can include dry land with water-related
  constraints; it is **not** a spatial test for being underwater. Neither rule
  treats missing flag values as YES.
- All parcels with assessor `RESTRICTIVESZSHAPE = 1` (Restrictive Size Shape:
  YES) are normally excluded by user-selected policy. Like the other constraint flags,
  this does not prove occupancy or unusability. Missing values are not treated as YES.
- A **neighbor-based review exception** applies to otherwise eligible parcels
  blocked only by Restrictive Size Shape or Auto Showroom and Lot (`161`).
  The user also approved the specific showroom-plus-nominal-$1,000 combination.
  All other exclusions still apply, including additional constraint flags,
  parks/public ownership, construction, garages, and building associations.
  Both the candidate and its anchor must have known improvements at or below
  $1,000 and complete valid geometry. Anchors are originally accepted parcels,
  not newly rescued ones: no chain propagation. Shared boundary must be at least
  1 metre with the existing 0.25-metre rounding tolerance; corner-only contact
  and overlapping footprints do not qualify. Restored records are marked
  `needs_review`, retain their original attributes, and include neighboring PINs
  and overridden exclusions in `neighborException` and the summary.
- Any **positive-area** intersection with a mapped park excludes the whole
  parcel. Touching a park edge does not. Explicit Parks PIN matches also exclude.
- Repeated rows for the same tax account are deduplicated, not summed. Different
  account values or tax roll years make the total unknown and flag the candidate.
- Missing values are never treated as zero. Unknown valuations enter the list
  only as review candidates. Condominium/stacked accounts and known common
  property are flagged; their tax values do not prove the underlying site empty.
  The county also publishes alphanumeric tract/public/hydro/unknown IDs,
  including some nine-character IDs; these are preserved and flagged as
  nonstandard rather than padded or discarded.
- All undivided-interest records (`PROPTYPE = 'U'`) are excluded. Their underlying
  GIS parcels may have no linked tax account, value or tax-roll year even when
  related share assessments show a building. This conservative user-selected
  policy may also remove genuinely vacant co-owned land. It does not blanket
  exclude condominium (`K`) records, and it does not aggregate share accounts
  to establish a physical site's value.
- Polygon parts are merged for the output. Missing/invalid parcel geometry is
  flagged, exported as `null`, and marked as unverified for spatial exclusions.
  Invalid park boundaries are repaired **in memory** with NetTopologySuite's
  `GeometryFixer`; source files stay unchanged. Each repair's source file,
  object ID, park name, validation error and resolution appear in
  `parkBoundaryRepairs` in both outputs and in console warnings. A missing Parks
  property shape can use an existing, valid same-name park outline; that fallback
  is also reported. Without that outline, missing/empty geometry stops the
  export, as does unrepairable geometry.

The present-use and building records can lag actual construction/demolition.
Assessor permit records are included, but the separate SDCI permit feed, building
footprints and aerial imagery are not integrated. Retrieval/extraction dates
are not assessment dates. `taxRollYears` preserves the selected bulk source
years from `TaxYr`, `RollYr` and the `BillYr` certified-account fallback.

The former public GIS valuation fields reflect the **most recently certified tax roll**,
not necessarily the latest appraisal shown on the assessor website. The website
distinguishes **Valued Year** from **Tax Year**: a 2026 valuation can appear under
tax year 2027, while the GIS service still publishes the certified 2026 tax roll
with the earlier values. A fresh download therefore does not necessarily capture
recently completed construction.

Current exports instead use the published bulk history and posted-appraisal
records where available. In the November 13, 2026 snapshot, these contain the
2027 improvement values matching the reviewed examples: $1,001,000 for
`8127700280` and $5,119,000 for `8823902795`, despite the older/missing account data.

Exports use `taxRollYears` (formerly mislabeled `valuationYears`) for actual
source years. Shared-name associations use `TaxRollYear` for the common as-of
comparison horizon, not a claim that each value changed in that year.
No valuation year is inferred by subtracting one. `assessmentDataBasis` documents
the comparison cutoff, source extract and remaining snapshot limitations.
Use reviewed exclusions for confirmed occupied/completed buildings absent from
the GIS values. See the county's [tax-account metadata](https://www5.kingcounty.gov/sdc/?Layer=rpacct_extr)
for the certified-roll basis.

Some GIS records omit the tax-account link and assessment values entirely,
even when the assessor website has values for the same tax year. An unresolved
assessment means missing or unusable source inputs, not that the assessor
has never valued the property. Refreshing cannot repair an omission present in
the live source. Verified KCA evidence can support a reviewed exclusion without
silently substituting zero or inventing an assessment value. Bulk parcel history
can supply a known value even when there is no account link in the certified file.
Consult [King County's data terms](https://kingcounty.gov/en/dept/kcit/data-information-services/gis-center/about/terms-conditions-copyrights)
before redistributing or selling derived data.

## Deploy runtime data

Commit these generated files with the application:

- `exports\empty-parcels.geojson`: map polygons, classifications and parcel details.
- `exports\aerial-catalog.json`: aerial groups, parcel IDs, addresses, lot areas and crop bounds.
- `exports\aerial-boundaries.json`: individual parcel outlines for each aerial group.

After a successful `dotnet run -- export`, commit all three together. The aerial
catalog and boundaries must have matching revisions. All other files under
`exports` and the entire `data` directory remain ignored; they are not needed
to serve the website.

Repository-based deployments obtain these files with `git pull`, so no separate
data upload or server-side export is needed. Run the published application with
the repository root as its content root, or set `ParcelMap__ExportDirectory` to
the repository's `exports` directory. Exports are excluded from `dotnet publish`
output, so a deployment containing only published files must supply these three
files separately. Browsers load map libraries and imagery from external services.

## Browse aerial views

```powershell
dotnet run --launch-profile http
```

Open `http://localhost:5262/` for the public aerial-first landing page. It starts
at a random group and uses a native range slider to browse the complete, stable
group catalog. Click the aerial map to focus the slider without scrolling, then
use the arrow keys to move between views. **Refresh** selects a different random
group without reloading the page or changing the parcel-outline preference.
It is disabled when only one group is available. **Show Parcels** is checked by default
and displays only the current group's individual outlines. The address and combined lot area are
centered below the slider; no city line, parcel count, or address-source badge is shown.
An initially collapsed **Parcel IDs** section below the lot area lists every
member PIN, even when **Show Parcels** is unchecked. It closes when changing
views. Each PIN has a **View on Assessor** link beside it that opens the parcel's
King County eRealProperty record in a new tab. A period separates each PIN from
its link; text selection uses the browser's native behavior. Additional addresses
are not displayed.

**View on Google Maps** opens the group's geographic center in satellite view
at a zoom based on the fitted aerial view. It opens in a new tab or the Google
Maps app on supported devices, without an API key. Google supplies its own
imagery; the site's parcel outlines are not transferred.

`exports\aerial-catalog.json` is served by `/api/aerial-groups`. Group boundaries
are fetched on demand at `/api/aerial-groups/{id}/parcels`; they come from
`exports\aerial-boundaries.json`, not live parcel APIs. Both artifacts share a
revision so stale combinations are rejected explicitly. The landing page never
runs an exporter or geocoder, and loads aerial tiles only for the current view.
Missing or empty catalogs and imagery failures provide an explicit retry/error.

Addresses prefer normalized assessor building/apartment/vacant-lot addresses,
then county parcel situs addresses. Cleaning removes extra spaces and trailing
ZIP/ZIP+4/city labels while retaining house/unit numbers and directional streets.
Without a recorded address, the exporter reverse geocodes a footprint interior
point against the downloaded county address-point snapshot within 100 metres.
These results are displayed as **Near [address]**, not claimed as onsite addresses.
No result means **Address unavailable**. Address source, matched PIN/coordinates,
distance, and retrieval time remain in catalog evidence, not public badges.
No remote geocoder batch or paid locator credentials are needed.

Combined lot area sums known distinct member parcel areas when footprints do
not overlap. Overlapping, missing, or conflicting recorded areas use the group
union area in the existing local Seattle metre projection, converted to square
feet and labeled **Approx.** Holes/gaps are preserved; crop rectangles are not lot
areas. Address/area enrichment is cached in `data\aerial-enrichment-v3.json`,
keyed by input hashes and algorithm version; unchanged catalogs reuse it.

## Review parcels in Map view

Open `http://localhost:5262/map/` for the HTML 2D parcel review map. The app serves the existing
`exports\empty-parcels.geojson` at `/api/parcels`; opening the map never downloads
or regenerates GIS datasets. If the export is missing, the map displays instructions
to generate it. For exports stored elsewhere, set `ParcelMap__ExportDirectory`
to that directory before starting the app.

Polygons have colored fills and outlines by category, with an alternative review-status
palette. Use the category filters, review-only toggle, or PIN search to explore the
inventory. Click a boundary to inspect its address, appraised improvements, tax roll
years, inclusion reasons and warnings. Records with missing/invalid geometry stay in
the export and are searchable, but cannot be drawn; their count is shown explicitly.
Details show the parcel's aerial-photo group and all member PINs. **Fit whole
aerial group** previews the shared crop extent without merging individual records.
**Show 2025 aerial imagery** loads the county's public cached map tiles only
around the selected group's extent, with 25% context padding. Parcel outlines
remain above the imagery. **Return to street view** removes the aerial layer.

The map uses canvas rendering for the parcel polygons, Leaflet 1.9.4 from a CDN, and
OpenStreetMap street tiles with attribution. An internet connection is needed for
the map library and basemap, but parcel data is served locally. No aerial imagery
is downloaded or served as original raster data. The aerial preview uses the
[public cached 2025 MapServer](https://gismaps.kingcounty.gov/arcgis/rest/services/BaseMaps/KingCo_Aerial_2025/MapServer),
credited to EagleView Technologies, Inc. and King County. The county
[distinguishes public cached imagery maps from restricted original Image Services](https://state-of-gis.kingcounty.gov/pages/imagery-data~cbce0bc89e6e43ddb7809b1d6fd1baf6).
The imagery is acquired in 2025, may predate current construction/demolition,
and is not authoritative survey data. The service's terms prohibit sale without
written county permission. The review map loads imagery only after selecting
the aerial action; the landing page loads its current aerial view automatically.
Neither page bulk-fetches the imagery for other groups.

Browser interaction regressions run with the built-in Node test runner:

```powershell
node --test tests\aerial-page.test.cjs tests\map-review.test.cjs
```

### Save reviewed exclusions locally

The local map includes an **Exclude parcels** section. Enter one or more
10-digit PINs separated by commas or whitespace, add a note, and select
**Save exclusions**. The whole batch is appended to `reviewed-exclusions.json`
with reason `reviewed_map_exclusion`. Invalid IDs, duplicate IDs, and existing
reviewed exclusions reject the batch without overwriting prior notes.
Notes are included in generated exports; do not enter private information.

Saving starts an in-process background export without rebuilding or stopping
the server. The editor displays progress and prevents concurrent save/export
jobs. It uses cached GIS/address and bulk inputs only; if inputs are missing,
prepare them once with the export CLI. No taxpayer detail pages are requested.

The map continues serving the previous dataset while the new inventory and
aerial artifacts are generated and validated in a staging directory. Publication
briefly holds API reads until the complete file set is installed; replacement
failures roll back the old files. The map reloads after success without changing
the map position or category filters. Commit the review file and the three
runtime exports together as before.

If exporting fails, the saved exclusions remain in place and the editor shows
the error with **Retry export**, which regenerates from the existing review file
without submitting duplicate PINs. **Regenerate exports** also applies reviews
saved before a server restart. If publication and rollback both fail, the API
stops serving the inconsistent dataset and the local server logs identify the
retained backup directory for recovery.

The editor is enabled by `ParcelReview:Enabled` in Development and disabled by
default in Production. Start it locally with
`dotnet run --configuration Release --launch-profile http`. It also requires a
loopback connection and a localhost/loopback Host header; save requests must
come from the same origin. Public/reverse-proxied map views remain read-only.
For a local run without the Development profile, explicitly pass
`--ParcelReview:Enabled true` after `dotnet run --`.
