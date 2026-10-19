# Google Takeout Metadata Remapper

A Windows/.NET utility for translating Google Photos Takeout companion JSON into standard EXIF/XMP metadata in staged copies of the original media.

The tool is deliberately conservative:

- It never changes the Takeout source files.
- It never uploads, deletes, replaces, or renames cloud content.
- It never copies private photos or companion JSON into the repository.
- It defaults to a dry run.
- It reports missing and ambiguous matches instead of guessing.
- It excludes `Instagram` and `Podcast Sharing` paths unless explicitly overridden.
- It verifies written metadata by reading it back with ExifTool.

## Requirements

- Windows 10/11
- .NET 10 SDK or runtime
- [ExifTool](https://exiftool.org/) installed and available on `PATH`, or supplied with `--exiftool`

## Usage

Dry run:

```powershell
dotnet run --project .\src\TakeoutMetadataRemapper -- `
  --source 'C:\path\to\Takeout\Google Photos' `
  --output 'C:\path\to\staging' `
  --limit 100
```

Apply to staged copies. The source remains untouched:

```powershell
dotnet run --project .\src\TakeoutMetadataRemapper -- `
  --source 'C:\path\to\Takeout\Google Photos' `
  --output 'C:\path\to\staging' `
  --limit 100 `
  --apply `
  --time-zone 'Eastern Standard Time'
```

The time zone is explicit because Takeout stores the capture timestamp as a Unix timestamp, while classic EXIF date fields do not carry a reliable time zone. Use a location-appropriate Windows time-zone ID and review the JSON report before upload.

## Pilot workflow

1. Run the dry run and inspect `remap-report.json`.
2. Run `--apply` into a new staging directory.
3. Confirm every applied item reports `Verification.Status = Verified`.
4. Compare a sample in OneDrive Photos.
5. Only then upload the staged media. Do not upload the `.json` sidecars.

## License

MIT. ExifTool is a separate dependency with its own license.
