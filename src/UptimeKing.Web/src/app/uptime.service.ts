import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Dashboard, MonitorInput, NotificationSettings, Check } from './models';

@Injectable({ providedIn: 'root' })
export class UptimeService {
  readonly data = signal<Dashboard | null>(null);
  readonly authenticated = signal(false);
  readonly connection = signal<'demo' | 'connecting' | 'live' | 'offline'>('demo');
  readonly error = signal('');
  readonly loading = signal(true);
  private hub?: HubConnection;
  private ready?: Promise<void>;
  private refreshVersion = 0;
  private sessionVersion = 0;
  private sessionKnown = false;
  private destroyed = false;
  private connecting?: Promise<void>;
  private changeTimer?: ReturnType<typeof setTimeout>;
  private refreshTimer?: ReturnType<typeof setInterval>;

  init(): Promise<void> {
    return (this.ready ??= this.start());
  }
  private async start() {
    this.refreshTimer ??= setInterval(() => {
      if (!this.sessionKnown) void this.init();
      else if (this.authenticated()) {
        void this.refresh();
        if (this.connection() === 'offline') void this.connect();
      }
    }, 30000);
    const sessionVersion = this.sessionVersion;
    try {
      const session = await this.request<{ authenticated: boolean }>('/api/session');
      if (this.destroyed || sessionVersion !== this.sessionVersion) return;
      this.sessionKnown = true;
      this.setSession(session.authenticated);
      await this.refresh();
      if (this.authenticated()) await this.connect();
    } catch (error) {
      this.ready = undefined;
      this.error.set(this.message(error));
      this.loading.set(false);
    }
  }
  private setSession(authenticated: boolean) {
    ++this.sessionVersion;
    ++this.refreshVersion;
    this.sessionKnown = true;
    this.data.set(null);
    this.authenticated.set(authenticated);
    if (!authenticated) this.connection.set('demo');
  }
  async request<T = void>(path: string, method = 'GET', body?: unknown): Promise<T> {
    const sessionVersion = this.sessionVersion;
    const protectedRequest = path !== '/api/session' && path !== '/api/demo';
    const response = await fetch(path, {
      method,
      credentials: 'same-origin',
      signal: AbortSignal.timeout(15000),
      headers: { 'Content-Type': 'application/json', 'X-UptimeKing': '1' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      if (
        response.status === 401 &&
        protectedRequest &&
        this.authenticated() &&
        sessionVersion === this.sessionVersion
      ) {
        this.setSession(false);
        const expiredVersion = this.sessionVersion;
        await this.hub?.stop();
        if (expiredVersion === this.sessionVersion) await this.refresh();
      }
      const detail = await response.json().catch(() => ({}));
      throw new Error(
        detail.error ||
          detail.detail ||
          (response.status === 401
            ? 'Sign in to manage your workspace.'
            : response.status === 429
              ? 'Too many attempts. Try again in a minute.'
              : 'Something went wrong. Please try again.'),
      );
    }
    if (protectedRequest && sessionVersion !== this.sessionVersion)
      throw new Error('Your session changed. Please try again.');
    return response.status === 204 ? (undefined as T) : response.json();
  }
  async refresh() {
    const version = ++this.refreshVersion;
    try {
      const data = await this.request<Dashboard>(
        this.authenticated() ? '/api/dashboard' : '/api/demo',
      );
      if (version === this.refreshVersion) {
        this.data.set(data);
        this.error.set('');
      }
    } catch (error) {
      if (version === this.refreshVersion) this.error.set(this.message(error));
    } finally {
      this.loading.set(false);
    }
  }
  async login(password: string) {
    await this.request('/api/session', 'POST', { password });
    this.setSession(true);
    await this.refresh();
    if (this.authenticated()) await this.connect();
  }
  async logout() {
    await this.request('/api/session', 'DELETE');
    this.setSession(false);
    await this.hub?.stop();
    await this.refresh();
  }
  private connect(): Promise<void> {
    if (this.connecting) return this.connecting;
    this.connecting = this.startConnection().finally(() => {
      this.connecting = undefined;
    });
    return this.connecting;
  }
  private async startConnection() {
    await this.hub?.stop();
    if (!this.authenticated() || this.destroyed) return;
    this.connection.set('connecting');
    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/dashboard')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Error)
      .build();
    this.hub = hub;
    const current = () => this.hub === hub && this.authenticated() && !this.destroyed;
    hub.on('dashboardChanged', () => {
      if (!current() || this.changeTimer) return;
      // A batch of checks emits several hints; fetch one snapshot for that burst.
      this.changeTimer = setTimeout(() => {
        this.changeTimer = undefined;
        if (current()) void this.refresh();
      }, 250);
    });
    hub.onreconnecting(() => {
      if (current()) this.connection.set('connecting');
    });
    hub.onreconnected(() => {
      if (!current()) return;
      this.connection.set('live');
      void this.refresh();
    });
    hub.onclose(() => {
      if (current()) this.connection.set('offline');
    });
    try {
      await hub.start();
      if (current()) this.connection.set('live');
      else await hub.stop();
    } catch {
      if (current()) this.connection.set('offline');
    }
  }
  async saveMonitor(input: MonitorInput, id?: string) {
    await this.request(id ? `/api/monitors/${id}` : '/api/monitors', id ? 'PUT' : 'POST', input);
    await this.refresh();
  }
  async pause(id: string, paused: boolean) {
    await this.request(`/api/monitors/${id}/pause`, 'POST', { paused });
    await this.refresh();
  }
  async remove(id: string) {
    await this.request(`/api/monitors/${id}`, 'DELETE');
    await this.refresh();
  }
  async acknowledge(id: string) {
    await this.request(`/api/incidents/${id}/acknowledge`, 'POST', {});
    await this.refresh();
  }
  settings() {
    return this.request<NotificationSettings>('/api/notifications');
  }
  history(id: string) {
    return this.request<Check[]>(`/api/monitors/${id}/history`);
  }
  message(error: unknown) {
    return error instanceof Error ? error.message : 'Unable to connect. Please try again.';
  }
  ngOnDestroy() {
    this.destroyed = true;
    ++this.refreshVersion;
    clearTimeout(this.changeTimer);
    clearInterval(this.refreshTimer);
    void this.hub?.stop();
  }
}
