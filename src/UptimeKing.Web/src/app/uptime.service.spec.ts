import { afterEach, describe, expect, it, vi } from 'vitest';
import { UptimeService } from './uptime.service';
import { Dashboard } from './models';

const realtime = vi.hoisted(() => ({
  hubs: [] as {
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    events: Record<string, () => void>;
    closed?: () => void;
  }[],
  failures: 0,
}));
vi.mock('@microsoft/signalr', () => ({
  LogLevel: { Error: 4 },
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const hub = {
        events: {} as Record<string, () => void>,
        closed: undefined as (() => void) | undefined,
        start: vi.fn(async () => {
          if (realtime.failures-- > 0) throw new Error('Unavailable');
        }),
        stop: vi.fn(async () => {
          hub.closed?.();
        }),
        on(name: string, callback: () => void) {
          hub.events[name] = callback;
        },
        onclose(callback: () => void) {
          hub.closed = callback;
        },
        onreconnecting() {},
        onreconnected() {},
      };
      realtime.hubs.push(hub);
      return hub;
    }
  },
}));
const instances: UptimeService[] = [];
function createService() {
  const service = new UptimeService();
  instances.push(service);
  return service;
}

const dashboard = (isDemo: boolean) => ({ isDemo, monitors: [] }) as unknown as Dashboard;
const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });

describe('UptimeService', () => {
  afterEach(() => {
    instances.splice(0).forEach((service) => service.ngOnDestroy());
    realtime.hubs = [];
    realtime.failures = 0;
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('returns an expired session to the public demo so the operator can sign in again', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(json({}, 401))
      .mockResolvedValueOnce(json(dashboard(true)));
    vi.stubGlobal('fetch', fetch);
    const service = createService();
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
    const service = createService();
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
    const service = createService();
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
    const service = createService();
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
    await createService().request('/api/monitors/one/pause', 'POST', { paused: true });
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
    await expect(createService().request('/api/dashboard')).rejects.toThrow(message);
  });
  it('retries an initial session network failure without reloading the page', async () => {
    const fetch = vi
      .fn()
      .mockRejectedValueOnce(new Error('Offline'))
      .mockResolvedValueOnce(json({ authenticated: false }))
      .mockResolvedValueOnce(json(dashboard(true)));
    vi.stubGlobal('fetch', fetch);
    const service = createService();
    await service.init();
    expect(service.error()).toBe('Offline');
    await service.init();
    expect(service.data()?.isDemo).toBe(true);
    expect(service.error()).toBe('');
  });

  it('does not reconnect privately if the initial dashboard request expires the session', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(json({ authenticated: true }))
        .mockResolvedValueOnce(json({}, 401))
        .mockResolvedValueOnce(json(dashboard(true))),
    );
    const service = createService();
    await service.init();
    expect(service.authenticated()).toBe(false);
    expect(service.connection()).toBe('demo');
    expect(realtime.hubs).toHaveLength(0);
  });

  it('a delayed 401 from an earlier session cannot sign out a new login', async () => {
    let resolveOld!: (value: Response) => void;
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockImplementationOnce(
          () =>
            new Promise<Response>((resolve) => {
              resolveOld = resolve;
            }),
        )
        .mockResolvedValueOnce(new Response(null, { status: 204 }))
        .mockResolvedValueOnce(json(dashboard(false))),
    );
    const service = createService();
    service.authenticated.set(true);
    const old = service.refresh();
    await service.login('new-session');
    resolveOld(json({}, 401));
    await old;
    expect(service.authenticated()).toBe(true);
    expect(service.data()?.isDemo).toBe(false);
    expect(service.connection()).toBe('live');
  });

  it('retries a failed initial SignalR connection on the polling interval', async () => {
    vi.useFakeTimers();
    realtime.failures = 1;
    const fetch = vi.fn().mockResolvedValue(json(dashboard(false)));
    fetch.mockResolvedValueOnce(json({ authenticated: true }));
    vi.stubGlobal('fetch', fetch);
    const service = createService();
    await service.init();
    expect(service.connection()).toBe('offline');
    await vi.advanceTimersByTimeAsync(30000);
    expect(service.connection()).toBe('live');
    expect(realtime.hubs).toHaveLength(2);
  });

  it('coalesces a burst of live update hints into one dashboard fetch', async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValue(json(dashboard(false)));
    fetch.mockResolvedValueOnce(json({ authenticated: true }));
    vi.stubGlobal('fetch', fetch);
    const service = createService();
    await service.init();
    fetch.mockClear();
    for (let i = 0; i < 8; i++) realtime.hubs[0].events['dashboardChanged']();
    await vi.advanceTimersByTimeAsync(250);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('clears private data immediately when signing out and ignores stale private responses', async () => {
    let resolveOld!: (value: Response) => void;
    let resolveDemo!: (value: Response) => void;
    let demoStarted!: () => void;
    const demoRequest = new Promise<void>((resolve) => {
      demoStarted = resolve;
    });
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockImplementationOnce(
          () =>
            new Promise<Response>((resolve) => {
              resolveOld = resolve;
            }),
        )
        .mockResolvedValueOnce(new Response(null, { status: 204 }))
        .mockImplementationOnce(() => {
          demoStarted();
          return new Promise<Response>((resolve) => {
            resolveDemo = resolve;
          });
        }),
    );
    const service = createService();
    service.authenticated.set(true);
    service.data.set(dashboard(false));
    const old = service.refresh();
    const logout = service.logout();
    await demoRequest;
    expect(service.data()).toBeNull();
    resolveOld(json(dashboard(false)));
    await old;
    expect(service.data()).toBeNull();
    resolveDemo(json(dashboard(true)));
    await logout;
    expect(service.data()?.isDemo).toBe(true);
  });
});
