using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Implementação centralizada de ExifTool para extração de dados FLIR.
/// Consolida os métodos duplicados de FindExifTool/EncontrarExifTool dos legados.
/// </summary>
public sealed class ExifToolService : IExifToolService
{
    private string? _cachedExifToolPath;
    private bool _searchedForExifTool;

    public string? FindExifTool()
    {
        if (_searchedForExifTool)
            return _cachedExifToolPath;

        _searchedForExifTool = true;
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";

        // 1. Buscar em PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var p in pathEnv.Split(Path.PathSeparator))
            {
                string fullPath = Path.Combine(p, exeName);
                if (File.Exists(fullPath))
                {
                    _cachedExifToolPath = fullPath;
                    LogInfo($"ExifTool encontrado em PATH: {fullPath}");
                    return fullPath;
                }
            }
        }

        // 2. Pasta do executável
        string appDirPath = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(appDirPath))
        {
            _cachedExifToolPath = appDirPath;
            LogInfo($"ExifTool encontrado no AppContext: {appDirPath}");
            return appDirPath;
        }

        // 3. Pasta tools embutida
        string toolsDirPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (File.Exists(toolsDirPath))
        {
            _cachedExifToolPath = toolsDirPath;
            LogInfo($"ExifTool encontrado em tools/: {toolsDirPath}");
            return toolsDirPath;
        }

        // 4. Recurso embutido extraído para temp (consolidado de ThermalAnalysisService)
        var tempEmbeddedExifTool = EnsureEmbeddedToolInTemp(
            "ThermixStudio.App.Embedded.exiftool.exe",
            "thermix_exiftool.exe");
        if (!string.IsNullOrWhiteSpace(tempEmbeddedExifTool) && File.Exists(tempEmbeddedExifTool))
        {
            _cachedExifToolPath = tempEmbeddedExifTool;
            LogInfo($"ExifTool encontrado em temp embutido: {tempEmbeddedExifTool}");
            return tempEmbeddedExifTool;
        }

        // 5. Python DJI SDK (.venv do workspace)
        var workspaceTool = Path.Combine(AppContext.BaseDirectory, ".venv", "Lib", "site-packages", "dji_executables", "dji_thermal_sdk_v1.7", "exiftool-12.35.exe");
        if (File.Exists(workspaceTool))
        {
            _cachedExifToolPath = workspaceTool;
            LogInfo($"ExifTool encontrado em .venv: {workspaceTool}");
            return workspaceTool;
        }

        // 6. PATH genérico (exiftool sem extensão explícita)
        if (IsExecutableAvailable("exiftool"))
        {
            _cachedExifToolPath = "exiftool";
            LogInfo("ExifTool encontrado via PATH (nome genérico)");
            return _cachedExifToolPath;
        }

        // 7. Caminhos legados fixos
        string[] legacyPaths =
        [
            @"C:\Program Files\exiftool\exiftool.exe",
            @"C:\Windows\exiftool.exe"
        ];

        foreach (var caminho in legacyPaths)
        {
            if (File.Exists(caminho))
            {
                _cachedExifToolPath = caminho;
                LogInfo($"ExifTool encontrado em caminho legado: {caminho}");
                return caminho;
            }
        }

        LogWarn("ExifTool não encontrado no sistema");
        return null;
    }

    public bool IsExifToolAvailable() => FindExifTool() != null;

    public async Task<ImageViewMode?> TryDetectModeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            string output = await RunExifToolAsync(exifTool, $"-s3 -FLIR:ThermalImageType \"{imagePath}\"", cancellationToken);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            return ExifModeMapper.MapThermalImageType(output.Trim());
        }
        catch (Exception ex)
        {
            LogWarn($"Falha ao detectar modo EXIF: {ex.Message}");
            return null;
        }
    }

    public async Task<byte[]?> TryExtractVisibleImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        string[] tagsToTry = { "FLIR:EmbeddedImage", "EmbeddedImage", "PreviewImage" };

        foreach (var tag in tagsToTry)
        {
            try
            {
                byte[] buffer = await RunExifToolBinaryAsync(exifTool, $"-b -{tag} \"{imagePath}\"", cancellationToken);
                if (buffer.Length > 5000)
                {
                    LogInfo($"Imagem visível extraída via tag '{tag}': {buffer.Length} bytes");
                    return buffer;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Tag '{tag}' falhou: {ex.Message}");
                continue;
            }
        }

        LogDebug("Nenhuma imagem visível encontrada");
        return null;
    }

    public async Task<byte[]?> TryExtractRawThermalAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            byte[] buffer = await RunExifToolBinaryAsync(exifTool, $"-b -RawThermalImage \"{imagePath}\"", cancellationToken);
            if (buffer.Length > 1000)
            {
                LogInfo($"Matriz térmica bruta extraída: {buffer.Length} bytes");
                return buffer;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Extração de térmico bruto falhou: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> TryGetPaletteNameAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            string output = await RunExifToolAsync(exifTool, $"-s3 -FLIR:PaletteName \"{imagePath}\"", cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                return output.Trim();
        }
        catch (Exception ex)
        {
            LogDebug($"Falha ao ler PaletteName: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> TryGetAllMetadataJsonAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            string output = await RunExifToolAsync(exifTool, $"-json \"{imagePath}\"", cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                return output;
        }
        catch (Exception ex)
        {
            LogDebug($"Falha ao ler metadados JSON: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> TryGetMetadataJsonNumericAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            string output = await RunExifToolAsync(exifTool, $"-j -n \"{imagePath}\"", cancellationToken);
            if (!string.IsNullOrWhiteSpace(output))
                return output;
        }
        catch (Exception ex)
        {
            LogDebug($"Falha ao ler metadados JSON numérico: {ex.Message}");
        }

        return null;
    }

    public async Task<byte[]?> TryExtractPaletteBytesAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return null;

        string? exifTool = FindExifTool();
        if (string.IsNullOrEmpty(exifTool))
            return null;

        try
        {
            byte[] buffer = await RunExifToolBinaryAsync(exifTool, $"-b -Palette \"{imagePath}\"", cancellationToken);
            if (buffer.Length > 0)
                return buffer;
        }
        catch (Exception ex)
        {
            LogDebug($"Falha ao extrair Palette: {ex.Message}");
        }

        return null;
    }

    private static string? EnsureEmbeddedToolInTemp(string resourceName, string outputFileName)
    {
        try
        {
            var outputPath = Path.Combine(Path.GetTempPath(), outputFileName);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            using var stream = typeof(ExifToolService).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.CopyTo(fs);
            return outputPath;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable);
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var hasExtension = Path.HasExtension(executable);

            foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var basePath = Path.Combine(folder.Trim(), executable);
                if (hasExtension)
                {
                    if (File.Exists(basePath))
                    {
                        return true;
                    }

                    continue;
                }

                foreach (var ext in extensions)
                {
                    var fullPath = basePath + ext;
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private async Task<string> RunExifToolAsync(string exifToolPath, string arguments, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exifToolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.Run(() => process.WaitForExit(5000), cancellationToken);

        if (!string.IsNullOrWhiteSpace(error))
            LogDebug($"ExifTool stderr: {error}");

        return output;
    }

    private async Task<byte[]> RunExifToolBinaryAsync(string exifToolPath, string arguments, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exifToolPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);

        await Task.Run(() => process.WaitForExit(5000), cancellationToken);

        return ms.ToArray();
    }

    private static void LogInfo(string message) =>
        Debug.WriteLine($"[EXIF_SERVICE] {message}");

    private static void LogWarn(string message) =>
        Debug.WriteLine($"[EXIF_SERVICE] [WARN] {message}");

    private static void LogDebug(string message) =>
        Debug.WriteLine($"[EXIF_SERVICE] [DEBUG] {message}");
}
