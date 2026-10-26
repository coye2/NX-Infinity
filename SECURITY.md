# download checks

The release includes SHA-256 checksums and a Microsoft Defender scan report for the finished ZIPs. Match the filename and hash to check your download.

On Windows:

```powershell
Get-FileHash .\NX-Infinity-v1.0.0-win-x64.zip -Algorithm SHA256
```

To scan a download yourself:

```powershell
./tools/Scan-Release.ps1 -Package ./NX-Infinity-v1.0.0-win-x64.zip -Report ./release-scan.json
```

The report records the scan result, engine, definitions and exact file hash. If your scanner flags a download, include the filename, hash and detection name in your report. A scan result describes that file and those definitions; it isn't a guarantee from every antivirus.
