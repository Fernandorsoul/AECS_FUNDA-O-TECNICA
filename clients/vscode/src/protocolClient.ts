import { ChildProcessWithoutNullStreams, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';

export const protocolVersion = 'aecs.vscode/v1';
const maximumLineLength = 1024 * 1024;

export interface ProtocolError {
  code: string;
  message: string;
}

interface ProtocolResponse<T> {
  protocol: string;
  id: string;
  ok: boolean;
  result?: T;
  error?: ProtocolError;
}

export class BackendProtocolError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
    this.name = 'BackendProtocolError';
  }
}

export interface LineTransport {
  send(line: string): void;
  onLine(listener: (line: string) => void): void;
  onClose(listener: (error: Error) => void): void;
  dispose(): void;
}

export interface BackendLaunchOptions {
  executable: string;
  arguments: string[];
  workingDirectory: string;
  environment: NodeJS.ProcessEnv;
  onDiagnostic: (line: string) => void;
}

export class ProcessLineTransport implements LineTransport {
  private readonly process: ChildProcessWithoutNullStreams;
  private readonly lineListeners: Array<(line: string) => void> = [];
  private readonly closeListeners: Array<(error: Error) => void> = [];
  private stdoutBuffer = '';
  private stderrBuffer = '';
  private closed = false;
  private closeError: Error | undefined;

  constructor(options: BackendLaunchOptions) {
    this.process = spawn(options.executable, options.arguments, {
      cwd: options.workingDirectory,
      env: options.environment,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      shell: false
    });
    this.process.stdout.setEncoding('utf8');
    this.process.stderr.setEncoding('utf8');
    this.process.stdout.on('data', (chunk: string) => this.consumeStdout(chunk));
    this.process.stderr.on('data', (chunk: string) => {
      this.stderrBuffer += chunk;
      const parts = this.stderrBuffer.split(/\r?\n/);
      this.stderrBuffer = parts.pop() ?? '';
      for (const part of parts) {
        if (part.trim()) {
          options.onDiagnostic(part);
        }
      }
    });
    this.process.on('error', error => this.close(error));
    this.process.on('exit', (code, signal) => {
      if (this.stderrBuffer.trim()) {
        options.onDiagnostic(this.stderrBuffer);
      }
      this.close(new Error(`AECS backend exited (code=${code ?? 'none'}, signal=${signal ?? 'none'}).`));
    });
  }

  send(line: string): void {
    if (this.closed || !this.process.stdin.writable) {
      throw new Error('AECS backend transport is closed.');
    }
    this.process.stdin.write(`${line}\n`, 'utf8');
  }

  onLine(listener: (line: string) => void): void {
    this.lineListeners.push(listener);
  }

  onClose(listener: (error: Error) => void): void {
    if (this.closeError) {
      listener(this.closeError);
      return;
    }
    this.closeListeners.push(listener);
  }

  dispose(): void {
    if (this.closed) {
      return;
    }
    this.process.stdin.end();
    const process = this.process;
    const timer = setTimeout(() => {
      if (process.exitCode === null) {
        process.kill();
      }
    }, 1500);
    timer.unref();
  }

  private consumeStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    if (this.stdoutBuffer.length > maximumLineLength * 2) {
      this.close(new Error('AECS backend produced an oversized protocol response.'));
      this.process.kill();
      return;
    }
    const parts = this.stdoutBuffer.split(/\r?\n/);
    this.stdoutBuffer = parts.pop() ?? '';
    for (const line of parts) {
      if (line.length > maximumLineLength) {
        this.close(new Error('AECS backend produced an oversized protocol response.'));
        this.process.kill();
        return;
      }
      if (line.trim()) {
        for (const listener of this.lineListeners) {
          listener(line);
        }
      }
    }
  }

  private close(error: Error): void {
    if (this.closed) {
      return;
    }
    this.closed = true;
    this.closeError = error;
    for (const listener of this.closeListeners) {
      listener(error);
    }
  }
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timeout: NodeJS.Timeout;
}

export class ProtocolClient {
  private readonly pending = new Map<string, PendingRequest>();
  private closedError: Error | undefined;

  constructor(
    private readonly transport: LineTransport,
    private readonly token: string,
    private readonly requestTimeoutMs = 30_000
  ) {
    if (token.length < 32) {
      throw new Error('AECS protocol token must contain at least 32 characters.');
    }
    transport.onLine(line => this.receive(line));
    transport.onClose(error => this.close(error));
  }

  get isClosed(): boolean {
    return this.closedError !== undefined;
  }

  request<T>(method: string, parameters: object): Promise<T> {
    if (this.closedError) {
      return Promise.reject(this.closedError);
    }
    const id = randomUUID();
    const line = JSON.stringify({
      protocol: protocolVersion,
      id,
      method,
      token: this.token,
      parameters
    });
    return new Promise<T>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`AECS request '${method}' timed out.`));
      }, this.requestTimeoutMs);
      this.pending.set(id, {
        resolve: value => resolve(value as T),
        reject,
        timeout
      });
      try {
        this.transport.send(line);
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  dispose(): void {
    this.transport.dispose();
    this.close(new Error('AECS protocol client was disposed.'));
  }

  private receive(line: string): void {
    let response: ProtocolResponse<unknown>;
    try {
      response = JSON.parse(line) as ProtocolResponse<unknown>;
    } catch {
      this.close(new Error('AECS backend returned malformed JSON.'));
      return;
    }
    if (response.protocol !== protocolVersion || typeof response.id !== 'string') {
      this.close(new Error('AECS backend returned an incompatible protocol response.'));
      return;
    }
    const pending = this.pending.get(response.id);
    if (!pending) {
      return;
    }
    clearTimeout(pending.timeout);
    this.pending.delete(response.id);
    if (response.ok) {
      pending.resolve(response.result);
      return;
    }
    pending.reject(new BackendProtocolError(
      response.error?.code ?? 'unknown_error',
      response.error?.message ?? 'AECS backend rejected the request.'
    ));
  }

  private close(error: Error): void {
    if (this.closedError) {
      return;
    }
    this.closedError = error;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.pending.clear();
  }
}
