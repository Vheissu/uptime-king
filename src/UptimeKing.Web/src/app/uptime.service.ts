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
  private refreshTimer?: ReturnType<typeof setInterval>;

  init(): Promise<void> {
    return (this.ready ??= this.start());
  }
  private async start() {
    try {
      const session = await this.request<{ authenticated: boolean }>('/api/session');
      this.authenticated.set(session.authenticated);
      await this.refresh();
      if (session.authenticated) await this.connect();
      this.refreshTimer = setInterval(() => {
        if (this.authenticated()) void this.refresh();
      }, 30000);
    } catch (error) {
      this.error.set(this.message(error));
      this.loading.set(false);
    }
  }
  async request<T = void>(path: string, method = 'GET', body?: unknown): Promise<T> {
    const response = await fetch(path, {
      method,
      credentials: 'same-origin',
      signal: AbortSignal.timeout(15000),
      headers: { 'Content-Type': 'application/json', 'X-UptimeKing': '1' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      if (response.status === 401 && this.authenticated()) {
        this.authenticated.set(false);
        this.data.set(null);
        ++this.refreshVersion;
        await this.hub?.stop();
        this.connection.set('demo');
        await this.refresh();
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
    this.authenticated.set(true);
    await this.refresh();
    await this.connect();
  }
  async logout() {
    await this.request('/api/session', 'DELETE');
    await this.hub?.stop();
    this.authenticated.set(false);
    this.connection.set('demo');
    await this.refresh();
  }
  private async connect() {
    await this.hub?.stop();
    this.connection.set('connecting');
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/dashboard')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Error)
      .build();
    this.hub.on('dashboardChanged', () => void this.refresh());
    this.hub.onreconnecting(() => this.connection.set('connecting'));
    this.hub.onreconnected(() => {
      this.connection.set('live');
      void this.refresh();
    });
    this.hub.onclose(() => this.connection.set('offline'));
    try {
      await this.hub.start();
      this.connection.set('live');
    } catch {
      this.connection.set('offline');
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
    clearInterval(this.refreshTimer);
    void this.hub?.stop();
  }
}
