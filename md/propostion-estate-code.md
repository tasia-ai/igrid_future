# Proposal: Region/Estate Listing Command

## Goal
Add a command that shows all regions along with the estates they belong to, so operators can quickly see region → estate mappings from a single command.

## Proposed Command
```
show region-estates
```

### Example Output
```
Region Name           | Region UUID                           | Estate Name       | Estate ID
---------------------|---------------------------------------|-------------------|---------
Tranquil Bay          | 11111111-1111-1111-1111-111111111111  | Serenity Estates  | 10
Old Harbor            | 22222222-2222-2222-2222-222222222222  | Serenity Estates  | 10
Mountain Pass         | 33333333-3333-3333-3333-333333333333  | Alpine Holdings   | 22
```

## Behavior Details
- Lists **all regions** currently known to the grid.
- Shows the **estate name and estate ID** for each region.
- Sorting (proposal): alphabetical by region name.
- Optional filter (future): `show region-estates <estate-name>` to restrict output to a single estate.

## Suggested Data Sources (Implementation Idea)
- Region list: grid/region database or region management service.
- Estate mapping: estate service (estate ID ↔ estate name).

## Proposed Code Skeleton (Implementation Outline Only)
> This is **not** a full implementation. It shows the key parts that would be needed.

```csharp
// 1) Register the command (example location: grid/console command registry)
AddCommand(
    "show region-estates",
    "List all regions with their estate mappings",
    ShowRegionEstatesCommand);

// 2) Command handler signature (example)
public void ShowRegionEstatesCommand(string[] cmd)
{
    // optional filter: cmd[1] = estate-name (if provided)
    string filterEstate = (cmd.Length > 1) ? cmd[1] : null;

    // 3) Fetch regions and estates
    var regions = RegionService.GetAllRegions(); // e.g., returns list of RegionInfo
    var estates = EstateService.GetAllEstates(); // e.g., returns list of EstateInfo

    // 4) Build lookup: estateId -> estateName
    var estateLookup = estates.ToDictionary(e => e.EstateID, e => e.EstateName);

    // 5) Join and output
    var rows = regions
        .Select(r => new {
            RegionName = r.RegionName,
            RegionId = r.RegionID,
            EstateId = r.EstateID,
            EstateName = estateLookup.ContainsKey(r.EstateID) ? estateLookup[r.EstateID] : "Unknown"
        });

    if (!string.IsNullOrWhiteSpace(filterEstate))
    {
        rows = rows.Where(x => x.EstateName.Equals(filterEstate, StringComparison.OrdinalIgnoreCase));
    }

    foreach (var row in rows.OrderBy(x => x.RegionName))
    {
        Console.WriteLine($"{row.RegionName} | {row.RegionId} | {row.EstateName} | {row.EstateId}");
    }
}
```

## Permissions
- Admin/console only (same scope as other grid-wide listing commands).

## Notes
- This is a documentation-only proposal. No code changes are included.
