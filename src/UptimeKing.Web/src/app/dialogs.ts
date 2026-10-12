import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { UptimeService } from './uptime.service';
import { Monitor, MonitorInput, Check } from './models';

const imports = [
  CommonModule,
  FormsModule,
  MatDialogModule,
  MatButtonModule,
  MatFormFieldModule,
  MatInputModule,
  MatSelectModule,
];
@Component({
  selector: 'uk-monitor-dialog',
  imports,
  template: ` <h2 mat-dialog-title>
      {{ original ? 'Edit monitor' : 'Add monitor' }}
    </h2>
    <form (ngSubmit)="save()" #form="ngForm">
      <mat-dialog-content>
        <p class="dialog-intro">Monitor a public HTTP or HTTPS endpoint.</p>
        <mat-form-field appearance="outline"
          ><mat-label>Monitor name</mat-label
          ><input
            matInput
            name="name"
            [(ngModel)]="input.name"
            required
            maxlength="100"
            placeholder="Client website"
        /></mat-form-field>
        <mat-form-field appearance="outline"
          ><mat-label>Website URL</mat-label
          ><input
            matInput
            name="url"
            [(ngModel)]="input.url"
            required
            type="url"
            placeholder="https://example.com"
        /></mat-form-field>
        <mat-form-field appearance="outline"
          ><mat-label>Client</mat-label
          ><input
            matInput
            name="client"
            [(ngModel)]="input.client"
            required
            maxlength="100"
            placeholder="Client or project name"
        /></mat-form-field>
        <div class="form-columns">
          <mat-form-field appearance="outline"
            ><mat-label>Check every</mat-label
            ><mat-select name="interval" [(ngModel)]="input.intervalSeconds"
              ><mat-option [value]="30">30 seconds</mat-option
              ><mat-option [value]="60">1 minute</mat-option
              ><mat-option [value]="300">5 minutes</mat-option
              ><mat-option [value]="900">15 minutes</mat-option
              ><mat-option [value]="3600">1 hour</mat-option></mat-select
            ></mat-form-field
          >
          <mat-form-field appearance="outline"
            ><mat-label>Slow above (ms)</mat-label
            ><input
              matInput
              type="number"
              name="threshold"
              [(ngModel)]="input.slowThresholdMs"
              min="100"
              max="10000"
              required
          /></mat-form-field>
        </div>
        <p class="form-note">
          <i class="ph ph-shield-check" aria-hidden="true"></i> Two failed checks open an incident.
          One successful check resolves it.
        </p>
        @if (error()) {
          <p class="form-error" role="alert">{{ error() }}</p>
        }</mat-dialog-content
      ><mat-dialog-actions align="end"
        ><button mat-button type="button" mat-dialog-close>Cancel</button
        ><button mat-flat-button type="submit" [disabled]="form.invalid || busy()">
          {{ busy() ? 'Saving…' : original ? 'Save changes' : 'Create monitor' }}
        </button></mat-dialog-actions
      >
    </form>`,
})
export class MonitorDialog {
  readonly api = inject(UptimeService);
  readonly ref = inject(MatDialogRef<MonitorDialog>);
  readonly original = inject<Monitor | null>(MAT_DIALOG_DATA);
  input: MonitorInput = this.original
    ? { ...this.original }
    : { name: '', url: '', client: '', intervalSeconds: 60, slowThresholdMs: 1500 };
  error = signal('');
  busy = signal(false);
  async save() {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.api.saveMonitor(this.input, this.original?.id);
      this.ref.close(true);
    } catch (e) {
      this.error.set(this.api.message(e));
    } finally {
      this.busy.set(false);
    }
  }
}
@Component({
  selector: 'uk-login-dialog',
  imports,
  template: ` <h2 mat-dialog-title>Sign in</h2>
    <form (ngSubmit)="login()" #form="ngForm">
      <mat-dialog-content>
        <p class="dialog-intro">
          Enter your workspace password to manage monitors and notifications.
        </p>
        <mat-form-field appearance="outline"
          ><mat-label>Workspace password</mat-label
          ><input
            matInput
            type="password"
            name="password"
            [(ngModel)]="password"
            autocomplete="current-password"
            required
        /></mat-form-field>
        @if (error()) {
          <p role="alert" class="form-error">{{ error() }}</p>
        }
        <p class="form-note">
          For a local installation, use the password in your .env file.
        </p> </mat-dialog-content
      ><mat-dialog-actions align="end"
        ><button type="button" mat-button mat-dialog-close>Cancel</button
        ><button mat-flat-button type="submit" [disabled]="form.invalid || busy()">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button></mat-dialog-actions
      >
    </form>`,
})
export class LoginDialog {
  api = inject(UptimeService);
  ref = inject(MatDialogRef<LoginDialog>);
  password = '';
  error = signal('');
  busy = signal(false);
  async login() {
    this.busy.set(true);
    try {
      await this.api.login(this.password);
      this.ref.close(true);
    } catch (e) {
      this.error.set(this.api.message(e));
    } finally {
      this.busy.set(false);
    }
  }
}
@Component({
  selector: 'uk-confirm-dialog',
  imports,
  template: `<h2 mat-dialog-title>Delete {{ data.name }}?</h2>
    <mat-dialog-content
      ><p>
        This removes the monitor, its check history and incidents. This can't be undone.
      </p></mat-dialog-content
    ><mat-dialog-actions align="end"
      ><button mat-button mat-dialog-close>Keep monitor</button
      ><button mat-flat-button [mat-dialog-close]="true">Delete monitor</button></mat-dialog-actions
    >`,
})
export class ConfirmDialog {
  data = inject<Monitor>(MAT_DIALOG_DATA);
}
@Component({
  selector: 'uk-detail-dialog',
  imports,
  template: ` <h2 mat-dialog-title>{{ monitor.name }}</h2>
    <mat-dialog-content
      ><p class="dialog-intro">{{ monitor.url }}</p>
      <div class="detail-stats">
        <div>
          <span>Client</span><strong>{{ monitor.client }}</strong>
        </div>
        <div>
          <span>Uptime · 30 days</span
          ><strong>{{ monitor.uptime === null ? '—' : monitor.uptime + '%' }}</strong>
        </div>
        <div>
          <span>Check interval</span><strong>{{ monitor.intervalSeconds }} seconds</strong>
        </div>
      </div>
      <h3>Recent checks</h3>
      @if (api.data()?.isDemo) {
        <p class="form-note">
          Sample history, grouped by hour. Sign in to see individual checks for your sites.
        </p>
      }
      @if (error()) {
        <p class="form-error" role="alert">{{ error() }}</p>
      }
      @if (loading()) {
        <p>Loading check history…</p>
      }
      <div class="history-list">
        @for (check of checks(); track check.checkedAt) {
          <div class="history-row">
            <span>{{ check.checkedAt | date: 'MMM d, HH:mm:ss' }}</span
            ><span [class]="check.success ? 'text-green' : 'text-red'">{{
              check.success ? 'Successful' : 'Failed'
            }}</span
            ><span>{{ check.statusCode ?? '—' }}</span
            ><strong>{{ check.responseMs | number: '1.0-0' }} ms</strong>
          </div>
        } @empty {
          @if (!loading()) {
            <p>No checks yet. The first check will run shortly.</p>
          }
        }
      </div> </mat-dialog-content
    ><mat-dialog-actions align="end"
      ><button mat-button mat-dialog-close>Close</button></mat-dialog-actions
    >`,
})
export class DetailDialog {
  monitor = inject<Monitor>(MAT_DIALOG_DATA);
  api = inject(UptimeService);
  checks = signal<Check[]>([]);
  loading = signal(true);
  error = signal('');
  constructor() {
    void this.load();
  }
  async load() {
    try {
      this.checks.set(
        this.api.data()?.isDemo
          ? this.monitor.history.map((h, i) => ({
              id: i,
              checkedAt: h.at,
              success: h.status === 'up',
              statusCode: null,
              responseMs: h.averageMs,
              error: null,
            }))
          : await this.api.history(this.monitor.id),
      );
    } catch (e) {
      this.error.set(this.api.message(e));
    } finally {
      this.loading.set(false);
    }
  }
}
