using System.Text;

namespace AECS.Application.ContextCompiler;

/// <summary>
/// Compila o contexto selecionado em um prompt estruturado para o agente.
/// Inclui o código-fonte real dos arquivos selecionados formatados para consumo pelo LLM.
/// </summary>
public class ContextCompiler
{
    /// <summary>
    /// Compila um ContextPackage em um prompt estruturado com código-fonte.
    /// </summary>
    /// <param name="package">Pacote de contexto selecionado</param>
    /// <param name="rootPath">Caminho raiz do repositório</param>
    /// <param name="taskObjective">Objetivo da task para contextualização</param>
    /// <returns>Prompt formatado com código-fonte dos arquivos relevantes</returns>
    public string Compile(ContextPackage package, string rootPath, string taskObjective)
    {
        var sb = new StringBuilder();
        
        // Cabeçalho do contexto
        sb.AppendLine("## CONTEXT");
        sb.AppendLine();
        sb.AppendLine($"Task: {taskObjective}");
        sb.AppendLine($"Context ID: {package.Id}");
        sb.AppendLine($"Strategy: {package.Strategy}");
        sb.AppendLine($"Estimated Tokens: {package.EstimatedTokens}");
        sb.AppendLine();
        
        // Seção de símbolos relevantes
        if (package.SelectedSymbols.Count > 0)
        {
            sb.AppendLine("### Relevant Symbols");
            foreach (var symbol in package.SelectedSymbols)
            {
                sb.AppendLine($"  - {symbol}");
            }
            sb.AppendLine();
        }
        
        // Seção de testes relevantes
        if (package.RelevantTests.Count > 0)
        {
            sb.AppendLine("### Relevant Tests");
            foreach (var test in package.RelevantTests)
            {
                sb.AppendLine($"  - {test}");
            }
            sb.AppendLine();
        }
        
        // Seção principal: Código-fonte dos arquivos selecionados
        sb.AppendLine("### Source Code");
        sb.AppendLine();
        
        foreach (var filePath in package.SelectedFiles)
        {
            var fullPath = Path.Combine(rootPath, filePath);
            
            if (!File.Exists(fullPath))
            {
                sb.AppendLine($"// File not found: {filePath}");
                sb.AppendLine();
                continue;
            }
            
            try
            {
                var content = File.ReadAllText(fullPath);
                
                sb.AppendLine($"```csharp");
                sb.AppendLine($"// File: {filePath}");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"// Error reading file {filePath}: {ex.Message}");
                sb.AppendLine();
            }
        }
        
        sb.AppendLine("## END CONTEXT");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Compila o contexto em um formato JSON para armazenamento ou transmissão.
    /// </summary>
    public CompiledContext CompileToJson(ContextPackage package, string rootPath)
    {
        var files = new Dictionary<string, string>();
        
        foreach (var filePath in package.SelectedFiles)
        {
            var fullPath = Path.Combine(rootPath, filePath);
            
            if (File.Exists(fullPath))
            {
                files[filePath] = File.ReadAllText(fullPath);
            }
        }
        
        return new CompiledContext
        {
            Id = package.Id,
            TaskId = package.TaskId,
            Strategy = package.Strategy,
            EstimatedTokens = package.EstimatedTokens,
            SelectedFiles = package.SelectedFiles,
            SelectedSymbols = package.SelectedSymbols,
            RelevantTests = package.RelevantTests,
            FileContents = files
        };
    }
}

/// <summary>
/// Representa o contexto compilado com conteúdo dos arquivos.
/// </summary>
public class CompiledContext
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Strategy { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public List<string> SelectedFiles { get; init; } = [];
    public List<string> SelectedSymbols { get; init; } = [];
    public List<string> RelevantTests { get; init; } = [];
    public Dictionary<string, string> FileContents { get; init; } = new();
}
