using Microsoft.EntityFrameworkCore;
using ThermixStudio.Core;

namespace ThermixStudio.Infrastructure;

public sealed class AppDataService(ThermixDbContext dbContext) : IAppDataService
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSchemaUpgradesAsync(cancellationToken);

        if (!await dbContext.Users.AnyAsync(cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Name = "Admin Thermix",
                Email = "admin@thermix.local",
                Role = "Administrator"
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsureSchemaUpgradesAsync(CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync("Thermograms", "ProcessingJson", cancellationToken))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Thermograms ADD COLUMN ProcessingJson TEXT NOT NULL DEFAULT '{{}}'",
                cancellationToken);
        }
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var existing = reader[1]?.ToString();
                if (string.Equals(existing, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyList<Inspection>> GetInspectionsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Inspections.AsNoTracking().OrderByDescending(x => x.StartAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<Inspection> UpsertInspectionAsync(Inspection inspection, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Inspections.FirstOrDefaultAsync(x => x.Id == inspection.Id, cancellationToken);

        if (existing is null)
        {
            dbContext.Inspections.Add(inspection);
        }
        else
        {
            existing.OsNumber = inspection.OsNumber;
            existing.StartAtUtc = inspection.StartAtUtc;
            existing.EndAtUtc = inspection.EndAtUtc;
            existing.TechnicianName = inspection.TechnicianName;
            existing.Sector = inspection.Sector;
            existing.Plant = inspection.Plant;
            existing.Status = inspection.Status;
            existing.Notes = inspection.Notes;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return inspection;
    }

    public async Task<IReadOnlyList<Thermogram>> GetAllThermogramsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Thermograms
            .AsNoTracking()
            .OrderByDescending(x => x.CaptureAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Thermogram>> GetThermogramsByInspectionAsync(Guid inspectionId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Thermograms
            .AsNoTracking()
            .Where(x => x.InspectionId == inspectionId)
            .OrderByDescending(x => x.CaptureAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<Thermogram> AddThermogramAsync(Thermogram thermogram, CancellationToken cancellationToken = default)
    {
        dbContext.Thermograms.Add(thermogram);
        await dbContext.SaveChangesAsync(cancellationToken);
        return thermogram;
    }

    public async Task<Thermogram> UpdateThermogramAsync(Thermogram thermogram, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Thermograms.FirstOrDefaultAsync(x => x.Id == thermogram.Id, cancellationToken);
        if (existing is not null)
        {
            existing.EquipmentTag = thermogram.EquipmentTag;
            existing.EquipmentDescription = thermogram.EquipmentDescription;
            existing.EquipmentLocation = thermogram.EquipmentLocation;
            existing.Criticality = thermogram.Criticality;
            existing.Notes = thermogram.Notes;
            existing.InspectionId = thermogram.InspectionId;
            existing.MetadataJson = thermogram.MetadataJson;
            existing.ProcessingJson = thermogram.ProcessingJson;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return thermogram;
    }

    public async Task<bool> RemoveThermogramAsync(Guid thermogramId, CancellationToken cancellationToken = default)
    {
        var thermogram = await dbContext.Thermograms.FirstOrDefaultAsync(x => x.Id == thermogramId, cancellationToken);
        if (thermogram is null)
        {
            return false;
        }

        var measurements = await dbContext.ThermalMeasurements
            .Where(x => x.ThermogramId == thermogramId)
            .ToListAsync(cancellationToken);

        if (measurements.Count > 0)
        {
            dbContext.ThermalMeasurements.RemoveRange(measurements);
        }

        dbContext.Thermograms.Remove(thermogram);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ThermalMeasurement>> GetMeasurementsByThermogramAsync(Guid thermogramId, CancellationToken cancellationToken = default)
    {
        return await dbContext.ThermalMeasurements
            .AsNoTracking()
            .Where(x => x.ThermogramId == thermogramId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<ThermalMeasurement> AddMeasurementAsync(ThermalMeasurement measurement, CancellationToken cancellationToken = default)
    {
        dbContext.ThermalMeasurements.Add(measurement);
        await dbContext.SaveChangesAsync(cancellationToken);
        return measurement;
    }

    public async Task<bool> RemoveMeasurementAsync(Guid measurementId, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.ThermalMeasurements.FirstOrDefaultAsync(x => x.Id == measurementId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        dbContext.ThermalMeasurements.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TrendPoint>> GetThermogramTrendAsync(Guid thermogramId, CancellationToken cancellationToken = default)
    {
        var measurements = await dbContext.ThermalMeasurements
            .AsNoTracking()
            .Where(x => x.ThermogramId == thermogramId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return measurements.Select(x => new TrendPoint
        {
            DateUtc = x.CreatedAtUtc,
            Temperature = x.Tmax
        }).ToList();
    }

    public async Task<ReportRecord> AddReportRecordAsync(ReportRecord reportRecord, CancellationToken cancellationToken = default)
    {
        dbContext.Reports.Add(reportRecord);
        await dbContext.SaveChangesAsync(cancellationToken);
        return reportRecord;
    }
}