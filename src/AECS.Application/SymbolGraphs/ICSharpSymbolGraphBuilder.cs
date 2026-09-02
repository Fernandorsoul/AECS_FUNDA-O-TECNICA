using AECS.Domain.Models;

namespace AECS.Application.SymbolGraphs;

public interface ICSharpSymbolGraphBuilder
{
    Task<CSharpSymbolGraph> BuildAsync(
        string workspacePath,
        RepositorySnapshot repositorySnapshot,
        CSharpSymbolGraphLimits? limits = null,
        CancellationToken cancellationToken = default);
}
