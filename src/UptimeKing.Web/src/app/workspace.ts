import { Component, computed, inject, signal, HostListener } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { UptimeService } from './uptime.service';
import { Monitor, Incident, NotificationSettings } from './models';
import { MonitorDialog, LoginDialog, ConfirmDialog, DetailDialog } from './dialogs';

@Component({
  selector: 'uk-workspace',
  imports: [
    A11yModule,
    CommonModule,
    FormsModule,
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatDialogModule,
    MatMenuModule,
    MatFormFieldModule,
    MatInputModule,
    MatSlideToggleModule,
    MatSnackBarModule,
    MatTooltipModule,
  ],
  templateUrl: './workspace.html',
})
export class Workspace {
  readonly api = inject(UptimeService);
  readonly dialog = inject(MatDialog);
  readonly snack = inject(MatSnackBar);
  readonly router = inject(Router);
  readonly route = inject(ActivatedRoute);
  readonly view = this.route.snapshot.data['view'] as string;
  readonly title = this.view.charAt(0).toUpperCase() + this.view.slice(1);
  readonly nav = [
    { path: 'overview', icon: 'squares-four', label: 'Overview' },
    { path: 'monitors', icon: 'pulse', label: 'Monitors' },
    { path: 'incidents', icon: 'warning-circle', label: 'Incidents' },
    { path: 'clients', icon: 'users-three', label: 'Clients' },
    { path: 'notifications', icon: 'bell', label: 'Notifications' },
  ];
  search = signal('');
  client = signal('all');
  status = signal('all');
  mobileOpen = signal(false);
  incidentFilter = signal('all');
  settings = signal<NotificationSettings | null>(null);
  settingsError = signal('');
  saving = signal(false);
  webhook = '';
  clients = computed(() =>
    [...new Set(this.api.data()?.monitors.map((m) => m.client) ?? [])].sort(),
  );
  filtered = computed(() =>
    (this.api.data()?.monitors ?? []).filter(
      (m) =>
        (this.client() === 'all' || this.client() === m.client) &&
        (this.status() === 'all' || this.status() === m.status) &&
        `${m.name} ${m.url} ${m.client}`.toLowerCase().includes(this.search().toLowerCase()),
    ),
  );
  incidents = computed(() =>
    (this.api.data()?.incidents ?? []).filter(
      (i) =>
        this.incidentFilter() === 'all' ||
        (this.incidentFilter() === 'open' ? !i.resolvedAt : !!i.resolvedAt),
    ),
  );
  openIncident = computed(() => this.api.data()?.incidents.find((i) => !i.resolvedAt));
  chartMax = computed(
    () =>
      Math.max(400, ...(this.api.data()?.responseHistory.map((p) => p.averageMs) ?? [400])) * 1.15,
  );
  chartPoints = computed(() => {
    const p = this.api.data()?.responseHistory ?? [];
    return p
      .map(
        (v, i) =>
          `${42 + (i * 600) / Math.max(1, p.length - 1)},${175 - (v.averageMs / this.chartMax()) * 150}`,
      )
      .join(' ');
  });
  chartArea = computed(() => (this.chartPoints() ? `42,175 ${this.chartPoints()} 642,175` : ''));
  constructor() {
    void this.api.init().then(() => {
      if (this.view === 'notifications' && this.api.authenticated()) void this.loadSettings();
    });
  }
  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent) {
    const target = event.target as HTMLElement;
    if (
      event.key === '/' &&
      !['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) &&
      !target.isContentEditable &&
      !this.dialog.openDialogs.length
    ) {
      const search = document.getElementById('monitor-search');
      if (search) {
        event.preventDefault();
        search.focus();
      }
    }
    if (event.key === 'Escape') this.mobileOpen.set(false);
  }
  signIn() {
    this.mobileOpen.set(false);
    const ref = this.dialog.open(LoginDialog, { width: '480px', maxWidth: '94vw' });
    ref.afterClosed().subscribe((ok) => {
      if (ok && this.view === 'notifications') void this.loadSettings();
    });
  }
  add(m?: Monitor) {
    if (!this.api.authenticated()) {
      this.signIn();
      return;
    }
    this.dialog.open(MonitorDialog, { width: '540px', maxWidth: '94vw', data: m ?? null });
  }
  detail(m: Monitor) {
    this.dialog.open(DetailDialog, { width: '680px', maxWidth: '94vw', data: m });
  }
  async pause(m: Monitor) {
    await this.action(
      () => this.api.pause(m.id, !m.paused),
      m.paused ? 'Monitoring resumed' : 'Monitor paused',
    );
  }
  remove(m: Monitor) {
    this.dialog
      .open(ConfirmDialog, { width: '440px', data: m })
      .afterClosed()
      .subscribe((ok) => {
        if (ok) void this.action(() => this.api.remove(m.id), 'Monitor deleted');
      });
  }
  async acknowledge(i: Incident) {
    if (!this.api.authenticated()) {
      this.signIn();
      return;
    }
    await this.action(() => this.api.acknowledge(i.id), 'Incident acknowledged');
  }
  async action(work: () => Promise<unknown>, message: string) {
    try {
      await work();
      this.snack.open(message, 'Dismiss', { duration: 4000 });
    } catch (e) {
      this.snack.open(this.api.message(e), 'Dismiss', { duration: 7000 });
    }
  }
  async signOut() {
    await this.action(() => this.api.logout(), 'Signed out');
  }
  hostname(url: string) {
    try {
      return new URL(url).host + (new URL(url).pathname === '/' ? '' : new URL(url).pathname);
    } catch {
      return url;
    }
  }
  initials(name: string) {
    return name
      .split(' ')
      .map((n) => n[0])
      .slice(0, 2)
      .join('')
      .toUpperCase();
  }
  clientMonitors(client: string) {
    return this.api.data()?.monitors.filter((m) => m.client === client) ?? [];
  }
  clientHealthy(client: string) {
    return this.clientMonitors(client).filter((m) => m.status === 'up').length;
  }
  showClient(client: string) {
    void this.router.navigate(['/monitors'], { queryParams: { client } });
  }
  duration(i: Incident) {
    const ms =
      new Date(i.resolvedAt ?? this.api.data()?.asOf ?? Date.now()).getTime() -
      new Date(i.startedAt).getTime();
    const minutes = Math.max(1, Math.round(ms / 60000));
    return minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
  }
  async loadSettings() {
    try {
      this.settings.set(await this.api.settings());
      this.settingsError.set('');
    } catch (e) {
      this.settingsError.set(this.api.message(e));
    }
  }
  async refresh() {
    await this.api.refresh();
    if (this.view === 'notifications' && this.api.authenticated()) await this.loadSettings();
  }
  async saveSettings() {
    const s = this.settings();
    if (!s) return;
    this.saving.set(true);
    try {
      await this.api.request('/api/notifications', 'PUT', {
        email: s.email,
        emailEnabled: s.emailEnabled,
        discordEnabled: s.discordEnabled,
        discordWebhook: this.webhook || null,
      });
      this.webhook = '';
      await this.loadSettings();
      this.snack.open('Notification settings saved', 'Dismiss', { duration: 4000 });
    } catch (e) {
      this.settingsError.set(this.api.message(e));
    } finally {
      this.saving.set(false);
    }
  }
  ngOnInit() {
    const client = this.route.snapshot.queryParamMap.get('client');
    if (client) this.client.set(client);
  }
}
