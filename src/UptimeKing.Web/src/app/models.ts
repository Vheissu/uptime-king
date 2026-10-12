export type Status = 'up' | 'down' | 'degraded' | 'pending' | 'paused';
export interface HistoryBucket {
  at: string;
  averageMs: number;
  status: Status;
}
export interface Monitor {
  id: string;
  name: string;
  url: string;
  client: string;
  status: Status;
  paused: boolean;
  intervalSeconds: number;
  slowThresholdMs: number;
  lastCheckedAt: string | null;
  lastResponseMs: number | null;
  uptime: number | null;
  checks: number;
  history: HistoryBucket[];
}
export interface Incident {
  id: string;
  monitorId: string;
  monitorName: string;
  client: string;
  startedAt: string;
  resolvedAt: string | null;
  acknowledgedAt: string | null;
  cause: string;
}
export interface Dashboard {
  isDemo: boolean;
  asOf: string;
  monitors: Monitor[];
  incidents: Incident[];
  summary: {
    total: number;
    up: number;
    uptime: number | null;
    averageMs: number | null;
    openIncidents: number;
  };
  responseHistory: { at: string; averageMs: number }[];
}
export interface MonitorInput {
  name: string;
  url: string;
  client: string;
  intervalSeconds: number;
  slowThresholdMs: number;
}
export interface Delivery {
  id: string;
  channel: string;
  kind: string;
  attempts: number;
  deliveredAt: string | null;
  lastError: string | null;
  message: string;
}
export interface NotificationSettings {
  email: string;
  emailEnabled: boolean;
  discordEnabled: boolean;
  hasDiscordWebhook: boolean;
  smtpConfigured: boolean;
  deliveries: Delivery[];
}
export interface Check {
  id: number;
  checkedAt: string;
  success: boolean;
  statusCode: number | null;
  responseMs: number;
  error: string | null;
}
