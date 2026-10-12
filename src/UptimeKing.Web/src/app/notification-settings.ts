import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { UptimeService } from './uptime.service';
import { NotificationSettings } from './models';

@Component({
  selector: 'uk-notification-settings',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  templateUrl: './notification-settings.html',
})
export class NotificationSettingsComponent {
  readonly api = inject(UptimeService);
  private readonly snack = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  readonly refreshVersion = input(0);
  readonly signIn = output<void>();
  readonly settings = signal<NotificationSettings | null>(null);
  readonly settingsError = signal('');
  readonly loading = signal(false);
  readonly saving = signal(false);
  webhook = '';
  private version = 0;

  constructor() {
    effect(() => {
      const authenticated = this.api.authenticated();
      this.refreshVersion();
      ++this.version;
      if (!authenticated) {
        this.settings.set(null);
        this.webhook = '';
        this.settingsError.set('');
        this.loading.set(false);
      } else untracked(() => void this.loadSettings());
    });
  }

  async loadSettings() {
    const version = ++this.version;
    this.loading.set(true);
    try {
      const settings = await this.api.settings();
      if (!this.destroyRef.destroyed && this.api.authenticated() && version === this.version) {
        this.settings.set(settings);
        this.settingsError.set('');
      }
    } catch (e) {
      if (!this.destroyRef.destroyed && version === this.version)
        this.settingsError.set(this.api.message(e));
    } finally {
      if (version === this.version) this.loading.set(false);
    }
  }

  async saveSettings() {
    const settings = this.settings();
    if (!settings || this.saving()) return;
    this.saving.set(true);
    this.settingsError.set('');
    try {
      await this.api.request('/api/notifications', 'PUT', {
        email: settings.email,
        emailEnabled: settings.emailEnabled,
        discordEnabled: settings.discordEnabled,
        discordWebhook: this.webhook || null,
      });
      this.webhook = '';
      if (this.destroyRef.destroyed || !this.api.authenticated()) return;
      await this.loadSettings();
      this.snack.open('Notification settings saved', 'Dismiss', { duration: 4000 });
    } catch (e) {
      if (!this.destroyRef.destroyed) this.settingsError.set(this.api.message(e));
    } finally {
      this.saving.set(false);
    }
  }
}
