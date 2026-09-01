import * as vscode from 'vscode';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import * as path from 'node:path';
import {
  BackendProtocolError,
  ProcessLineTransport,
  ProtocolClient
} from './protocolClient';

const tokenEnvironmentVariable = 'AECS_VSCODE_SESSION_TOKEN';
const lastOperationKey = 'aecs.lastOperationId';
const pendingStartKey = 'aecs.pendingStart';

interface ExecutionOperation {
  schemaVersion: string;
  operationId: string;
  clientRequestId: string;
  repositoryPath: string;
  taskFile: string;
  taskId: string;
  objective: string;
  status: string;
  evidenceId?: string;
  evidenceLocation: string;
  decision: string;
  finalState: string;
  message: string;
  createdAt: string;
  updatedAt: string;
}

interface CandidateSnapshot {
  available: boolean;
  evidenceId: string;
  candidateId: string;
  baselineCommit: string;
  baselineBranch: string;
  diff: string;
  diffHash: string;
  changedFiles: string[];
  risk: string;
  decision: string;
  state: string;
  eligibility: string;
  reviewable: boolean;
  repositoryReady: boolean;
  repositoryState: string;
}

interface GateFact {
  phase: string;
  verifier: string;
  status: string;
  message: string;
}

interface AcceptanceFact {
  criterionId: string;
  description: string;
  status: string;
  evidenceType: string;
  evidenceReference: string;
}

interface BudgetFact {
  attemptsUsed: number;
  maximumAttempts: number;
  inputTokens: number;
  outputTokens: number;
  estimatedCostUsd: number;
  wallClockSeconds: number;
  wallClockLimitSeconds: number;
  exhausted: boolean;
  exhaustionReason: string;
}

interface EvidenceLink {
  relation: string;
  uri: string;
}

interface ExecutionInspection {
  schemaVersion: string;
  operation: ExecutionOperation;
  candidate: CandidateSnapshot;
  gates: GateFact[];
  acceptanceCriteria: AcceptanceFact[];
  budget: BudgetFact;
  evidenceLinks: EvidenceLink[];
  diagnostics: string[];
}

interface ReviewResult {
  status: string;
  message: string;
  persisted: boolean;
  evidence: { id: string };
}

interface PendingStart {
  clientRequestId: string;
  taskFile: string;
}

class AecsNode extends vscode.TreeItem {
  constructor(
    label: string,
    description?: string,
    children: AecsNode[] = [],
    contextValue?: string,
    command?: vscode.Command,
    icon?: vscode.ThemeIcon
  ) {
    super(label, children.length > 0
      ? vscode.TreeItemCollapsibleState.Expanded
      : vscode.TreeItemCollapsibleState.None);
    this.description = description;
    this.children = children;
    this.contextValue = contextValue;
    this.command = command;
    this.iconPath = icon;
  }

  readonly children: AecsNode[];
}

class ExecutionTreeProvider implements vscode.TreeDataProvider<AecsNode> {
  private readonly changed = new vscode.EventEmitter<AecsNode | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  operation: ExecutionOperation | undefined;
  inspection: ExecutionInspection | undefined;
  startupMessage = 'Selecione um TaskContract para iniciar.';

  refresh(): void {
    this.changed.fire(undefined);
  }

  getTreeItem(element: AecsNode): vscode.TreeItem {
    return element;
  }

  getChildren(element?: AecsNode): AecsNode[] {
    if (element) {
      return element.children;
    }
    if (!this.operation) {
      return [new AecsNode(
        'Nenhuma execução vinculada',
        this.startupMessage,
        [],
        undefined,
        undefined,
        new vscode.ThemeIcon('circle-outline'))];
    }

    const operation = this.operation;
    const running = !isTerminal(operation.status);
    const roots: AecsNode[] = [new AecsNode(
      operation.taskId || 'Execução AECS',
      operation.status,
      [
        leaf('Objetivo', operation.objective, 'target'),
        leaf('Estado', operation.status, statusIcon(operation.status)),
        leaf('Decisão', operation.decision || 'ainda indisponível', 'law'),
        leaf('Mensagem', operation.message, 'info'),
        leaf('Operação', operation.operationId, 'link'),
        leaf('Evidência', operation.evidenceId ?? 'ainda indisponível', 'verified')
      ],
      running ? 'aecs.running' : 'aecs.execution',
      undefined,
      new vscode.ThemeIcon(statusIcon(operation.status)))];

    const inspection = this.inspection;
    if (!inspection) {
      return roots;
    }
    const candidate = inspection.candidate;
    roots.push(new AecsNode(
      'Candidato',
      `${candidate.decision}/${candidate.state}`,
      [
        leaf('Risco', candidate.risk, 'shield'),
        leaf('Elegibilidade', candidate.eligibility, 'pass'),
        leaf('Baseline', `${candidate.baselineCommit} (${candidate.baselineBranch})`, 'git-commit'),
        leaf('Checkout', candidate.repositoryState, candidate.repositoryReady ? 'pass' : 'warning'),
        new AecsNode(
          'Diff autenticado',
          candidate.diffHash,
          candidate.changedFiles.map(file => leaf(file, undefined, 'file-code')),
          'aecs.diff',
          { command: 'aecs.openDiff', title: 'Abrir diff' },
          new vscode.ThemeIcon('diff'))
      ],
      candidate.reviewable ? 'aecs.reviewable' : 'aecs.candidate',
      undefined,
      new vscode.ThemeIcon('git-pull-request')));

    roots.push(new AecsNode(
      'Gates',
      `${inspection.gates.length}`,
      inspection.gates.map(gate => new AecsNode(
        `[${gate.phase}] ${gate.verifier}`,
        gate.status,
        gate.message ? [leaf(gate.message, undefined, 'comment')] : [],
        undefined,
        undefined,
        new vscode.ThemeIcon(gate.status.toLowerCase() === 'pass' ? 'pass' : 'error'))),
      undefined,
      undefined,
      new vscode.ThemeIcon('checklist')));

    roots.push(new AecsNode(
      'Critérios de aceite',
      `${inspection.acceptanceCriteria.length}`,
      inspection.acceptanceCriteria.map(criterion => new AecsNode(
        criterion.criterionId || criterion.description,
        criterion.status,
        [
          leaf(criterion.description, undefined, 'comment'),
          leaf('Evidência', `${criterion.evidenceType}: ${criterion.evidenceReference}`, 'references')
        ],
        undefined,
        undefined,
        new vscode.ThemeIcon(criterion.status.toLowerCase() === 'pass' ? 'pass' : 'error'))),
      undefined,
      undefined,
      new vscode.ThemeIcon('tasklist')));

    const budget = inspection.budget;
    roots.push(new AecsNode(
      'Budget',
      budget.exhausted ? 'esgotado' : 'dentro do limite',
      [
        leaf('Tentativas', `${budget.attemptsUsed}/${budget.maximumAttempts}`, 'debug-restart'),
        leaf('Tokens', `${budget.inputTokens + budget.outputTokens}`, 'symbol-number'),
        leaf('Custo estimado', formatCost(budget.estimatedCostUsd), 'credit-card'),
        leaf('Tempo', `${budget.wallClockSeconds.toFixed(1)}s/${budget.wallClockLimitSeconds}s`, 'watch'),
        ...(budget.exhaustionReason ? [leaf('Esgotamento', budget.exhaustionReason, 'warning')] : [])
      ],
      undefined,
      undefined,
      new vscode.ThemeIcon('dashboard')));

    roots.push(new AecsNode(
      'Links de evidência',
      `${inspection.evidenceLinks.length}`,
      inspection.evidenceLinks.map(link => new AecsNode(
        link.relation,
        link.uri,
        [],
        'aecs.evidenceLink',
        { command: 'aecs.copyEvidenceLink', title: 'Copiar link', arguments: [link.uri] },
        new vscode.ThemeIcon('link'))),
      undefined,
      undefined,
      new vscode.ThemeIcon('verified-filled')));
    return roots;
  }
}

class DiffContentProvider implements vscode.TextDocumentContentProvider {
  private diff = '';

  setDiff(diff: string): void {
    this.diff = diff;
  }

  provideTextDocumentContent(): string {
    return this.diff || 'Nenhum diff autenticado está disponível.';
  }
}

class AecsController implements vscode.Disposable {
  private client: ProtocolClient | undefined;
  private pollTimer: NodeJS.Timeout | undefined;
  private readonly output = vscode.window.createOutputChannel('AECS');
  private readonly provider = new ExecutionTreeProvider();
  private readonly diffProvider = new DiffContentProvider();
  private readonly repository: vscode.WorkspaceFolder | undefined;
  private disposed = false;

  constructor(private readonly context: vscode.ExtensionContext) {
    this.repository = vscode.workspace.workspaceFolders?.[0];
    context.subscriptions.push(
      this.output,
      vscode.window.registerTreeDataProvider('aecs.executions', this.provider),
      vscode.workspace.registerTextDocumentContentProvider('aecs-diff', this.diffProvider),
      vscode.commands.registerCommand('aecs.startExecution', () => this.start()),
      vscode.commands.registerCommand('aecs.refreshExecution', () => this.refresh(true)),
      vscode.commands.registerCommand('aecs.cancelExecution', () => this.cancel()),
      vscode.commands.registerCommand('aecs.openDiff', () => this.openDiff()),
      vscode.commands.registerCommand('aecs.approveCandidate', () => this.review('Approve')),
      vscode.commands.registerCommand('aecs.rejectCandidate', () => this.review('Reject')),
      vscode.commands.registerCommand('aecs.copyEvidenceLink', (uri: string) =>
        vscode.env.clipboard.writeText(uri)),
      vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('aecs')) {
          this.restartClient();
        }
      })
    );
  }

  async restore(): Promise<void> {
    if (!this.repository) {
      this.provider.startupMessage = 'Abra uma pasta de repositório no VS Code.';
      this.provider.refresh();
      return;
    }
    const operationId = this.context.workspaceState.get<string>(lastOperationKey);
    const pending = this.context.workspaceState.get<PendingStart>(pendingStartKey);
    if (!operationId && !pending) {
      return;
    }
    try {
      if (operationId) {
        await this.loadOperation(operationId);
      } else if (pending) {
        const operation = await (await this.ensureClient()).request<ExecutionOperation>(
          'execution.start', pending);
        await this.rememberOperation(operation);
      }
    } catch (error) {
      if (error instanceof BackendProtocolError) {
        await this.context.workspaceState.update(pendingStartKey, undefined);
      }
      this.report(error, false);
    }
  }

  dispose(): void {
    this.disposed = true;
    if (this.pollTimer) {
      clearTimeout(this.pollTimer);
    }
    this.client?.dispose();
  }

  private async start(): Promise<void> {
    if (!this.repository) {
      void vscode.window.showErrorMessage('Abra uma pasta de repositório antes de iniciar o AECS.');
      return;
    }
    const selected = await vscode.window.showOpenDialog({
      canSelectFiles: true,
      canSelectFolders: false,
      canSelectMany: false,
      defaultUri: this.repository.uri,
      filters: { 'TaskContract YAML': ['yaml', 'yml'] },
      title: 'Selecione o TaskContract'
    });
    if (!selected?.[0]) {
      return;
    }
    const pending: PendingStart = {
      clientRequestId: randomUUID(),
      taskFile: selected[0].fsPath
    };
    await this.context.workspaceState.update(pendingStartKey, pending);
    try {
      const operation = await (await this.ensureClient()).request<ExecutionOperation>(
        'execution.start', pending);
      await this.rememberOperation(operation);
      void vscode.window.showInformationMessage(`AECS iniciou ${operation.taskId}.`);
    } catch (error) {
      if (error instanceof BackendProtocolError) {
        await this.context.workspaceState.update(pendingStartKey, undefined);
      }
      this.report(error, true);
    }
  }

  private async refresh(showErrors: boolean): Promise<void> {
    const operationId = this.provider.operation?.operationId ??
      this.context.workspaceState.get<string>(lastOperationKey);
    if (!operationId) {
      this.provider.refresh();
      return;
    }
    try {
      await this.loadOperation(operationId);
    } catch (error) {
      this.report(error, showErrors);
    }
  }

  private async loadOperation(operationId: string): Promise<void> {
    const client = await this.ensureClient();
    const operation = await client.request<ExecutionOperation>(
      'execution.get', { operationId });
    this.provider.operation = operation;
    this.provider.inspection = undefined;
    if (operation.evidenceId) {
      try {
        const inspection = await client.request<ExecutionInspection>(
          'execution.inspect', { operationId });
        this.provider.inspection = inspection;
        this.diffProvider.setDiff(inspection.candidate.diff);
      } catch (error) {
        if (!(error instanceof BackendProtocolError) || error.code !== 'evidence_unavailable') {
          throw error;
        }
      }
    }
    this.provider.refresh();
    if (!isTerminal(operation.status)) {
      this.schedulePoll();
    }
  }

  private async rememberOperation(operation: ExecutionOperation): Promise<void> {
    this.provider.operation = operation;
    this.provider.inspection = undefined;
    await this.context.workspaceState.update(lastOperationKey, operation.operationId);
    await this.context.workspaceState.update(pendingStartKey, undefined);
    this.provider.refresh();
    this.schedulePoll();
  }

  private async cancel(): Promise<void> {
    const operation = this.provider.operation;
    if (!operation || isTerminal(operation.status)) {
      return;
    }
    const answer = await vscode.window.showWarningMessage(
      `Cancelar a execução ${operation.taskId}?`,
      { modal: true },
      'Cancelar execução');
    if (answer !== 'Cancelar execução') {
      return;
    }
    try {
      this.provider.operation = await (await this.ensureClient()).request<ExecutionOperation>(
        'execution.cancel', { operationId: operation.operationId });
      this.provider.refresh();
      this.schedulePoll();
    } catch (error) {
      this.report(error, true);
    }
  }

  private async review(decision: 'Approve' | 'Reject'): Promise<void> {
    const inspection = this.provider.inspection;
    if (!inspection) {
      void vscode.window.showErrorMessage('Atualize a execução antes de revisar o candidato.');
      return;
    }
    const policyReference = await vscode.window.showInputBox({
      title: `${decision === 'Approve' ? 'Aprovar' : 'Rejeitar'} candidato AECS`,
      prompt: 'Referência da política de revisão',
      placeHolder: 'policy/team-v1',
      ignoreFocusOut: true,
      validateInput: value => value.trim() ? undefined : 'A referência da política é obrigatória.'
    });
    if (!policyReference) {
      return;
    }
    const justification = await vscode.window.showInputBox({
      title: 'Justificativa auditável',
      prompt: 'Explique a decisão; o texto será persistido na evidência autenticada.',
      ignoreFocusOut: true,
      validateInput: value => value.trim() ? undefined : 'A justificativa é obrigatória.'
    });
    if (!justification) {
      return;
    }
    if (decision === 'Approve') {
      const confirmation = await vscode.window.showWarningMessage(
        'Esta ação registra a aprovação no backend, mas não aplica o patch. A promoção continua exigindo confirmação separada pelo AECS.',
        { modal: true },
        'Registrar aprovação');
      if (confirmation !== 'Registrar aprovação') {
        return;
      }
    }
    try {
      const result = await (await this.ensureClient()).request<ReviewResult>('review.submit', {
        operationId: inspection.operation.operationId,
        expectedDiffHash: inspection.candidate.diffHash,
        decision,
        justification,
        policyReference,
        validMinutes: 15
      });
      if (!result.persisted) {
        throw new Error(`O backend recusou persistir a revisão: ${result.message}`);
      }
      await this.refresh(false);
      void vscode.window.showInformationMessage(
        `Revisão ${result.status} persistida pelo AECS (${result.evidence.id}).`);
    } catch (error) {
      this.report(error, true);
    }
  }

  private async openDiff(): Promise<void> {
    const inspection = this.provider.inspection;
    if (!inspection) {
      return;
    }
    this.diffProvider.setDiff(inspection.candidate.diff);
    const uri = vscode.Uri.parse(
      `aecs-diff:/${inspection.operation.operationId}.diff?${Date.now()}`);
    const document = await vscode.workspace.openTextDocument(uri);
    await vscode.languages.setTextDocumentLanguage(document, 'diff');
    await vscode.window.showTextDocument(document, { preview: true });
  }

  private schedulePoll(): void {
    if (this.pollTimer) {
      clearTimeout(this.pollTimer);
    }
    const interval = vscode.workspace.getConfiguration('aecs', this.repository?.uri)
      .get<number>('refreshIntervalMs', 2000);
    this.pollTimer = setTimeout(() => {
      if (!this.disposed) {
        void this.refresh(false);
      }
    }, interval);
  }

  private async ensureClient(): Promise<ProtocolClient> {
    if (this.client && !this.client.isClosed) {
      return this.client;
    }
    if (!this.repository) {
      throw new Error('Nenhuma pasta de repositório está aberta.');
    }
    const config = vscode.workspace.getConfiguration('aecs', this.repository.uri);
    const configuredPath = expandWorkspace(
      config.get<string>('backendPath', ''),
      this.repository.uri.fsPath);
    if (!configuredPath) {
      const action = await vscode.window.showErrorMessage(
        'Configure aecs.backendPath com o executável AECS ou AECS.Cli.dll.',
        'Abrir configurações');
      if (action === 'Abrir configurações') {
        await vscode.commands.executeCommand('workbench.action.openSettings', 'aecs.backendPath');
      }
      throw new Error('aecs.backendPath não está configurado.');
    }

    const repositoryHash = createHash('sha256')
      .update(this.repository.uri.toString())
      .digest('hex');
    const stateUri = vscode.Uri.joinPath(this.context.globalStorageUri, repositoryHash);
    await vscode.workspace.fs.createDirectory(stateUri);
    const tokenKey = `aecs.protocolToken.${repositoryHash}`;
    let token = await this.context.secrets.get(tokenKey);
    if (!token) {
      token = randomBytes(32).toString('base64url');
      await this.context.secrets.store(tokenKey, token);
    }

    const backendPath = path.resolve(configuredPath);
    const executable = backendPath.toLowerCase().endsWith('.dll') ? 'dotnet' : backendPath;
    const backendArguments = backendPath.toLowerCase().endsWith('.dll') ? [backendPath] : [];
    backendArguments.push(
      'vscode-server',
      '--repo', this.repository.uri.fsPath,
      '--state-dir', stateUri.fsPath);
    const evidenceStore = config.get<string>('evidenceStore', 'runtime');
    if (evidenceStore !== 'runtime') {
      backendArguments.push('--evidence-store', evidenceStore);
    }
    const runtimeConfig = expandWorkspace(
      config.get<string>('runtimeConfig', ''),
      this.repository.uri.fsPath);
    if (runtimeConfig) {
      backendArguments.push('--runtime-config', runtimeConfig);
    }
    if (config.get<string>('agentMode', 'runtime') === 'mock') {
      backendArguments.push('--mock');
    }
    if (config.get<boolean>('allowHostExecution', false)) {
      backendArguments.push('--allow-host-execution');
    }
    const evidenceRoot = expandWorkspace(
      config.get<string>('evidenceRoot', ''),
      this.repository.uri.fsPath);
    if (evidenceRoot) {
      backendArguments.push('--evidence-root', evidenceRoot);
    }
    const keyDirectory = expandWorkspace(
      config.get<string>('keyDirectory', ''),
      this.repository.uri.fsPath);
    if (keyDirectory) {
      backendArguments.push('--key-directory', keyDirectory);
    }

    this.output.appendLine(`Iniciando backend AECS para ${this.repository.uri.fsPath}`);
    const transport = new ProcessLineTransport({
      executable,
      arguments: backendArguments,
      workingDirectory: this.repository.uri.fsPath,
      environment: { ...process.env, [tokenEnvironmentVariable]: token },
      onDiagnostic: line => this.output.appendLine(line)
    });
    const client = new ProtocolClient(transport, token);
    try {
      await client.request('initialize', {});
    } catch (error) {
      client.dispose();
      throw error;
    }
    this.client = client;
    return client;
  }

  private restartClient(): void {
    this.client?.dispose();
    this.client = undefined;
    if (this.provider.operation && !isTerminal(this.provider.operation.status)) {
      this.schedulePoll();
    }
  }

  private report(error: unknown, showToUser: boolean): void {
    const message = error instanceof Error ? error.message : String(error);
    this.output.appendLine(message);
    if (showToUser) {
      void vscode.window.showErrorMessage(`AECS: ${message}`, 'Ver saída').then(action => {
        if (action === 'Ver saída') {
          this.output.show(true);
        }
      });
    }
  }
}

export function activate(context: vscode.ExtensionContext): void {
  const controller = new AecsController(context);
  context.subscriptions.push(controller);
  void controller.restore();
}

export function deactivate(): void {
  // Disposables registered in the extension context close the authenticated stdio session.
}

function leaf(label: string, description?: string, icon = 'circle-small-filled'): AecsNode {
  return new AecsNode(label, description, [], undefined, undefined, new vscode.ThemeIcon(icon));
}

function isTerminal(status: string): boolean {
  return ['Completed', 'Cancelled', 'Failed', 'Interrupted'].includes(status);
}

function statusIcon(status: string): string {
  switch (status) {
    case 'Completed': return 'pass-filled';
    case 'Cancelled': return 'circle-slash';
    case 'Failed': return 'error';
    case 'Interrupted': return 'debug-disconnect';
    default: return 'loading~spin';
  }
}

function formatCost(value: number): string {
  return Number.isFinite(value) ? `USD ${value.toFixed(6)}` : 'indisponível';
}

function expandWorkspace(value: string, workspacePath: string): string {
  return value.trim().replaceAll('${workspaceFolder}', workspacePath);
}
