using AECS.Application.ContextCompiler;
using AECS.Application.AdaptiveController;
using AECS.Application.Promotion;
using AECS.Application.Staging;
using AECS.Domain.Interfaces;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using AECS.Infrastructure.Sandbox;

namespace AECS.Cli.Runtime;

public sealed class AecsExecutionRuntime : IDisposable
{
    private readonly List<HttpClient> _httpClients;
    private readonly string _cloudApiKey;

    private AecsExecutionRuntime(
        EffectiveAecsRuntimeConfiguration configuration,
        IAgentAdapter agentAdapter,
        IExecutionEvidenceStore evidenceStore,
        string cloudApiKey,
        List<HttpClient>? httpClients = null)
    {
        Configuration = configuration;
        AgentAdapter = agentAdapter;
        EvidenceStore = evidenceStore;
        _cloudApiKey = cloudApiKey;
        _httpClients = httpClients ?? [];
    }

    public EffectiveAecsRuntimeConfiguration Configuration { get; }
    public IAgentAdapter AgentAdapter { get; }
    public IExecutionEvidenceStore EvidenceStore { get; }
    internal string CloudApiKey => _cloudApiKey;

    public static AecsExecutionRuntime Create(ResolvedAecsRuntimeConfiguration resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var configuration = resolved.Effective;
        var clients = new List<HttpClient>();
        try
        {
            IAgentAdapter agent;
            if (configuration.AgentMode.Value == "mock")
            {
                agent = new MockAgentAdapter();
            }
            else
            {
                var localClient = new HttpClient();
                clients.Add(localClient);
                agent = new OllamaAdapter(
                    localClient,
                    configuration.OllamaBaseUrl.Value,
                    configuration.OllamaContextWindowTokens.Value);
            }

            if (configuration.CloudFallbackEnabled.Value)
            {
                var cloudClient = new HttpClient();
                clients.Add(cloudClient);
                var cloud = new CloudAdapter(cloudClient, new CloudAdapterOptions
                {
                    ApiKey = resolved.CloudApiKey,
                    BaseUrl = configuration.CloudBaseUrl.Value,
                    Model = configuration.CloudModel.Value,
                    ContextWindowTokens = configuration.CloudContextWindowTokens.Value,
                    MaxTokens = configuration.CloudMaxOutputTokens.Value
                });
                var allowedRisks = configuration.CloudAllowedRisks.Value.ToHashSet();
                agent = new FallbackAdapter(
                    agent,
                    cloud,
                    authorizeFallback: request => allowedRisks.Contains(request.Risk));
            }

            var store = CreateEvidenceStore(resolved);
            return new AecsExecutionRuntime(
                configuration,
                agent,
                store,
                resolved.CloudApiKey,
                clients);
        }
        catch
        {
            foreach (var client in clients)
                client.Dispose();
            throw;
        }
    }

    public static IExecutionEvidenceStore CreateEvidenceStore(
        ResolvedAecsRuntimeConfiguration resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var configuration = resolved.Effective;
        return configuration.EvidenceBackend.Value switch
        {
            "json" => (IExecutionEvidenceStore)new JsonExecutionEvidenceStore(
                configuration.EvidenceJsonRoot.Value,
                configuration.EvidenceKeyDirectory.Value),
            "postgres" => new PostgreSqlExecutionEvidenceStore(
                resolved.PostgreSqlConnectionString,
                configuration.EvidenceKeyDirectory.Value),
            _ => throw new InvalidOperationException(
                $"Unsupported evidence backend '{configuration.EvidenceBackend.Value}'.")
        };
    }

    public static AecsExecutionRuntime CreateForTesting(
        IAgentAdapter agentAdapter,
        IExecutionEvidenceStore evidenceStore,
        bool allowHostExecution = false)
    {
        ArgumentNullException.ThrowIfNull(agentAdapter);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        var resolved = AecsRuntimeConfigurationResolver.Resolve(
            new RuntimeCliOptions(),
            _ => null);
        var baseConfiguration = resolved.Effective;
        var configuration = new EffectiveAecsRuntimeConfiguration
        {
            ConfigurationHash = baseConfiguration.ConfigurationHash,
            AgentMode = new EffectiveRuntimeSetting<string>
            {
                Value = agentAdapter is MockAgentAdapter ? "mock" : "local",
                Source = "test-injection"
            },
            OllamaBaseUrl = baseConfiguration.OllamaBaseUrl,
            OllamaContextWindowTokens = baseConfiguration.OllamaContextWindowTokens,
            CloudFallbackEnabled = baseConfiguration.CloudFallbackEnabled,
            CloudRepositoryContextAllowed = baseConfiguration.CloudRepositoryContextAllowed,
            CloudBaseUrl = baseConfiguration.CloudBaseUrl,
            CloudModel = baseConfiguration.CloudModel,
            CloudContextWindowTokens = baseConfiguration.CloudContextWindowTokens,
            CloudMaxOutputTokens = baseConfiguration.CloudMaxOutputTokens,
            CloudAllowedRisks = baseConfiguration.CloudAllowedRisks,
            CloudCredential = baseConfiguration.CloudCredential,
            EvidenceBackend = baseConfiguration.EvidenceBackend,
            EvidenceJsonRoot = baseConfiguration.EvidenceJsonRoot,
            EvidenceKeyDirectory = baseConfiguration.EvidenceKeyDirectory,
            PostgreSqlConnection = baseConfiguration.PostgreSqlConnection,
            AdaptiveRoutingEnabled = baseConfiguration.AdaptiveRoutingEnabled,
            AdaptiveRoutingRollbackRequested = baseConfiguration.AdaptiveRoutingRollbackRequested,
            AdaptiveRoutingMinimumReadyRecords = baseConfiguration.AdaptiveRoutingMinimumReadyRecords,
            AdaptiveRoutingCanaryRepositoryPath = baseConfiguration.AdaptiveRoutingCanaryRepositoryPath,
            AdaptiveRoutingAllowedRisks = baseConfiguration.AdaptiveRoutingAllowedRisks,
            AllowHostExecution = new EffectiveRuntimeSetting<bool>
            {
                Value = allowHostExecution,
                Source = "test-injection"
            }
        };
        return new AecsExecutionRuntime(
            configuration,
            agentAdapter,
            evidenceStore,
            string.Empty);
    }

    public StagedExecutionPipeline CreatePipeline(
        RepositoryContextCompiler? contextCompiler = null,
        IExecutionController? executionController = null,
        IAgentAdapter? agentOverride = null,
        ICompiledContextGate? compiledContextGate = null)
    {
        var processRunner = new SystemProcessRunner();
        return new StagedExecutionPipeline(
            agentOverride ?? AgentAdapter,
            processRunner,
            EvidenceStore,
            contextCompiler,
            stagedProcessRunnerFactory: CreateStagedProcessRunnerFactory(processRunner),
            executionController: executionController,
            adaptiveRoutingPolicy: CreateAdaptiveRoutingPolicy(),
            compiledContextGate: compiledContextGate);
    }

    private AdaptiveRoutingPolicy CreateAdaptiveRoutingPolicy() => new()
    {
        Enabled = Configuration.AdaptiveRoutingEnabled.Value,
        RollbackRequested = Configuration.AdaptiveRoutingRollbackRequested.Value,
        MinimumReadyRecords = Configuration.AdaptiveRoutingMinimumReadyRecords.Value,
        AllowedRisks = Configuration.AdaptiveRoutingAllowedRisks.Value,
        CanaryRepositoryPath = string.IsNullOrWhiteSpace(
            Configuration.AdaptiveRoutingCanaryRepositoryPath.Value)
            ? null
            : Configuration.AdaptiveRoutingCanaryRepositoryPath.Value
    };

    public IStagedProcessRunnerFactory CreateStagedProcessRunnerFactory(
        IProcessRunner? processRunner = null) =>
        new DockerStagedProcessRunnerFactory(
            processRunner ?? new SystemProcessRunner(),
            Configuration.AllowHostExecution.Value);

    public CandidatePromotionService CreatePromotionService(
        IProcessRunner? processRunner = null) =>
        new(processRunner ?? new SystemProcessRunner(), EvidenceStore);

    public void Dispose()
    {
        foreach (var client in _httpClients)
            client.Dispose();
    }
}
