import { afterEach, describe, expect, it, vi } from 'vitest';
import { UptimeService } from './uptime.service';
import { Dashboard } from './models';

const dashboard = (isDemo: boolean) => ({ isDemo, monitors: [] }) as unknown as Dashboard;
const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });

describe('UptimeService', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('returns an expired session to the public demo so the operator can sign in again', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({}, 401))
      .mockResolvedValueOnce(json(dashboard(true)));
    vi.stubGlobal('fetch', fetch);
    const service = new UptimeService();
    service.authenticated.set(true);
    service.data.set(dashboard(false));
    await service.refresh();
    expect(service.authenticated()).toBe(false);
    expect(service.data()?.isDemo).toBe(true);
    expect(service.connection()).toBe('demo');
    expect(fetch.mock.calls[1][0]).toBe('/api/demo');
  });

  it('loads public sample data for guests and private data for an authenticated session', async () => {
    const fetch = vi.fn().mockResolvedValue(json(dashboard(true)));
    vi.stubGlobal('fetch', fetch);
    const service = new UptimeService();
    await service.refresh();
    expect(fetch.mock.calls[0][0]).toBe('/api/demo');
    service.authenticated.set(true);
    fetch.mockResolvedValue(json(dashboard(false)));
    await service.refresh();
    expect(fetch.mock.calls[1][0]).toBe('/api/dashboard');
    expect(service.data()?.isDemo).toBe(false);
  });

  it('never lets a slow older refresh overwrite newer workspace data', async () => {
    let resolveOld!: (response: Response) => void;
    const fetch = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            resolveOld = resolve;
          }),
      )
      .mockResolvedValueOnce(json(dashboard(false)));
    vi.stubGlobal('fetch', fetch);
    const service = new UptimeService();
    const old = service.refresh();
    service.authenticated.set(true);
    await service.refresh();
    resolveOld(json(dashboard(true)));
    await old;
    expect(service.data()?.isDemo).toBe(false);
  });

  it('preserves the last good dashboard when the network drops and clears the error on recovery', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json(dashboard(true)))
      .mockRejectedValueOnce(new Error('Connection lost'))
      .mockResolvedValueOnce(json(dashboard(true)));
    vi.stubGlobal('fetch', fetch);
    const service = new UptimeService();
    await service.refresh();
    await service.refresh();
    expect(service.data()?.isDemo).toBe(true);
    expect(service.error()).toBe('Connection lost');
    expect(service.loading()).toBe(false);
    await service.refresh();
    expect(service.error()).toBe('');
  });

  it('includes the CSRF header, same-origin credentials and a bounded request timeout', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetch);
    await new UptimeService().request('/api/monitors/one/pause', 'POST', { paused: true });
    expect(fetch).toHaveBeenCalledWith(
      '/api/monitors/one/pause',
      expect.objectContaining({
        method: 'POST',
        credentials: 'same-origin',
        body: '{"paused":true}',
        headers: { 'Content-Type': 'application/json', 'X-UptimeKing': '1' },
        signal: expect.any(AbortSignal),
      }),
    );
  });

  it.each([
    [401, 'Sign in to manage your workspace.'],
    [429, 'Too many attempts. Try again in a minute.'],
    [502, 'Something went wrong. Please try again.'],
  ])('handles non-JSON error responses (%s)', async (status, message) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Proxy error', { status })));
    await expect(new UptimeService().request('/api/dashboard')).rejects.toThrow(message);
  });
});
