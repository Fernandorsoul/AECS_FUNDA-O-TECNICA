using AECS.Domain.Models;

namespace AECS.Application.ContextCompiler;

public interface ICompiledContextGate
{
    void EnsureAccepted(TaskContract contract, CompiledRepositoryContext context);
}
