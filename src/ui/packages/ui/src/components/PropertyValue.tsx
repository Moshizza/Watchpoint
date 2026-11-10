import { formatDateTime, useCulture, useT } from '@watchpoint/i18n';
import { Clock } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '../utils.ts';

export interface PropertyValueProps {
  value: unknown;
  unit?: string;
  isStale?: boolean;
  lastSeen?: Date | string;
  className?: string;
}

export function PropertyValue({ value, unit, isStale = false, lastSeen, className }: PropertyValueProps): ReactNode {
  const t = useT('app');
  const { culture } = useCulture();

  const formattedValue = typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value ?? '—');
  const staleText = t('property.stale');
  const lastSeenText = lastSeen
    ? t('property.lastSeen', {
        time: formatDateTime(lastSeen, { culture, timeZone: 'UTC', style: 'short' }),
      })
    : '';

  return (
    <span
      data-testid="property-value"
      data-stale={isStale}
      className={cn(
        'inline-flex items-center gap-1.5 font-mono text-sm',
        isStale && 'text-wp-text-muted opacity-75',
        className,
      )}
    >
      <span>{formattedValue}</span>
      {unit && <span className="text-wp-text-muted text-xs select-none">{unit}</span>}
      {isStale && (
        <span
          data-testid="stale-marker"
          title={lastSeenText || staleText}
          aria-label={lastSeenText || staleText}
          className="inline-flex items-center text-wp-state-warning cursor-help"
        >
          <Clock className="w-3.5 h-3.5" aria-hidden="true" />
        </span>
      )}
    </span>
  );
}
