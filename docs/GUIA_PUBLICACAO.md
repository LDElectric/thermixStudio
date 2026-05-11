# Guia de Publicacao Sanitizada - Thermix Studio

## Objetivo
Gerar uma publicacao limpa contendo apenas os artefatos necessarios para execucao.

## Comando de publicacao recomendado
```powershell
dotnet publish .\src\ThermixStudio.App\ThermixStudio.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:GenerateDocumentationFile=false -p:CopyOutputSymbolsToPublishDirectory=false -o .\artifacts\publish-sanitized
```

## Sanitizacao recomendada
Remover itens nao essenciais do runtime final:
- *.pdb
- *.xml

Exemplo:
```powershell
Remove-Item .\artifacts\publish-sanitized\*.pdb,.\artifacts\publish-sanitized\*.xml -Force -ErrorAction SilentlyContinue
```

## Empacotamento
```powershell
Compress-Archive -Path .\artifacts\publish-sanitized\* -DestinationPath .\artifacts\ThermixStudio.App-win-x64-sanitized.zip -Force
```

## Regra de escopo
- Nao incluir a pasta publish/ no pacote final.
- Incluir apenas saida gerada em artifacts/publish-sanitized/ e documentacao de distribuicao.
