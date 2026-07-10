# MDF to BAK Converter

Windows app for converting a SQL Server `.mdf` file, with an optional `.ldf` file, into a `.bak` backup.

The app copies the source files to a temporary folder before conversion, so it does not modify the original MDF or LDF.

## Build

```powershell
dotnet restore
dotnet run
```

## Notes

- Internet access is needed only when installing LocalDB.
- Use the same or a newer LocalDB version than the MDF version.
- Errors and progress are shown in the Technical log.
