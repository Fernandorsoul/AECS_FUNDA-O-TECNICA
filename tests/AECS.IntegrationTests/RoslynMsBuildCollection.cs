namespace AECS.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoslynMsBuildCollection
{
    public const string Name = "Roslyn MSBuild integration";
}
