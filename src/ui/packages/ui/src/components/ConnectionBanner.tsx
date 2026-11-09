import { formatDateTime, useCulture, useT } from '@watchpoint/i18n';
import { RefreshCw, WifiOff } from 'lucide-react';
import { cn } from '../utils.ts';

export type ConnectionStatus = 'connected' | 'connecting' | 'reconnecting' | 'disconnected';

export interface ConnectionBannerProps {
  status: ConnectionStatus;
  lastUpdateAt?: Date | string;
  className?: string;
}

export function ConnectionBanner({ status, lastUpdateAt, className }: ConnectionBannerProps) {
  const t = useT('app');
  const { culture } = useCulture();

  if (status === 'connected') {
    return null;
  }

  const lastUpdateFormatted = lastUpdateAt
    ? formatDateTime(lastUpdateAt, { culture, timeZone: 'UTC', style: 'short' })
    : undefined;

  return (
    <div
      role="alert"
      data-testid="connection-banner"
      data-status={status}
      className={cn(
        'w-full py-2 px-4 flex items-center justify-between text-sm font-medium transition-colors select-none',
        status === 'disconnected' && 'bg-wp-state-alarm-bg text-wp-state-alarm-fg border-b border-wp-state-alarm',
        status === 'reconnecting' && 'bg-wp-state-warning-bg text-wp-state-warning-fg border-b border-wp-state-warning',
        status === 'connecting' && 'bg-wp-surface-raised text-wp-text-muted border-b border-wp-border',
        className,
      )}
    >
      <div className="flex items-center gap-2">
        {status === 'disconnected' && <WifiOff className="w-4 h-4 shrink-0" aria-hidden="true" />}
        {status === 'reconnecting' && <RefreshCw className="w-4 h-4 shrink-0 animate-spin" aria-hidden="true" />}
        {status === 'connecting' && <RefreshCw className="w-4 h-4 shrink-0 animate-spin" aria-hidden="true" />}
        <span>{t(`connection.${status}`)}</span>
      </div>

      {lastUpdateFormatted && (
        <span className="text-xs opacity-80">{t('connection.lastUpdateAt', { time: lastUpdateFormatted })}</span>
      )}
    </div>
  );
}
