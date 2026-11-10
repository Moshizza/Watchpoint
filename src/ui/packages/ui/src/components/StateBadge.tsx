import { useT } from '@watchpoint/i18n';
import { AlertCircle, AlertTriangle, Ban, CheckCircle, HelpCircle, PowerOff, Wrench, Zap } from 'lucide-react';
import type { ElementType } from 'react';
import { cn } from '../utils.ts';

export type OverallState =
  'alarm' | 'fault' | 'warning' | 'normal-active' | 'normal' | 'maintenance' | 'disabled' | 'offline' | 'unknown';

export const STATE_ICONS: Record<OverallState, ElementType> = {
  alarm: AlertCircle,
  fault: AlertTriangle,
  warning: AlertTriangle,
  'normal-active': Zap,
  normal: CheckCircle,
  maintenance: Wrench,
  disabled: Ban,
  offline: PowerOff,
  unknown: HelpCircle,
};

export interface StateBadgeProps {
  state: OverallState;
  showText?: boolean;
  className?: string;
}

export function StateBadge({ state, showText = true, className }: StateBadgeProps) {
  const t = useT('common');
  const Icon = STATE_ICONS[state] ?? HelpCircle;
  const label = t(`states.${state}`);

  return (
    <span
      role="status"
      aria-label={label}
      data-testid="state-badge"
      data-state={state}
      className={cn(
        'inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium border border-transparent select-none',
        state === 'alarm' && 'bg-wp-state-alarm-bg text-wp-state-alarm-fg border-wp-state-alarm',
        state === 'fault' && 'bg-wp-state-fault-bg text-wp-state-fault-fg border-wp-state-fault',
        state === 'warning' && 'bg-wp-state-warning-bg text-wp-state-warning-fg border-wp-state-warning',
        state === 'normal-active' &&
          'bg-wp-state-normal-active-bg text-wp-state-normal-active-fg border-wp-state-normal-active',
        state === 'normal' && 'bg-wp-state-normal-bg text-wp-state-normal-fg border-wp-state-normal',
        state === 'maintenance' &&
          'bg-wp-state-maintenance-bg text-wp-state-maintenance-fg border-wp-state-maintenance',
        state === 'disabled' && 'bg-wp-state-disabled-bg text-wp-state-disabled-fg border-wp-state-disabled',
        state === 'offline' && 'bg-wp-state-offline-bg text-wp-state-offline-fg border-wp-state-offline',
        state === 'unknown' && 'bg-wp-state-unknown-bg text-wp-state-unknown-fg border-wp-state-unknown',
        className,
      )}
    >
      <Icon className="w-3.5 h-3.5 shrink-0" aria-hidden="true" />
      {showText && <span>{label}</span>}
    </span>
  );
}

export interface StateDotProps {
  state: OverallState;
  className?: string;
}

export function StateDot({ state, className }: StateDotProps) {
  const t = useT('common');
  const label = t(`states.${state}`);

  return (
    <span
      role="status"
      aria-label={label}
      data-testid="state-dot"
      data-state={state}
      className={cn(
        'inline-block w-2.5 h-2.5 rounded-full shrink-0',
        state === 'alarm' && 'bg-wp-state-alarm',
        state === 'fault' && 'bg-wp-state-fault',
        state === 'warning' && 'bg-wp-state-warning',
        state === 'normal-active' && 'bg-wp-state-normal-active',
        state === 'normal' && 'bg-wp-state-normal',
        state === 'maintenance' && 'bg-wp-state-maintenance',
        state === 'disabled' && 'bg-wp-state-disabled',
        state === 'offline' && 'bg-wp-state-offline',
        state === 'unknown' && 'bg-wp-state-unknown',
        className,
      )}
    />
  );
}
