import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NotificationSettingsComponent } from './notification-settings';
import { NotificationSettings } from './models';
import { UptimeService } from './uptime.service';

const settings: NotificationSettings = {
  email: 'operator@example.test',
  emailEnabled: true,
  discordEnabled: false,
  hasDiscordWebhook: false,
  smtpConfigured: true,
  deliveries: [],
};

describe('Notification settings', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('discards a late private response after sign-out and clears the entered webhook', async () => {
    let resolve!: (settings: NotificationSettings) => void;
    const api = {
      authenticated: signal(true),
      settings: vi.fn(
        () =>
          new Promise<NotificationSettings>((complete) => {
            resolve = complete;
          }),
      ),
      message: (error: Error) => error.message,
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: UptimeService, useValue: api },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(NotificationSettingsComponent);
    fixture.detectChanges();
    fixture.componentInstance.webhook = 'unsaved-secret';
    api.authenticated.set(false);
    fixture.detectChanges();
    resolve(settings);
    await fixture.whenStable();
    expect(fixture.componentInstance.settings()).toBeNull();
    expect(fixture.componentInstance.webhook).toBe('');
    expect(fixture.nativeElement.textContent).not.toContain('operator@example.test');
  });
});
