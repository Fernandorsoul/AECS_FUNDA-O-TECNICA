import test from 'node:test';
import assert from 'node:assert/strict';
import {
  BackendProtocolError,
  LineTransport,
  ProtocolClient,
  protocolVersion
} from '../src/protocolClient';

const token = 'test-session-token-with-at-least-32-characters';

class MemoryTransport implements LineTransport {
  readonly requests: Array<Record<string, unknown>> = [];
  private lineListener: ((line: string) => void) | undefined;
  private closeListener: ((error: Error) => void) | undefined;

  send(line: string): void {
    const request = JSON.parse(line) as Record<string, unknown>;
    this.requests.push(request);
    const method = String(request.method);
    const failed = method === 'review.submit' &&
      (request.parameters as { decision?: string }).decision === 'Reject';
    const response = failed
      ? {
          protocol: protocolVersion,
          id: request.id,
          ok: false,
          error: { code: 'review_rejected', message: 'Review was not persisted.' }
        }
      : {
          protocol: protocolVersion,
          id: request.id,
          ok: true,
          result: { method, persisted: method === 'review.submit' }
        };
    setImmediate(() => this.lineListener?.(JSON.stringify(response)));
  }

  onLine(listener: (line: string) => void): void {
    this.lineListener = listener;
  }

  onClose(listener: (error: Error) => void): void {
    this.closeListener = listener;
  }

  dispose(): void {
    this.closeListener?.(new Error('disposed'));
  }
}

test('client sends authenticated versioned main flow with correlated responses', async () => {
  const transport = new MemoryTransport();
  const client = new ProtocolClient(transport, token, 1000);

  const initialized = await client.request<{ method: string }>('initialize', {});
  const started = await client.request<{ method: string }>('execution.start', {
    clientRequestId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    taskFile: 'task.yaml'
  });
  const inspected = await client.request<{ method: string }>('execution.inspect', {
    operationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
  });
  const reviewed = await client.request<{ persisted: boolean }>('review.submit', {
    operationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    expectedDiffHash: 'sha256:abc',
    decision: 'Approve',
    justification: 'Reviewed',
    policyReference: 'policy/test-v1',
    validMinutes: 15
  });

  assert.equal(initialized.method, 'initialize');
  assert.equal(started.method, 'execution.start');
  assert.equal(inspected.method, 'execution.inspect');
  assert.equal(reviewed.persisted, true);
  assert.deepEqual(transport.requests.map(request => request.protocol),
    Array(4).fill(protocolVersion));
  assert.deepEqual(transport.requests.map(request => request.token),
    Array(4).fill(token));
  assert.equal(new Set(transport.requests.map(request => request.id)).size, 4);
  client.dispose();
});

test('client exposes structured backend errors', async () => {
  const client = new ProtocolClient(new MemoryTransport(), token, 1000);

  await assert.rejects(
    client.request('review.submit', {
      decision: 'Reject'
    }),
    (error: unknown) => error instanceof BackendProtocolError &&
      error.code === 'review_rejected');
  client.dispose();
});
