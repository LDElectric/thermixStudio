using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
                    txt.Span($"Gerado por {GetProductVersionLabel()} em ");
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
        var ptBr = new CultureInfo("pt-BR");

        // Substituir placeholders de cabeçalho
        var html = template
            .Replace("{{OS_NUMERO}}", SafeEncode(osNumber))
            .Replace("{{INSTALACAO_NOME}}", SafeEncode(installation))
            .Replace("{{EQUIPAMENTO}}", SafeEncode(equipment))
            .Replace("{{DATA_RELATORIO}}", SafeEncode(request.ReportDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)))
            .Replace("{{TERMOGRAFISTA}}", SafeEncode(request.TechnicianName))
            .Replace("{{CERTIFICACAO}}", SafeEncode(request.CertificationNumber));

        var sectionsBuilder = new StringBuilder();
        for (var i = 0; i < request.Sections.Count; i++)
        {
            var sectionHtml = BuildThermogramSectionHtml(request.Sections[i], i + 1, ptBr, isLast: i == request.Sections.Count - 1);
            sectionsBuilder.AppendLine(sectionHtml);
        }

        html = html.Replace("{{THERMOGRAM_SECTIONS}}", sectionsBuilder.ToString());

        // Substituir placeholders de rodapé
        html = html.Replace("{{RODAPE}}", SafeEncode($"Gerado por {GetProductVersionLabel()} - {DateTime.Now:dd/MM/yyyy HH:mm}"));

        return html;
    }

    private static string BuildThermogramSectionHtml(ReportSectionRequest section, int sectionIndex, CultureInfo culture, bool isLast)
    {
        var processing = ExtractProcessingState(section.Thermogram.ProcessingJson);
        var metadata = ExtractMetadata(section.Thermogram.MetadataJson);
        var visiblePath = ResolveVisiblePath(section.Thermogram);
        var thermalImagePath = string.IsNullOrWhiteSpace(section.AnnotatedThermalImagePath)
            ? section.Thermogram.FilePath
            : section.AnnotatedThermalImagePath;

        var thermalMarkup = BuildImageMarkup(thermalImagePath, "Termograma", "Imagem térmica não vinculada.");
        var visibleMarkup = BuildImageMarkup(visiblePath, "Foto Visual", "Imagem visual não vinculada.");

        var cameraModel = string.IsNullOrWhiteSpace(metadata.CameraModel) || metadata.CameraModel == "Unknown"
            ? section.Thermogram.CameraModel
            : metadata.CameraModel;

        var criticality = BuildCriticalityLabel(section.Thermogram.Criticality);
        var formattedEmissivity = processing.Emissivity.ToString("F2", culture);
        var formattedDistance = processing.TargetDistanceM.HasValue
            ? FormatNullable(processing.TargetDistanceM, "m", culture)
            : FormatNullable(metadata.ObjectDistanceM, "m", culture);
        var formattedAmbient = processing.AmbientTemperatureC.HasValue
            ? FormatNullable(processing.AmbientTemperatureC, "°C", culture)
            : FormatNullable(metadata.AmbientTemperatureC, "°C", culture);
        var formattedHumidity = processing.RelativeHumidityRh.HasValue
            ? FormatNullable(processing.RelativeHumidityRh, "%", culture)
            : FormatNullable(metadata.RelativeHumidity, "%", culture);

        var formattedLevelRange = FormatRange(
            metadata.PaletteScaleMinC ?? processing.LevelMinC,
            metadata.PaletteScaleMaxC ?? processing.LevelMaxC,
            culture);

        var sectionTitle = $"REGISTROS FOTOGRÁFICOS - TERMOGRAMA {sectionIndex}";

        var spotRowsBuilder = new StringBuilder();
        var spotMeasurements = section.Measurements.Where(m => m.Type == MeasurementType.Spot).ToList();
        if (spotMeasurements.Count > 0)
        {
            for (var i = 0; i < spotMeasurements.Count; i++)
            {
                var spot = spotMeasurements[i];
                var tspotValue = spot.Tmax.ToString("F1", culture);
                var tmaxAdCell = spot.MaxAdmissibleC.HasValue && spot.MaxAdmissibleC.Value > 0
                    ? spot.MaxAdmissibleC.Value.ToString("F1", culture)
                    : string.Empty;
                var margemCell = spot.MaxAdmissibleC.HasValue && spot.MaxAdmissibleC.Value > 0
                    ? (spot.MaxAdmissibleC.Value - spot.Tmax).ToString("F1", culture)
                    : string.Empty;

                spotRowsBuilder.AppendLine(
                    $"<tr><td class=\"spot-name\">Spot {i + 1}</td><td>{WebUtility.HtmlEncode(tspotValue)}</td><td>{WebUtility.HtmlEncode(tmaxAdCell)}</td><td>{WebUtility.HtmlEncode(margemCell)}</td></tr>");
            }
        }
        else
        {
            spotRowsBuilder.AppendLine("<tr><td colspan=\"4\" style=\"text-align:center;color:#888;padding:6px 4px;\">Sem medições de Spot</td></tr>");
        }

        var pageBreakClass = isLast ? string.Empty : " thermogram-page-break";

        return $@"
<div class=""thermogram-page{pageBreakClass}"">
    <div class=""section-header"">
        <div class=""red-bar""></div>
        <div class=""section-title"">{SafeEncode(sectionTitle)}</div>
    </div>

    <div class=""image-grid"">
        <div class=""img-box"">
            <div class=""img-frame"">{thermalMarkup}</div>
            <div class=""img-footer"">Imagem Térmica (IR)</div>
        </div>
        <div class=""img-box"">
            <div class=""img-frame"">{visibleMarkup}</div>
            <div class=""img-footer"">Imagem Visual (Luz Visível)</div>
        </div>
    </div>

    <div class=""section-header"">
        <div class=""red-bar""></div>
        <div class=""section-title"">PARÂMETROS TÉCNICOS E MEDIÇÕES</div>
    </div>

    <div class=""metrics-grid"">
        <div class=""info-card"">
            <div class=""card-title"">PARÂMETROS DE INSPEÇÃO</div>
            <div class=""params-list"">
                <div class=""param-row""><span class=""param-label"">Criticidade</span><span class=""param-value"">{SafeEncode(criticality)}</span></div>
                <div class=""param-row""><span class=""param-label"">Câmera</span><span class=""param-value"">{SafeEncode(cameraModel)}</span></div>
                <div class=""param-row""><span class=""param-label"">Emissividade</span><span class=""param-value"">{SafeEncode(formattedEmissivity)}</span></div>
                <div class=""param-row""><span class=""param-label"">Localização</span><span class=""param-value"">{SafeEncode(section.Thermogram.EquipmentLocation)}</span></div>
                <div class=""param-row""><span class=""param-label"">Escala Visual</span><span class=""param-value"">{SafeEncode(formattedLevelRange)}</span></div>
                <div class=""param-row""><span class=""param-label"">Distância (m)</span><span class=""param-value"">{SafeEncode(formattedDistance)}</span></div>
                <div class=""param-row""><span class=""param-label"">T. Ambiente (°C)</span><span class=""param-value"">{SafeEncode(formattedAmbient)}</span></div>
                <div class=""param-row""><span class=""param-label"">Umidade RH (%)</span><span class=""param-value"">{SafeEncode(formattedHumidity)}</span></div>
            </div>
        </div>

        <div class=""info-card"">
            <div class=""card-title"">MEDIÇÕES PONTUAIS (SPOTS)</div>
            <table class=""measurements-table"">
                <colgroup>
                    <col style=""width: 20%"">
                    <col style=""width: 26%"">
                    <col style=""width: 27%"">
                    <col style=""width: 27%"">
                </colgroup>
                <thead>
                    <tr>
                        <th>Ident.</th>
                        <th>Tspot (°C)</th>
                        <th>Tmáx Adm (°C)</th>
                        <th>Margem (°C)</th>
                    </tr>
                </thead>
                <tbody>
                    {spotRowsBuilder}
                </tbody>
            </table>
            <div class=""measurement-note"">📐 Margem = Tmax Admissível - Tspot. Baseado na emissividade.</div>
        </div>
    </div>

    <div class=""section-header"">
        <div class=""red-bar""></div>
        <div class=""section-title"">OBSERVAÇÕES E RECOMENDAÇÕES</div>
    </div>

    <table class=""report-meta-table"">
        <colgroup>
            <col style=""width: 22%"">
            <col style=""width: 78%"">
        </colgroup>
        <tr>
            <td class=""label"">Observações:</td>
            <td class=""text-area"">{SafeEncode(section.Observations)}</td>
        </tr>
        <tr>
            <td class=""label"">Ação Recomendada:</td>
            <td class=""text-area"">{SafeEncode(section.Recommendation)}</td>
        </tr>
    </table>
</div>";
    }

    private static string GetProductVersionLabel()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version;

        if (version is null)
        {
            return "Thermix Studio v 2.0";
        }

        return $"Thermix Studio v {version.Major}.{Math.Max(0, version.Minor)}";
    }

    private static string ReplaceThermogramPlaceholders(string html, ReportSectionRequest section, int sectionIndex, CultureInfo culture)
    {
        var processing = ExtractProcessingState(section.Thermogram.ProcessingJson);
        var metadata = ExtractMetadata(section.Thermogram.MetadataJson);
        var visiblePath = ResolveVisiblePath(section.Thermogram);
        var thermalImagePath = string.IsNullOrWhiteSpace(section.AnnotatedThermalImagePath)
            ? section.Thermogram.FilePath
            : section.AnnotatedThermalImagePath;

        var thermalMarkup = BuildImageMarkup(thermalImagePath, "Termograma", "Imagem térmica não vinculada.");
        var visibleMarkup = BuildImageMarkup(visiblePath, "Foto Visual", "Imagem visual não vinculada.");

        var cameraModel = string.IsNullOrWhiteSpace(metadata.CameraModel) || metadata.CameraModel == "Unknown"
            ? section.Thermogram.CameraModel
            : metadata.CameraModel;

        var criticality = BuildCriticalityLabel(section.Thermogram.Criticality);
        var formattedEmissivity = processing.Emissivity.ToString("F2", culture);

        // Distance: prefer user-entered value, then EXIF metadata
        var formattedDistance = processing.TargetDistanceM.HasValue
            ? FormatNullable(processing.TargetDistanceM, "m", culture)
            : FormatNullable(metadata.ObjectDistanceM, "m", culture);

        // Ambient temperature and humidity: prefer user-entered values, then EXIF metadata
        var formattedAmbient = processing.AmbientTemperatureC.HasValue
            ? FormatNullable(processing.AmbientTemperatureC, "°C", culture)
            : FormatNullable(metadata.AmbientTemperatureC, "°C", culture);
        var formattedHumidity = processing.RelativeHumidityRh.HasValue
            ? FormatNullable(processing.RelativeHumidityRh, "%", culture)
            : FormatNullable(metadata.RelativeHumidity, "%", culture);

        var formattedLevelRange = FormatRange(
            metadata.PaletteScaleMinC ?? processing.LevelMinC,
            metadata.PaletteScaleMaxC ?? processing.LevelMaxC,
            culture);

        // Section title: base title + optional description suffix
        var baseTitle = $"REGISTROS FOTOGRÁFICOS - TERMOGRAMA {sectionIndex}";
        var sectionTitle = string.IsNullOrWhiteSpace(section.Thermogram.EquipmentDescription)
            ? baseTitle
            : $"{baseTitle} | {section.Thermogram.EquipmentDescription}";

        html = html
            .Replace($"{{{{SECTION_TITLE_{sectionIndex}}}}}", SafeEncode(sectionTitle))
            .Replace($"{{{{IMAGEM_TERMICA_{sectionIndex}}}}}", thermalMarkup)
            .Replace($"{{{{IMAGEM_VISUAL_{sectionIndex}}}}}", visibleMarkup)
            .Replace($"{{{{CRITICIDADE_{sectionIndex}}}}}", criticality)
            .Replace($"{{{{CAMERA_{sectionIndex}}}}}", SafeEncode(cameraModel))
            .Replace($"{{{{EMISSIVIDADE_{sectionIndex}}}}}", SafeEncode(formattedEmissivity))
            .Replace($"{{{{LOCALIZACAO_{sectionIndex}}}}}", SafeEncode(section.Thermogram.EquipmentLocation))
            .Replace($"{{{{ESCALA_VISUAL_{sectionIndex}}}}}", SafeEncode(formattedLevelRange))
            .Replace($"{{{{DISTANCIA_{sectionIndex}}}}}", SafeEncode(formattedDistance))
            .Replace($"{{{{TEMP_AMBIENTE_{sectionIndex}}}}}", SafeEncode(formattedAmbient))
            .Replace($"{{{{UMIDADE_{sectionIndex}}}}}", SafeEncode(formattedHumidity))
            .Replace($"{{{{OBSERVACOES_{sectionIndex}}}}}", SafeEncode(section.Observations))
            .Replace($"{{{{ACAO_RECOMENDADA_{sectionIndex}}}}}", SafeEncode(section.Recommendation));

        // Build dynamic spot rows — only existing spots, following rule:
        // always show Spot + Tspot; show Tmax Adm. and Margem only when that spot has an admissible Tmax.
        var spotMeasurements = section.Measurements.Where(m => m.Type == MeasurementType.Spot).ToList();
        var spotRowsBuilder = new System.Text.StringBuilder();
        if (spotMeasurements.Count > 0)
        {
            for (var i = 0; i < spotMeasurements.Count; i++)
            {
                var spot = spotMeasurements[i];
                var tspotValue = spot.Tmax.ToString("F1", culture);
                var tmaxAdCell = spot.MaxAdmissibleC.HasValue && spot.MaxAdmissibleC.Value > 0
                    ? spot.MaxAdmissibleC.Value.ToString("F1", culture)
                    : string.Empty;
                var margemCell = spot.MaxAdmissibleC.HasValue && spot.MaxAdmissibleC.Value > 0
                    ? (spot.MaxAdmissibleC.Value - spot.Tmax).ToString("F1", culture)
                    : string.Empty;
                spotRowsBuilder.AppendLine(
                    $"<tr><td class=\"spot-name\">Spot {i + 1}</td>" +
                    $"<td>{WebUtility.HtmlEncode(tspotValue)}</td>" +
                    $"<td>{WebUtility.HtmlEncode(tmaxAdCell)}</td>" +
                    $"<td>{WebUtility.HtmlEncode(margemCell)}</td></tr>");
            }
        }
        else
        {
            spotRowsBuilder.AppendLine(
                "<tr><td colspan=\"4\" style=\"text-align:center;color:#888;padding:6px 4px;\">Sem medições de Spot</td></tr>");
        }

        html = html.Replace($"{{{{SPOTS_{sectionIndex}_ROWS}}}}", spotRowsBuilder.ToString());

        return html;
    }

    private static string RemoveUnusedThermogramSections(string html, int usedSectionCount)
    {
        // Remover seções não utilizadas (mantém apenas as necessárias)
        var regex = new System.Text.RegularExpressions.Regex($@"<!-- Repita a seção.*?(?=<div class=""footer"">|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
        html = regex.Replace(html, string.Empty);
        return html;
    }

    private static string BuildCriticalityLabel(EquipmentCriticality criticality)
    {
        return criticality switch
        {
            EquipmentCriticality.Low => "Baixa",
            EquipmentCriticality.Medium => "Média",
            EquipmentCriticality.High => "Alta",
            EquipmentCriticality.Critical => "Crítica",
            _ => "-"
        };
    }

    private static string SafeEncode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return WebUtility.HtmlEncode(value.Trim());
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
                return $"<img src=\"{imageDataUri}\" style=\"display:block; width:100%; height:100%; object-fit:contain; object-position:center;\" alt=\"{WebUtility.HtmlEncode(alt)}\">";
            }
        }

        return $"<div class=\"img-placeholder\" style=\"height: 260px; line-height: 260px; color: #666; font-size: 10pt; background: #1a1a1a;\">{WebUtility.HtmlEncode(placeholderText)}</div>";
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
