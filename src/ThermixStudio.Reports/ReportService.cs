using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ThermixStudio.Core;

namespace ThermixStudio.Reports;

public sealed class ReportService : IReportService
{
    public ReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<string> BuildPreviewHtmlAsync(ReportRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(BuildHtml(request));

    public async Task<ReportResult> GenerateAsync(ReportRequest request, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var osNumber = string.IsNullOrWhiteSpace(request.Inspection.OsNumber) ? "SEM-OS" : request.Inspection.OsNumber.Trim();
        var installation = string.IsNullOrWhiteSpace(request.InstallationName) ? request.Inspection.Plant : request.InstallationName;
        var equipment = string.IsNullOrWhiteSpace(request.EquipmentName) ? "-" : request.EquipmentName;

        var baseName = $"{osNumber}_{SanitizeFileNamePart(installation)}_{timestamp}";

        var htmlPath = Path.Combine(outputDirectory, $"{baseName}.html");
        var pdfPath = Path.Combine(outputDirectory, $"{baseName}.pdf");

        var html = BuildHtml(request, osNumber, installation, equipment);
        await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8, cancellationToken);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(18);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().ShowOnce().Column(header =>
                {
                    header.Item().AlignCenter().Text("RELATÓRIO TERMOGRÁFICO").Bold().FontSize(17).FontColor(Colors.Red.Darken2);
                    header.Item().AlignCenter().Text($"Relatório Técnico de Inspeção N°: {osNumber}").FontSize(10).FontColor(Colors.Grey.Darken3);
                    header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Red.Darken2);
                    header.Item().PaddingTop(4).Text($"Instalação: {installation}").FontSize(10);
                    header.Item().Text($"Equipamento: {(string.IsNullOrWhiteSpace(equipment) ? "-" : equipment)}").FontSize(10);
                    header.Item().Text($"Data: {request.ReportDate:dd/MM/yyyy}").FontSize(10);
                });

                page.Content().Column(content =>
                {
                    content.Spacing(10);

                    foreach (var section in request.Sections.Select((section, index) => new { section, index }))
                    {
                        content.Item().Column(sectionCol =>
                        {
                            sectionCol.Spacing(6);
                            sectionCol.Item().PaddingTop(6).Text(section.section.Title).Bold().FontSize(12).FontColor(Colors.Red.Darken2);

                            sectionCol.Item().Row(row =>
                            {
                                row.Spacing(10);
                                row.RelativeItem().Column(left =>
                                {
                                    left.Item().Text("IMAGEM IR").Bold().FontSize(10);
                                    if (File.Exists(section.section.Thermogram.FilePath))
                                    {
                                        left.Item().Height(160).Image(section.section.Thermogram.FilePath).FitArea();
                                    }
                                    else
                                    {
                                        left.Item().Border(1).Padding(20).Text("Imagem térmica indisponível.");
                                    }
                                });

                                var visiblePath = ResolveVisiblePath(section.section.Thermogram);
                                row.RelativeItem().Column(right =>
                                {
                                    right.Item().Text("IMAGEM VISUAL").Bold().FontSize(10);
                                    if (!string.IsNullOrWhiteSpace(visiblePath) && File.Exists(visiblePath))
                                    {
                                        right.Item().Height(160).Image(visiblePath).FitArea();
                                    }
                                    else
                                    {
                                        right.Item().Border(1).Padding(20).Text("Imagem visível não vinculada.");
                                    }
                                });
                            });

                            sectionCol.Item().AlignCenter().Text(section.section.Title).FontSize(9).Italic();

                            sectionCol.Item().Table(table =>
                            {
                                var processing = ExtractProcessingState(section.section.Thermogram.ProcessingJson);
                                var hasMeasurements = section.section.Measurements.Count > 0;
                                var globalTmin = hasMeasurements
                                    ? section.section.Measurements.Min(x => x.Tmin)
                                    : processing.LevelMinC;
                                var globalTmax = hasMeasurements
                                    ? section.section.Measurements.Max(x => x.Tmax)
                                    : processing.LevelMaxC;
                                var globalDelta = globalTmin.HasValue && globalTmax.HasValue
                                    ? globalTmax.Value - globalTmin.Value
                                    : (double?)null;

                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(3);
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(3);
                                });

                                AddCell(table, "Tag equipamento");
                                AddCell(table, string.IsNullOrWhiteSpace(section.section.Thermogram.EquipmentTag) ? "-" : section.section.Thermogram.EquipmentTag);
                                AddCell(table, "Criticidade");
                                AddCell(table, section.section.Thermogram.Criticality.ToString());

                                AddCell(table, "Emissividade");
                                AddCell(table, processing.Emissivity.ToString("F2", CultureInfo.InvariantCulture));
                                AddCell(table, "Tmax admissível");
                                AddCell(table, processing.MaxAdmissibleC.HasValue
                                    ? processing.MaxAdmissibleC.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C"
                                    : "-");

                                AddCell(table, "Tmin global");
                                AddCell(table, globalTmin.HasValue ? globalTmin.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C" : "-");
                                AddCell(table, "Tmax global");
                                AddCell(table, globalTmax.HasValue ? globalTmax.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C" : "-");

                                AddCell(table, "ΔT global");
                                AddCell(table, globalDelta.HasValue ? globalDelta.Value.ToString("F1", CultureInfo.InvariantCulture) + " °C" : "-");
                                AddCell(table, "Captura");
                                AddCell(table, section.section.Thermogram.CaptureAtUtc == default
                                    ? "-"
                                    : section.section.Thermogram.CaptureAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
                            });

                            if (section.section.Measurements.Count > 0)
                            {
                                sectionCol.Item().Text("Parâmetros técnicos e medições").Bold().FontSize(10);
                                var spotCounter = 0;
                                var maxAdmissiblePdf = ExtractProcessingState(section.section.Thermogram.ProcessingJson).MaxAdmissibleC;
                                foreach (var measurement in section.section.Measurements.Where(m => m.Type == MeasurementType.Spot))
                                {
                                    spotCounter++;
                                    var spot = measurement.Tmax;
                                    if (maxAdmissiblePdf is { } maxAdmissible && maxAdmissible > 0)
                                    {
                                        var margem = maxAdmissible - spot;
                                        var utilizacao = (spot / maxAdmissible) * 100d;
                                        var status = utilizacao switch
                                        {
                                            >= 100d => "Acima do limite",
                                            >= 90d => "Atenção",
                                            _ => "Normal"
                                        };

                                        sectionCol.Item().Text($"- Spot {spotCounter}: Tspot={spot:F1} °C | Tmax adm={maxAdmissible:F1} °C | Margem={margem:F1} °C | Uso={utilizacao:F1}% | {status}");
                                    }
                                    else
                                    {
                                        sectionCol.Item().Text($"- Spot {spotCounter}: Tspot={spot:F1} °C | Tmax adm= - | Status: Sem referência");
                                    }
                                }

                                if (spotCounter == 0)
                                {
                                    sectionCol.Item().Text("- Sem medições de Spot cadastradas para este termograma.");
                                }
                            }

                            sectionCol.Item().Text("Parecer técnico e recomendações").Bold().FontSize(10);
                            sectionCol.Item().Text(string.IsNullOrWhiteSpace(section.section.Observations) ? "-" : section.section.Observations);
                            sectionCol.Item().Text("Ação recomendada").Bold().FontSize(10);
                            sectionCol.Item().Text(string.IsNullOrWhiteSpace(section.section.Recommendation) ? "-" : section.section.Recommendation);

                            if (section.index < request.Sections.Count - 1)
                            {
                                sectionCol.Item().PaddingTop(4).PageBreak();
                            }
                        });
                    }

                    content.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    content.Item().Text($"Parecer técnico geral: {request.TechnicalOpinion}");
                    content.Item().Text($"Ação recomendada geral: {request.RecommendedAction}");
                });

                page.Footer().AlignRight().Text(txt =>
                {
                    txt.Span("Gerado por Thermix Studio em ");
                    txt.Span(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).SemiBold();
                });
            });
        });

        document.GeneratePdf(pdfPath);

        return new ReportResult
        {
            HtmlPath = htmlPath,
            PdfPath = pdfPath
        };
    }

    private static string BuildHtml(ReportRequest request)
    {
        var osNumber = string.IsNullOrWhiteSpace(request.Inspection.OsNumber) ? "SEM-OS" : request.Inspection.OsNumber.Trim();
        var installation = string.IsNullOrWhiteSpace(request.InstallationName) ? request.Inspection.Plant : request.InstallationName;
        var equipment = string.IsNullOrWhiteSpace(request.EquipmentName) ? "-" : request.EquipmentName;

        return BuildHtml(request, osNumber, installation, equipment);
    }

    private static string BuildHtml(ReportRequest request, string osNumber, string installation, string equipment)
    {
        var template = LoadTemplate();
        var processingState = request.Sections.Count > 0
            ? ExtractProcessingState(request.Sections[0].Thermogram.ProcessingJson)
            : new ThermalProcessingState();

        var sectionsHtml = new StringBuilder();

        for (var index = 0; index < request.Sections.Count; index++)
        {
            var section = request.Sections[index];
            sectionsHtml.Append(BuildSectionHtml(section, index + 1, index < request.Sections.Count - 1));
        }

        return template
            .Replace("{{OS_NUMERO}}", WebUtility.HtmlEncode(osNumber))
            .Replace("{{INSTALACAO_NOME}}", WebUtility.HtmlEncode(installation))
            .Replace("{{EQUIPAMENTO}}", WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(equipment) ? "-" : equipment))
            .Replace("{{DATA_RELATORIO}}", WebUtility.HtmlEncode(request.ReportDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)))
            .Replace("{{SECOES_RELATORIO}}", sectionsHtml.ToString())
            .Replace("{{PARECER_TECNICO}}", WebUtility.HtmlEncode(request.TechnicalOpinion))
            .Replace("{{ACAO_RECOMENDADA}}", WebUtility.HtmlEncode(request.RecommendedAction))
            .Replace("{{RODAPE}}", WebUtility.HtmlEncode($"Gerado por Thermix Studio - {DateTime.Now:dd/MM/yyyy HH:mm}"))
            .Replace("{{PALLETA_PASTA}}", WebUtility.HtmlEncode(processingState.Palette.ToString()));
    }

    private static string BuildSectionHtml(ReportSectionRequest section, int index, bool isLast)
    {
        var ptBr = new CultureInfo("pt-BR");
        var processing = ExtractProcessingState(section.Thermogram.ProcessingJson);
        var metadata = ExtractMetadata(section.Thermogram.MetadataJson);
        var visiblePath = ResolveVisiblePath(section.Thermogram);
        var thermalMarkup = BuildImageMarkup(section.Thermogram.FilePath, "Termograma", "Imagem térmica não vinculada.");
        var visibleMarkup = BuildImageMarkup(visiblePath, "Foto Visual", "Imagem visual não vinculada.");

        var formattedEmissivity = processing.Emissivity.ToString("F2", ptBr);
        var globalTminValue = section.Measurements.Count == 0 ? processing.LevelMinC : section.Measurements.Min(x => x.Tmin);
        var globalTmaxValue = section.Measurements.Count == 0 ? processing.LevelMaxC : section.Measurements.Max(x => x.Tmax);
        var formattedGlobalTmin = globalTminValue.HasValue ? globalTminValue.Value.ToString("F1", ptBr) + " °C" : "-";
        var formattedGlobalTmax = globalTmaxValue.HasValue ? globalTmaxValue.Value.ToString("F1", ptBr) + " °C" : "-";
        var formattedDeltaGlobal = globalTminValue.HasValue && globalTmaxValue.HasValue
            ? (globalTmaxValue.Value - globalTminValue.Value).ToString("F1", ptBr) + " °C"
            : "-";
        var formattedMaxAdmissible = processing.MaxAdmissibleC.HasValue
            ? processing.MaxAdmissibleC.Value.ToString("F1", ptBr) + " °C"
            : "-";

        var formattedCaptureAt = section.Thermogram.CaptureAtUtc == default
            ? "-"
            : section.Thermogram.CaptureAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", ptBr);

        var formattedLevelRange = FormatRange(processing.LevelMinC, processing.LevelMaxC, ptBr);
        var formattedAmbient = FormatNullable(metadata.AmbientTemperatureC, "°C", ptBr);
        var formattedReflected = FormatNullable(metadata.ReflectedTemperatureC, "°C", ptBr);
        var formattedHumidity = FormatNullable(metadata.RelativeHumidity, "%", ptBr);
        var formattedDistance = FormatNullable(metadata.ObjectDistanceM, "m", ptBr);

        var cameraModel = string.IsNullOrWhiteSpace(metadata.CameraModel) || metadata.CameraModel == "Unknown"
            ? section.Thermogram.CameraModel
            : metadata.CameraModel;

        var criticalityMarkup = BuildCriticalityMarkup(section.Thermogram.Criticality);

        var hasAdmissibleReference = processing.MaxAdmissibleC.HasValue && processing.MaxAdmissibleC.Value > 0;
        var measurementColumnMarkup = hasAdmissibleReference
            ? """
                <colgroup>
                    <col style="width: 12%;">
                    <col style="width: 16%;">
                    <col style="width: 24%;">
                    <col style="width: 16%;">
                    <col style="width: 14%;">
                    <col style="width: 18%;">
                </colgroup>
            """
            : """
                <colgroup>
                    <col style="width: 14%;">
                    <col style="width: 18%;">
                    <col style="width: 68%;">
                </colgroup>
            """;
        var sectionMeasurementsSimplified = BuildMeasurementRows(section.Measurements, processing.MaxAdmissibleC, ptBr, hasAdmissibleReference);

        return $"""
        <section class="report-section{(isLast ? string.Empty : " page-break")}">
            <div class="section-header">
                <div class="red-bar"></div>
                <div class="section-title">REGISTROS FOTOGRÁFICOS - TERMOGRAMA {index}</div>
            </div>
            <table class="image-table" style="width: 100%; border-collapse: separate; border-spacing: 10px 0; table-layout: fixed; margin-bottom: 20px;">
                <tr>
                    <td style="width: 50%; vertical-align: top; border: 1px solid #000; padding: 0;">
                        <div style="height: 220px; background: #000; text-align: center; vertical-align: middle; line-height: 220px;">
                            {thermalMarkup}
                        </div>
                        <div class="img-footer">Imagem Térmica (IR)</div>
                    </td>
                    <td style="width: 50%; vertical-align: top; border: 1px solid #000; padding: 0;">
                        <div style="height: 220px; background: #000; text-align: center; vertical-align: middle; line-height: 220px;">
                            {visibleMarkup}
                        </div>
                        <div class="img-footer">Imagem Visual (Luz Visível)</div>
                    </td>
                </tr>
            </table>

            <div class="section-header">
                <div class="red-bar"></div>
                <div class="section-title">PARÂMETROS TÉCNICOS E MEDIÇÕES</div>
            </div>

            <table class="data-table">
                <tr>
                    <td class="label">Tag equipamento:</td>
                    <td>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(section.Thermogram.EquipmentTag) ? "-" : section.Thermogram.EquipmentTag)}</td>
                    <td class="label">Criticidade:</td>
                    <td>
                        <div class="checkbox-area">
                            {criticalityMarkup}
                        </div>
                    </td>
                </tr>
                <tr>
                    <td class="label">Emissividade:</td>
                    <td>{formattedEmissivity}</td>
                    <td class="label">Tmax admissível:</td>
                    <td>{formattedMaxAdmissible}</td>
                </tr>
                <tr>
                    <td class="label">Tmin global:</td>
                    <td>{formattedGlobalTmin}</td>
                    <td class="label">Tmax global:</td>
                    <td>{formattedGlobalTmax}</td>
                </tr>
                <tr>
                    <td class="label">ΔT global:</td>
                    <td>{formattedDeltaGlobal}</td>
                    <td class="label">Captura:</td>
                    <td>{WebUtility.HtmlEncode(formattedCaptureAt)}</td>
                </tr>
                <tr>
                    <td class="label">Câmera:</td>
                    <td>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(cameraModel) ? "-" : cameraModel)}</td>
                    <td class="label">Localização:</td>
                    <td>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(section.Thermogram.EquipmentLocation) ? "-" : section.Thermogram.EquipmentLocation)}</td>
                </tr>
                <tr>
                    <td class="label">Escala visual:</td>
                    <td>{formattedLevelRange}</td>
                    <td class="label">Distância:</td>
                    <td>{formattedDistance}</td>
                </tr>
                <tr>
                    <td class="label">T. ambiente:</td>
                    <td>{formattedAmbient}</td>
                    <td class="label">T. refletida:</td>
                    <td>{formattedReflected}</td>
                </tr>
                <tr>
                    <td class="label">Umidade:</td>
                    <td>{formattedHumidity}</td>
                    <td class="label">Imagem visual:</td>
                    <td>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(visiblePath) ? "Não vinculada" : "Vinculada")}</td>
                </tr>
            </table>

            <table class="measurement-table">
                {measurementColumnMarkup}
                <thead>
                    <tr>
                        <th>Spot</th>
                        <th>Tspot (°C)</th>
                        {(hasAdmissibleReference
                            ? "<th>Tmax admissível (°C)</th><th>Margem (°C)</th><th>Utilização</th><th>Status</th>"
                            : "<th>Referência</th>")}
                    </tr>
                </thead>
                <tbody>
                    {sectionMeasurementsSimplified}
                </tbody>
            </table>
            <div class="measurement-note">
                <strong>Margem (°C):</strong> diferença entre o Tmax admissível e o Tspot (Margem = Tmax admissível - Tspot).<br>
                <strong>Utilização:</strong> percentual de uso do limite (Utilização = Tspot / Tmax admissível × 100).
            </div>

            <div class="section-header">
                <div class="red-bar"></div>
                <div class="section-title">OBSERVAÇÕES E RECOMENDAÇÕES</div>
            </div>

            <table>
                <tr><td class="label">Observações:</td></tr>
                <tr><td class="text-area">{WebUtility.HtmlEncode(section.Observations)}</td></tr>
                <tr><td class="label">Ação Recomendada:</td></tr>
                <tr><td class="text-area">{WebUtility.HtmlEncode(section.Recommendation)}</td></tr>
            </table>
        </section>
        """;
    }

    private static string BuildCriticalityMarkup(EquipmentCriticality criticality)
    {
        static string Item(string label, bool isChecked)
            => $"<span><span class=\"box{(isChecked ? " checked" : string.Empty)}\"></span>{label}</span>";

        return string.Join(string.Empty, new[]
        {
            Item("Baixa", criticality == EquipmentCriticality.Low),
            Item("Média", criticality == EquipmentCriticality.Medium),
            Item("Alta", criticality == EquipmentCriticality.High),
            Item("Crítica", criticality == EquipmentCriticality.Critical)
        });
    }

    private static string ResolveVisiblePath(Thermogram thermogram)
    {
        var processing = ExtractProcessingState(thermogram.ProcessingJson);
        return processing.VisibleImagePath ?? ExtractVisiblePathFromMetadata(thermogram.MetadataJson) ?? string.Empty;
    }

    private static string ToBase64DataUri(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var base64 = Convert.ToBase64String(bytes);
            var mimeType = GetMimeType(path);
            return $"data:{mimeType};base64,{base64}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tiff" or ".tif" => "image/tiff",
            _ => "image/unknown"
        };
    }

    private static string BuildImageMarkup(string? sourcePath, string alt, string placeholderText)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            var imageDataUri = ToBase64DataUri(sourcePath);
            if (!string.IsNullOrWhiteSpace(imageDataUri))
            {
                return $"<img src=\"{imageDataUri}\" style=\"display: inline-block; width: 100%; height: 220px; object-fit: contain; object-position: center; vertical-align: middle; background: #000; line-height: normal;\" alt=\"{WebUtility.HtmlEncode(alt)}\">";
            }
        }

        return $"<div class=\"img-placeholder\" style=\"height: 220px; line-height: 220px; color: #666; font-size: 10pt; background: #1a1a1a;\">{WebUtility.HtmlEncode(placeholderText)}</div>";
    }

    private static string BuildMeasurementRows(IReadOnlyList<ThermalMeasurement> measurements, double? maxAdmissibleC, CultureInfo culture, bool hasAdmissibleReference)
    {
        // Filtrar apenas Spots - a única ferramenta de medição
        var spotMeasurements = measurements.Where(m => m.Type == MeasurementType.Spot).ToList();

        if (spotMeasurements.Count == 0)
        {
            var colspan = hasAdmissibleReference ? 6 : 3;
            return $"<tr><td colspan=\"{colspan}\" class=\"measurement-empty\">Sem medições de Spot cadastradas para este termograma.</td></tr>";
        }

        var rows = new StringBuilder();
        var spotCounter = 0;
        var formattedAdmissible = maxAdmissibleC.HasValue
            ? maxAdmissibleC.Value.ToString("F1", culture) + " °C"
            : "-";

        foreach (var measurement in spotMeasurements)
        {
            spotCounter++;
            var measurementName = $"Spot {spotCounter}";
            var spotValue = measurement.Tmax;
            var formattedSpotValue = spotValue.ToString("F1", culture) + " °C";

            if (!hasAdmissibleReference)
            {
                rows.Append($"<tr><td>{WebUtility.HtmlEncode(measurementName)}</td><td>{formattedSpotValue}</td><td>Defina Tmax admissível na análise</td></tr>");
                continue;
            }

            var formattedMargin = "-";
            var formattedUtilization = "-";
            var status = "Sem referência";

            if (maxAdmissibleC.HasValue && maxAdmissibleC.Value > 0)
            {
                var margin = maxAdmissibleC.Value - spotValue;
                var utilization = (spotValue / maxAdmissibleC.Value) * 100d;
                formattedMargin = margin.ToString("F1", culture) + " °C";
                formattedUtilization = utilization.ToString("F1", culture) + " %";

                status = utilization switch
                {
                    >= 100d => "Acima do limite",
                    >= 90d => "Atenção",
                    _ => "Normal"
                };
            }

            rows.Append($"<tr><td>{WebUtility.HtmlEncode(measurementName)}</td><td>{formattedSpotValue}</td><td>{formattedAdmissible}</td><td>{formattedMargin}</td><td>{formattedUtilization}</td><td>{status}</td></tr>");
        }

        return rows.ToString();
    }

    private static string NormalizeMeasurementNote(ThermalMeasurement measurement, string measurementName)
    {
        var rawNote = measurement.Notes?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawNote))
        {
            return measurement.Type switch
            {
                MeasurementType.Spot => $"Leitura pontual registrada em {measurementName}.",
                MeasurementType.Area => $"Estatística de área registrada em {measurementName}.",
                MeasurementType.Line => $"Perfil térmico de linha registrado em {measurementName}.",
                _ => $"Medição registrada em {measurementName}."
            };
        }

        if (rawNote.Contains("manual no canvas", StringComparison.OrdinalIgnoreCase))
        {
            return measurement.Type switch
            {
                MeasurementType.Spot => $"Leitura pontual manual ({measurementName}).",
                MeasurementType.Area => $"Região manual delimitada ({measurementName}).",
                MeasurementType.Line => $"Linha manual de perfil térmico ({measurementName}).",
                _ => $"Medição manual registrada ({measurementName})."
            };
        }

        return rawNote;
    }

    private static string FormatNullable(double? value, string unit, CultureInfo culture)
    {
        if (!value.HasValue)
        {
            return "-";
        }

        return $"{value.Value.ToString("F1", culture)} {unit}";
    }

    private static string FormatRange(double? min, double? max, CultureInfo culture)
    {
        if (!min.HasValue || !max.HasValue)
        {
            return "-";
        }

        return $"{min.Value.ToString("F1", culture)} a {max.Value.ToString("F1", culture)} °C";
    }

    private static void AddCell(TableDescriptor table, string text)
    {
        table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(text).FontSize(9);
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "SEM-INSTALACAO" : sanitized.Trim();
    }

    private static string LoadTemplate()
    {
        var candidatePaths = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "ThermixStudio.App", "templates", "relatorio termografico.html")),
            Path.Combine(AppContext.BaseDirectory, "templates", "relatorio termografico.html"),
            Path.Combine(AppContext.BaseDirectory, "relatorio termografico.html"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "relatorio termografico.html"))
        };

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }
        }

        const string embeddedTemplateName = "ThermixStudio.Reports.Embedded.relatorio_termografico.html";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(embeddedTemplateName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return "<html><body><h1>Template não encontrado</h1></body></html>";
    }

    private static ThermalProcessingState ExtractProcessingState(string processingJson)
    {
        if (string.IsNullOrWhiteSpace(processingJson))
        {
            return new ThermalProcessingState();
        }

        try
        {
            return JsonSerializer.Deserialize<ThermalProcessingState>(processingJson) ?? new ThermalProcessingState();
        }
        catch
        {
            return new ThermalProcessingState();
        }
    }

    private static string? ExtractVisiblePathFromMetadata(string metadataJson)
    {
        return ExtractMetadata(metadataJson)?.VisibleImagePath;
    }

    private static RadiometricMetadata ExtractMetadata(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new RadiometricMetadata();
        }

        try
        {
            return JsonSerializer.Deserialize<RadiometricMetadata>(metadataJson) ?? new RadiometricMetadata();
        }
        catch
        {
            return new RadiometricMetadata();
        }
    }
}
